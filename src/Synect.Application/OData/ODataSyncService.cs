using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Ingest;
using Synect.Application.Resilience;
using Synect.Application.Secrets;
using Synect.Application.Sync;
using Synect.Domain.Accounting;
using Synect.Domain.Sync;

namespace Synect.Application.OData;

public enum ODataSyncMode
{
    /// <summary>Full when the last completed full load is older than OData:FullSyncInterval (or there is none), else delta.</summary>
    Auto,
    Full,
    Delta,
}

/// <summary>What happened (or, in a dry run, would happen) to one entity.</summary>
public sealed class ODataEntityReport
{
    public required string Entity { get; init; }
    public string Mode { get; set; } = "";
    public int Listed { get; set; }
    public int New { get; set; }
    public int Changed { get; set; }
    public int Restored { get; set; }
    public int Unchanged { get; set; }

    /// <summary>Full load: stored records absent from the source (marked deleted by the receiver when the export completes).</summary>
    public int MissingInSource { get; set; }

    public int Sent { get; set; }
    public int Packages { get; set; }
    public int Accepted { get; set; }
    public int UnchangedByReceiver { get; set; }
    public int Rejected { get; set; }
    public int MarkedDeleted { get; set; }
    public List<string> Warnings { get; } = [];
}

public sealed class ODataSyncReport
{
    public required bool DryRun { get; init; }
    public string Mode { get; set; } = "";
    public string Profile { get; set; } = "";
    public List<ODataEntityReport> Entities { get; } = [];
    public SortedSet<string> MissingProperties { get; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; } = [];
    public int Requests { get; set; }
    public int Retries { get; set; }
    public long ElapsedMs { get; set; }
    public bool Complete { get; set; } = true;
    public Guid? SyncRunId { get; set; }
    public int Records => Entities.Sum(e => e.Listed);
}

/// <summary>
/// Loads a 1C:Fresh (Clobus) application over the standard OData interface into the 1C tables through the same receiver
/// as the 1C extension (<see cref="IngestService.ProcessPulledAsync"/>), in packages with the connection's id. Strictly
/// one organization (the tenant context) and one connection.
/// <list type="bullet">
/// <item>Full load, per entity: keys and DataVersion of every record, page by page; records that are new, changed (another
/// DataVersion) or were marked deleted by an earlier full export are read in full and sent; the keys of unchanged ones are
/// reported to the receiver, so the completed export marks deleted exactly the records absent from the source (the
/// existing full-export mechanism).</item>
/// <item>Delta: catalogs are compared the same way without marking deletions; documents only within OData:DeltaWindowDays
/// by their date.</item>
/// <item>Dry run: everything is read and compared, nothing is written; the report says what would be sent.</item>
/// </list>
/// Logs carry counters, entity and resource names only, never values, the host or credentials.
/// </summary>
public sealed class ODataSyncService(
    ISynectDbContext db,
    IngestService ingest,
    IngestWriter writer,
    IODataClientFactory clients,
    ISecretStore secrets,
    IOptions<ODataOptions> options,
    IOptions<IngestOptions> ingestOptions,
    IOptions<CircuitBreakerOptions> breakerOptions,
    Queue.SyncShutdown shutdown,
    TimeProvider time,
    ILogger<ODataSyncService> logger)
{
    /// <summary>Provider slot of the sync queue (per-source concurrency) for OData loads.</summary>
    public const string Provider = "OneCFreshOData";

    /// <summary>Extra field of an item: the source's own deletion mark, to tell it from a deletion by a full export.</summary>
    public const string SourceDeletedField = "source_deleted";

    private const int PackageItems = 200;

    private ODataOptions O => options.Value;

    /// <summary>A load by the sync queue: recorded as a sync run; a failure feeds the circuit breaker.</summary>
    public async Task<SyncOutcome> SyncAsync(Guid connectionId, CancellationToken ct)
    {
        var (connection, run) = await db.InTransactionAsync(async token =>
        {
            var c = await LoadConnectionAsync(connectionId, token);
            var r = SyncRun.Start(c.TenantId, c.Id, time.GetUtcNow());
            db.SyncRuns.Add(r);
            await db.SaveChangesAsync(token);
            return (c, r);
        }, ct);

        try
        {
            var report = await RunAsync(connection, ODataSyncMode.Auto, dryRun: false, run.Id, ct);
            await db.InTransactionAsync(async token =>
            {
                var tracked = await db.SyncRuns.SingleAsync(r => r.Id == run.Id, token);
                var c = await db.Connections.SingleAsync(x => x.Id == connectionId, token);
                var now = time.GetUtcNow();
                tracked.Succeed(now, report.Complete, report.Records);
                c.MarkSyncSucceeded(now);
                await db.SaveChangesAsync(token);
            }, ct);
            return new SyncOutcome(run.Id, SyncRunResult.Succeeded, report.Complete, report.Records, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("OData sync failed: connection {ConnectionId}, error type {ErrorType}", connectionId, ex.GetType().Name);
            var outcome = await SyncRunFinalizer.FailAsync(db, run.Id, connectionId, ex, 0, time.GetUtcNow(), breakerOptions.Value.FailureThreshold,
                interruptedByShutdown: ex is OperationCanceledException && shutdown.JobsCancelled);
            if (ex is OperationCanceledException) throw;
            return outcome;
        }
    }

    /// <summary>A load started by hand (DevTools): <paramref name="dryRun"/> writes nothing (no sync run, no packages).</summary>
    public async Task<ODataSyncReport> RunAsync(Guid connectionId, ODataSyncMode mode, bool dryRun, CancellationToken ct)
    {
        var connection = await db.InTransactionAsync(token => LoadConnectionAsync(connectionId, token), ct);
        return await RunAsync(connection, mode, dryRun, null, ct);
    }

    /// <summary>The access test: $metadata and one count with the stored credentials.</summary>
    public async Task<ConnectionTestResult> TestAsync(Guid connectionId, CancellationToken ct)
    {
        try
        {
            var connection = await db.InTransactionAsync(token => LoadConnectionAsync(connectionId, token), ct);
            using var client = await CreateClientAsync(connection, ct);
            var metadata = await client.GetMetadataAsync(ct);
            return metadata.Contains("EntityType", StringComparison.Ordinal)
                ? ConnectionTestResult.Ok()
                : ConnectionTestResult.Failed("The $metadata answer does not look like an OData service description.");
        }
        catch (AdapterException ex)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
        catch (ODataEndpointException ex)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
    }

    private async Task<Connection> LoadConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        var c = await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == connectionId, ct)
                ?? throw new KeyNotFoundException($"Connection {connectionId} not found for the current tenant.");
        if (!c.IsOneCOData) throw new InvalidOperationException($"Connection {connectionId} is not a 1C:Fresh OData connection.");
        if (c.Status == ConnectionStatus.Disabled) throw new InvalidOperationException($"Connection {connectionId} is disabled.");
        return c;
    }

    private async Task<IODataClient> CreateClientAsync(Connection connection, CancellationToken ct)
    {
        var baseUrl = ConnectionKinds.ReadSetting(connection.Settings, "base_url")
                      ?? throw new AdapterException("The OData connection has no base_url in its settings.");
        var root = ODataEndpoint.Normalize(baseUrl, O);
        var secret = await db.InTransactionAsync(token => secrets.GetAsync(connection.Id, ConnectionSecret.CredentialsKind, token), ct)
                     ?? throw new AdapterAuthenticationException("No credentials are stored for the OData connection.");
        var (login, password) = ODataCredentials.Parse(secret);
        return clients.Create(root, login, password);
    }

    private sealed record Remote(string Ref, string? Version, ODataSource Source);

    private async Task<ODataSyncReport> RunAsync(Connection connection, ODataSyncMode mode, bool dryRun, Guid? runId, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var profile = ODataProfileResolver.Resolve(connection.Settings, O);
        var report = new ODataSyncReport { DryRun = dryRun, Profile = profile.Name };
        var tenantId = connection.TenantId;
        var organization = ConnectionKinds.ReadSetting(connection.Settings, "organization_key");
        if (organization is not null && !Guid.TryParse(organization, out _))
            throw new AdapterException("settings.organization_key of the OData connection is not a GUID.");

        if (mode == ODataSyncMode.Auto) mode = await LastFullAsync(connection.Id, ct) is { } last && time.GetUtcNow() - last < O.FullSyncInterval
            ? ODataSyncMode.Delta
            : ODataSyncMode.Full;
        report.Mode = mode == ODataSyncMode.Full ? "full" : "delta";
        var runKey = (runId ?? Guid.CreateVersion7()).ToString("N");
        logger.LogInformation("OData sync started: connection {ConnectionId}, mode {Mode}, dry run {DryRun}, profile {Profile}",
            connection.Id, report.Mode, dryRun, profile.Name);

        using var client = await CreateClientAsync(connection, ct);
        var lookups = new ODataLookupCache();
        await LoadLookupsAsync(client, profile, lookups, report, ct);
        var mapper = new ODataRecordMapper(profile, lookups);

        foreach (var entity in profile.Entities)
        {
            ct.ThrowIfCancellationRequested();
            var spec = IngestSchema.Find(entity.Entity)!;
            var er = new ODataEntityReport { Entity = spec.Name, Mode = report.Mode };
            report.Entities.Add(er);
            var isDocument = entity.Sources.Any(s => s.DateProperty is not null);
            var full = mode == ODataSyncMode.Full;

            // 1. Keys and versions in the source.
            var remote = new Dictionary<string, Remote>(StringComparer.Ordinal);
            var consistent = true;
            foreach (var source in entity.Sources)
            {
                var filter = Filter(source, organization, full || !isDocument || source.DateProperty is null ? null : DeltaFrom());
                var (rows, ok) = await ListKeysAsync(client, profile, source, filter, mapper, er, ct);
                consistent &= ok;
                foreach (var r in rows)
                    if (!remote.TryAdd(r.Ref, r))
                        er.Warnings.Add($"{source.Resource.Name}: ref listed by two resources of {spec.Name}; the first one is used.");
            }
            er.Listed = remote.Count;

            // 2. What is stored (full: every record of the entity; delta: only the listed ones).
            var stored = await db.InTransactionAsync(token => writer.LoadVersionsAsync(spec, tenantId, full ? null : remote.Keys.ToList(), token), ct);
            var sourceDeleted = full || stored.Count > 0
                ? await db.InTransactionAsync(token => LoadSourceDeletedAsync(spec, tenantId, stored.Where(s => s.Value.IsDeleted).Select(s => s.Key).ToList(), token), ct)
                : new HashSet<string>(StringComparer.Ordinal);
            var toRead = new List<Remote>();
            var unchanged = new List<string>();
            foreach (var r in remote.Values)
            {
                if (!stored.TryGetValue(r.Ref, out var s)) { er.New++; toRead.Add(r); continue; }
                if (r.Version is null || s.DataVersion != r.Version) { er.Changed++; toRead.Add(r); continue; }
                // Same version but marked deleted by a full export (not by the source itself): read it again to lift the mark.
                if (s.IsDeleted && !sourceDeleted.Contains(r.Ref)) { er.Restored++; toRead.Add(r); continue; }
                er.Unchanged++;
                unchanged.Add(r.Ref);
            }
            if (full) er.MissingInSource = stored.Values.Count(s => !s.IsDeleted && !remote.ContainsKey(s.Ref));

            if (full && !consistent)
            {
                er.Mode = "delta";
                er.Warnings.Add("The source changed while it was listed (count or duplicate pages); sent as a delta, nothing is marked deleted this time.");
                report.Complete = false;
            }

            // 3. Read the records to send, map them.
            var items = new List<JsonObject>(toRead.Count);
            foreach (var group in toRead.GroupBy(r => r.Source))
            {
                foreach (var chunk in group.Chunk(Math.Max(1, O.RecordsPerKeyRequest)))
                {
                    // TODO(verify-real): a $filter of up to OData:RecordsPerKeyRequest "Ref_Key eq guid'…'" terms joined by "or"
                    // is assumed to be accepted (URL length, server load); the documented alternative is one GET per key.
                    var filter = string.Join(" or ", chunk.Select(r => $"{profile.KeyProperty} eq {ODataLiteral.Guid(r.Ref)}"));
                    var page = await client.GetAsync(new ODataQuery(group.Key.Resource.Name) { Filter = filter }, ct);
                    foreach (var record in page.Items)
                    {
                        var item = mapper.Map(record, group.Key);
                        item[SourceDeletedField] = item[IngestSchema.IsDeleted]?.GetValue<bool>() ?? false;
                        if (item["ref"] is not null) items.Add(item);
                    }
                }
            }
            er.Sent = items.Count;
            if (items.Count < toRead.Count)
                er.Warnings.Add($"{toRead.Count - items.Count} listed records were not returned when read by key (deleted meanwhile?).");

            // 4. Send through the receiver (or count, in a dry run).
            var exportFull = full && consistent;
            var packages = items.Chunk(Math.Min(PackageItems, ingestOptions.Value.MaxItems)).ToList();
            if (packages.Count == 0 && exportFull) packages.Add([]);
            er.Packages = packages.Count;
            if (dryRun) continue;

            var fullSyncId = exportFull ? $"odata-{runKey}" : null;
            var batchId = $"odata-{runKey}-{spec.Name}";
            for (var i = 0; i < packages.Count; i++)
            {
                var body = Envelope(spec.Name, batchId, i + 1, exportFull, fullSyncId, i == packages.Count - 1, packages[i]);
                var response = await ingest.ProcessPulledAsync(connection.Id, body, exportFull && i == 0 ? unchanged : null, ct);
                Account(er, response, spec.Name);
            }
            if (er.Rejected > 0) report.Complete = false;
        }

        foreach (var p in mapper.MissingProperties) report.MissingProperties.Add(p);
        report.Requests = client.Stats.Requests;
        report.Retries = client.Stats.Retries;
        report.ElapsedMs = watch.ElapsedMilliseconds;
        logger.LogInformation(
            "OData sync finished: connection {ConnectionId}, mode {Mode}, dry run {DryRun}, listed {Listed}, sent {Sent}, rejected {Rejected}, requests {Requests}, {ElapsedMs} ms",
            connection.Id, report.Mode, dryRun, report.Records, report.Entities.Sum(e => e.Sent), report.Entities.Sum(e => e.Rejected), report.Requests, report.ElapsedMs);
        if (report.MissingProperties.Count > 0)
            logger.LogWarning("OData sync: {Count} profile properties were absent from the records (check the profile against $metadata): {Properties}",
                report.MissingProperties.Count, string.Join(", ", report.MissingProperties.Take(50)));
        return report;
    }

    private DateOnly DeltaFrom()
    {
        // 1C dates are local (Tashkent); the window is counted in days, so the offset matters little but is applied.
        var local = time.GetUtcNow().ToOffset(ingestOptions.Value.SourceUtcOffset);
        return DateOnly.FromDateTime(local.DateTime).AddDays(-Math.Max(1, O.DeltaWindowDays));
    }

    private static string? Filter(ODataSource source, string? organization, DateOnly? from)
    {
        var parts = new List<string>();
        if (source.Filter is { Value.Length: > 0 } f) parts.Add(f.Value);
        if (organization is not null && source.OrganizationFilter is { Value.Length: > 0 } of)
            parts.Add(of.Value.Replace("{organization}", ODataLiteral.Guid(organization), StringComparison.Ordinal));
        if (from is { } d && source.DateProperty is { } dp) parts.Add($"{ODataUrl.CheckName(dp.Name)} ge {ODataLiteral.DateTime(d)}");
        return parts.Count == 0 ? null : string.Join(" and ", parts.Select(p => parts.Count > 1 ? $"({p})" : p));
    }

    /// <summary>
    /// Keys and versions page by page: $top/$skip with $inlinecount=allpages, ordered by the key (stable). Stops on an empty
    /// or short page, at OData:MaxPages, or when a page brings nothing new (a source that ignores $skip would loop).
    /// Returns false (inconsistent) when the count changed between pages or pages overlapped.
    /// </summary>
    private async Task<(List<Remote> Rows, bool Consistent)> ListKeysAsync(IODataClient client, ODataProfile profile, ODataSource source, string? filter,
        ODataRecordMapper mapper, ODataEntityReport er, CancellationToken ct)
    {
        var rows = new List<Remote>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var consistent = true;
        long? firstCount = null;
        var size = Math.Max(1, O.PageSize);
        for (var page = 0; ; page++)
        {
            if (page >= O.MaxPages)
            {
                er.Warnings.Add($"{source.Resource.Name}: stopped at OData:MaxPages ({O.MaxPages}).");
                return (rows, false);
            }
            var result = await client.GetAsync(new ODataQuery(source.Resource.Name)
            {
                Filter = filter,
                Select = [profile.KeyProperty, profile.VersionProperty],
                // TODO(verify-real): ordering by Ref_Key is assumed to be supported and stable in 1C OData.
                OrderBy = profile.KeyProperty,
                Top = size,
                Skip = page * size,
                InlineCount = true,
            }, ct);
            if (result.Count is { } count)
            {
                firstCount ??= count;
                if (count != firstCount) consistent = false;
            }
            var fresh = 0;
            foreach (var record in result.Items)
            {
                if (mapper.KeyOf(record) is not { } key) continue;
                if (!seen.Add(key))
                {
                    consistent = false;
                    continue;
                }
                fresh++;
                rows.Add(new Remote(key, mapper.VersionOf(record), source));
            }
            if (result.Items.Count == 0 || result.Items.Count < size) break;
            if (fresh == 0)
                throw new ODataProtocolException($"OData {source.Resource.Name}: page {page + 1} repeated earlier records ($skip ignored?); listing stopped.");
            if (firstCount is { } total && (page + 1) * (long)size >= total) break;
        }
        if (firstCount is { } expected && rows.Count != expected) consistent = false;
        return (rows, consistent);
    }

    /// <summary>
    /// Reads every resource used by lookups once, with only the properties the profile takes from it.
    /// TODO(verify-real): $select of Ref_Key plus the lookup properties and $orderby=Ref_Key on catalogs and charts of accounts.
    /// </summary>
    private async Task LoadLookupsAsync(IODataClient client, ODataProfile profile, ODataLookupCache cache, ODataSyncReport report, CancellationToken ct)
    {
        var needed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var source in profile.Entities.SelectMany(e => e.Sources))
        {
            var maps = source.Fields.Values.Concat(source.Children?.Tables.SelectMany(t => t.Fields.Values) ?? []);
            foreach (var l in maps.Select(m => m.Lookup).OfType<ODataLookup>())
            {
                if (!needed.TryGetValue(l.Resource, out var set)) needed[l.Resource] = set = new HashSet<string>(StringComparer.Ordinal);
                set.UnionWith(l.Properties);
            }
        }
        var size = Math.Max(1, O.PageSize);
        foreach (var (resource, properties) in needed)
        {
            try
            {
                var seen = 0;
                for (var page = 0; page < O.MaxPages; page++)
                {
                    var result = await client.GetAsync(new ODataQuery(resource)
                    {
                        Select = [profile.KeyProperty, .. properties],
                        OrderBy = profile.KeyProperty,
                        Top = size,
                        Skip = page * size,
                    }, ct);
                    var fresh = 0;
                    foreach (var record in result.Items)
                    {
                        if (ODataRecordMapper.NormalizeRef(ODataRecordMapper.Get(record, profile.KeyProperty)) is not { } key) continue;
                        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                        foreach (var p in properties)
                            values[p] = ODataRecordMapper.Get(record, p) is { } v
                                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.GetRawText() : null
                                : null;
                        cache.Add(resource, key, values);
                        fresh++;
                    }
                    seen += fresh;
                    if (result.Items.Count < size || fresh == 0) break;
                }
                cache.MarkLoaded(resource);
            }
            catch (ODataProtocolException ex)
            {
                // A lookup resource that is not published only leaves its fields empty; the load goes on.
                report.Warnings.Add($"Lookup {resource} could not be read: {ex.Message}");
                cache.MarkLoaded(resource);
            }
        }
    }

    private async Task<HashSet<string>> LoadSourceDeletedAsync(IngestEntitySpec spec, Guid tenantId, List<string> refs, CancellationToken ct)
    {
        if (refs.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        // The table name comes from the schema only; every value is a bind parameter.
        var sql = "SELECT t.ref AS \"Value\" FROM " + spec.Table + " t WHERE t.tenant_id = {0} AND t.ref = ANY({1}) AND (t.extra->>'" + SourceDeletedField + "') = 'true'";
        var rows = await db.Database.SqlQueryRaw<string>(sql, tenantId, refs.ToArray()).ToListAsync(ct);
        return rows.ToHashSet(StringComparer.Ordinal);
    }

    private async Task<DateTimeOffset?> LastFullAsync(Guid connectionId, CancellationToken ct) =>
        await db.InTransactionAsync(token => db.IngestBatches.AsNoTracking()
            .Where(b => b.ConnectionId == connectionId && b.Mode == IngestEnvelope.ModeFull && b.IsLastBatch
                        && (b.Status == IngestBatchStatus.Processed || b.Status == IngestBatchStatus.ProcessedWithErrors))
            .MaxAsync(b => (DateTimeOffset?)b.ReceivedAt, token), ct);

    private byte[] Envelope(string entity, string batchId, int sequence, bool full, string? fullSyncId, bool last, IEnumerable<JsonObject> items)
    {
        var envelope = new JsonObject
        {
            ["schema_version"] = IngestOptions.DefaultSchemaVersion,
            ["extension_version"] = "synect-odata/1",
            ["organization_tin"] = IngestService.PulledOrganizationTin,
            ["batch_id"] = batchId,
            ["sequence"] = sequence,
            ["exported_at"] = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            ["mode"] = full ? IngestEnvelope.ModeFull : IngestEnvelope.ModeDelta,
            ["entity"] = entity,
            ["items"] = new JsonArray(items.Select(i => (JsonNode)i.DeepClone()).ToArray()),
        };
        if (full)
        {
            envelope["full_sync_id"] = fullSyncId;
            envelope["is_last_batch"] = last;
        }
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }

    private void Account(ODataEntityReport er, IngestResponse response, string entity)
    {
        var json = JsonSerializer.SerializeToElement(response.Body);
        if (response.HttpStatus != 200)
        {
            var code = json.TryGetProperty("error", out var e) ? e.GetString() : null;
            throw new AdapterException($"The receiver refused a package of {entity}: HTTP {response.HttpStatus} {code}.");
        }
        int Int(string name) => json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        er.Accepted += Int("accepted");
        er.UnchangedByReceiver += Int("unchanged");
        er.Rejected += Int("rejected");
        er.MarkedDeleted += Int("marked_deleted");
        if (Int("rejected") > 0 && json.TryGetProperty("items", out var items))
            foreach (var i in items.EnumerateArray().Where(i => i.TryGetProperty("status", out var s) && s.GetString() == IngestItemResult.Rejected).Take(5))
                er.Warnings.Add($"rejected {(i.TryGetProperty("ref", out var r) ? r.GetString() : "?")}: {(i.TryGetProperty("code", out var c) ? c.GetString() : "")}");
    }
}

/// <summary>Login and password stored as the "credentials" secret of the connection: {"login": "...", "password": "..."}.</summary>
public static class ODataCredentials
{
    public static (string Login, SecretValue Password) Parse(SecretValue secret)
    {
        try
        {
            using var doc = JsonDocument.Parse(secret.Reveal());
            var login = doc.RootElement.GetProperty("login").GetString();
            var password = doc.RootElement.GetProperty("password").GetString();
            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(password)) throw new FormatException();
            return (login, new SecretValue(password));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new AdapterAuthenticationException("Stored credentials are malformed: expected login and password.");
        }
    }
}
