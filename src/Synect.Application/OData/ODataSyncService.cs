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

    /// <summary>A full export of this entity was attempted in this load (it may have ended as a delta).</summary>
    public bool FullAttempted { get; set; }

    /// <summary>Active (not deleted) records stored before the load.</summary>
    public int ActiveStored { get; set; }

    /// <summary>The mass-deletion guard held the full export back (nothing marked deleted).</summary>
    public bool MassDeleteBlocked { get; set; }

    /// <summary>A mass deletion was let through by an explicit confirmation (DevTools --confirm-mass-delete).</summary>
    public bool MassDeleteConfirmed { get; set; }

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
    Attention.AttentionTrigger attention,
    TimeProvider time,
    ILogger<ODataSyncService> logger)
{
    /// <summary>Provider slot of the sync queue (per-source concurrency) for OData loads.</summary>
    public const string Provider = "OneCFreshOData";

    /// <summary>Extra field of an item: the source's own deletion mark, to tell it from a deletion by a full export.</summary>
    public const string SourceDeletedField = "source_deleted";

    private const int PackageItems = 200;

    private ODataOptions O => options.Value;

    /// <summary>A load by the sync queue: recorded as a sync run; a failure feeds the circuit breaker. Never forces a mass deletion.</summary>
    public async Task<SyncOutcome> SyncAsync(Guid connectionId, CancellationToken ct)
    {
        var (outcome, _, _) = await RecordedAsync(connectionId, ODataSyncMode.Auto, null, ct);
        return outcome;
    }

    /// <summary>
    /// A load started by hand (DevTools). <paramref name="dryRun"/> writes nothing (no sync run, no packages). A real load
    /// is recorded as a sync run like a queued one (circuit breaker, attention rules). <paramref name="confirmedMassDeleteBy"/>
    /// (DevTools --confirm-mass-delete only): who confirmed that a full export may mark deleted more than the guard allows;
    /// the confirmation is journaled in ingest_batches.envelope_extra of the export and logged.
    /// </summary>
    public async Task<ODataSyncReport> RunAsync(Guid connectionId, ODataSyncMode mode, bool dryRun, CancellationToken ct, string? confirmedMassDeleteBy = null)
    {
        if (dryRun)
        {
            var connection = await db.InTransactionAsync(token => LoadConnectionAsync(connectionId, token), ct);
            return await RunAsync(connection, mode, dryRun: true, null, null, ct);
        }
        if (confirmedMassDeleteBy is not null && string.IsNullOrWhiteSpace(confirmedMassDeleteBy))
            throw new ArgumentException("A mass deletion is confirmed by a named operator.", nameof(confirmedMassDeleteBy));
        var (_, report, error) = await RecordedAsync(connectionId, mode, confirmedMassDeleteBy, ct);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return report!;
    }

    private async Task<(SyncOutcome Outcome, ODataSyncReport? Report, Exception? Error)> RecordedAsync(Guid connectionId, ODataSyncMode mode,
        string? confirmedMassDeleteBy, CancellationToken ct)
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
            var report = await RunAsync(connection, mode, dryRun: false, run.Id, confirmedMassDeleteBy, ct);
            report.SyncRunId = run.Id;
            await db.InTransactionAsync(async token =>
            {
                var tracked = await db.SyncRuns.SingleAsync(r => r.Id == run.Id, token);
                var c = await db.Connections.SingleAsync(x => x.Id == connectionId, token);
                var now = time.GetUtcNow();
                tracked.Succeed(now, report.Complete, report.Records);
                c.MarkSyncSucceeded(now);
                await db.SaveChangesAsync(token);
                // The whole source was read: its accounting data is fresh even when nothing changed.
                await writer.MarkFreshAsync(c.TenantId, c.Id, IngestDataGroup.Accounting, now, token);
            }, ct);
            // Attention rules over the connection (own scope and transaction; never fails the load, hence no cancellation token).
            await attention.AfterLoadAsync(AdapterKind.Accounting1C, succeeded: true, CancellationToken.None);
            return (new SyncOutcome(run.Id, SyncRunResult.Succeeded, report.Complete, report.Records, null), report, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("OData sync failed: connection {ConnectionId}, error type {ErrorType}", connectionId, ex.GetType().Name);
            var outcome = await SyncRunFinalizer.FailAsync(db, run.Id, connectionId, ex, 0, time.GetUtcNow(), breakerOptions.Value.FailureThreshold,
                interruptedByShutdown: ex is OperationCanceledException && shutdown.JobsCancelled);
            if (ex is OperationCanceledException) throw;
            await attention.AfterLoadAsync(AdapterKind.Accounting1C, succeeded: false, CancellationToken.None);
            return (outcome, null, ex);
        }
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

    private async Task<ODataSyncReport> RunAsync(Connection connection, ODataSyncMode mode, bool dryRun, Guid? runId, string? confirmedMassDeleteBy,
        CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var profile = ODataProfileResolver.Resolve(connection.Settings, O);
        var report = new ODataSyncReport { DryRun = dryRun, Profile = profile.Name };
        var tenantId = connection.TenantId;
        var organization = ConnectionKinds.ReadSetting(connection.Settings, "organization_key");
        if (organization is not null && !Guid.TryParse(organization, out _))
            throw new AdapterException("settings.organization_key of the OData connection is not a GUID.");

        // Auto: full or delta per entity, by the time of its own last completed full export (ODataFullSyncStates.FullIsDue).
        var states = mode == ODataSyncMode.Auto
            ? (await db.InTransactionAsync(token => ODataFullSyncStates.LoadAsync(db, tenantId, connection.Id, token), ct))
                .ToDictionary(s => s.Entity, StringComparer.Ordinal)
            : [];
        var decisionTime = time.GetUtcNow();
        bool FullFor(string entity) => mode switch
        {
            ODataSyncMode.Full => true,
            ODataSyncMode.Delta => false,
            _ => ODataFullSyncStates.FullIsDue(states.GetValueOrDefault(entity), decisionTime, O),
        };
        var decisions = profile.Entities.ToDictionary(e => e.Entity, e => FullFor(IngestSchema.Find(e.Entity)!.Name), StringComparer.Ordinal);
        report.Mode = decisions.Values.All(d => d) ? "full" : decisions.Values.All(d => !d) ? "delta" : "mixed";
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
            var full = decisions[entity.Entity];
            var er = new ODataEntityReport { Entity = spec.Name, Mode = full ? "full" : "delta", FullAttempted = full };
            report.Entities.Add(er);
            var isDocument = entity.Sources.Any(s => s.DateProperty is not null);
            var batchId = $"odata-{runKey}-{spec.Name}";
            var sentPackages = 0;
            try
            {

                // 1. Keys and versions in the source.
                var remote = new Dictionary<string, Remote>(StringComparer.Ordinal);
                var problems = new List<string>();
                foreach (var source in entity.Sources)
                {
                    var filter = Filter(source, organization, full || !isDocument || source.DateProperty is null ? null : DeltaFrom());
                    var (rows, problem) = await ListKeysAsync(client, profile, source, filter, mapper, full, ct);
                    if (problem is not null) problems.Add($"{source.Resource.Name}: {problem}");
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
                er.ActiveStored = full ? stored.Values.Count(s => !s.IsDeleted) : 0;
                if (full) er.MissingInSource = stored.Values.Count(s => !s.IsDeleted && !remote.ContainsKey(s.Ref));

                // A full export may mark records deleted only when the list of keys is known to be complete: every resource said
                // how many records it has (odata.count, else /$count) and exactly that many keys were collected.
                var exportFull = full;
                if (full && problems.Count > 0)
                {
                    exportFull = false;
                    er.Mode = "delta";
                    foreach (var problem in problems) er.Warnings.Add(problem + "; sent as a delta, nothing is marked deleted this time.");
                    report.Complete = false;
                    logger.LogWarning("OData full export of {Entity} sent as a delta: the list of keys may be incomplete ({Problems})",
                        spec.Name, string.Join("; ", problems));
                }

                // What the loader decided, journaled in the first package (synect_odata): the outcome of a full attempt and, when
                // it applies, the mass-deletion facts.
                JsonObject? marker = null;
                string? attemptReason = full && problems.Count > 0 ? string.Join("; ", problems) : null;
                var attemptOutcome = full && problems.Count > 0 ? ODataFullSyncStates.OutcomeIncompleteList : ODataFullSyncStates.OutcomeFull;
                var guard = O.MassDeleteGuard;
                if (exportFull && guard.Enabled && er.ActiveStored > 0)
                {
                    var share = (double)er.MissingInSource / er.ActiveStored;
                    var empty = remote.Count == 0;
                    if (empty || (er.MissingInSource >= guard.MinCount && share > guard.MaxShare))
                    {
                        var facts = new JsonObject
                        {
                            ["entity"] = spec.Name, ["missing"] = er.MissingInSource, ["active"] = er.ActiveStored,
                            ["share"] = Math.Round(share, 4), ["empty_source"] = empty,
                            ["max_share"] = guard.MaxShare, ["min_count"] = guard.MinCount,
                        };
                        if (confirmedMassDeleteBy is null)
                        {
                            exportFull = false;
                            er.Mode = "delta";
                            er.MassDeleteBlocked = true;
                            report.Complete = false;
                            er.Warnings.Add(empty
                                ? $"The source returned no records while {er.ActiveStored} are stored; nothing is marked deleted (OData:MassDeleteGuard)."
                                : $"{er.MissingInSource} of {er.ActiveStored} active records ({share:P0}) are absent from the source; nothing is marked deleted " +
                                  "(OData:MassDeleteGuard). If this is right: DevTools sync-odata --mode full --confirm-mass-delete.");
                            marker = new JsonObject { ["mass_delete_blocked"] = facts };
                            attemptOutcome = ODataFullSyncStates.OutcomeHeldBack;
                            attemptReason = empty
                                ? $"the source returned no records while {er.ActiveStored} are stored (mass-deletion guard)"
                                : $"{er.MissingInSource} of {er.ActiveStored} active records ({share:P0}) absent (mass-deletion guard)";
                            logger.LogWarning(
                                "OData mass deletion held back: connection {ConnectionId}, entity {Entity}, absent {Missing} of {Active} active ({Share:P1}), empty source {Empty}",
                                connection.Id, spec.Name, er.MissingInSource, er.ActiveStored, share, empty);
                        }
                        else
                        {
                            er.MassDeleteConfirmed = true;
                            facts["confirmed_by"] = confirmedMassDeleteBy;
                            facts["confirmed_at"] = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
                            facts["via"] = "DevTools sync-odata --confirm-mass-delete";
                            marker = new JsonObject { ["mass_delete_confirmed"] = facts };
                            er.Warnings.Add($"Mass deletion confirmed by {confirmedMassDeleteBy}: {er.MissingInSource} of {er.ActiveStored} records will be marked deleted.");
                            logger.LogWarning(
                                "OData mass deletion CONFIRMED by {Operator}: connection {ConnectionId}, entity {Entity}, absent {Missing} of {Active} active ({Share:P1})",
                                confirmedMassDeleteBy, connection.Id, spec.Name, er.MissingInSource, er.ActiveStored, share);
                        }
                    }
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

                if (full)
                {
                    marker ??= new JsonObject();
                    marker[ODataFullSyncStates.AttemptField] = new JsonObject { ["outcome"] = attemptOutcome, ["reason"] = attemptReason };
                }

                // 4. Send through the receiver (or count, in a dry run). A held-back export still sends one (possibly empty) delta
                // package, so that the reason is journaled in ingest_batches.envelope_extra.
                var packages = items.Chunk(Math.Min(PackageItems, ingestOptions.Value.MaxItems)).ToList();
                if (packages.Count == 0 && (exportFull || marker is not null)) packages.Add([]);
                er.Packages = packages.Count;
                if (dryRun) continue;

                var fullSyncId = exportFull ? $"odata-{runKey}" : null;
                for (var i = 0; i < packages.Count; i++)
                {
                    var body = Envelope(spec.Name, batchId, i + 1, exportFull, fullSyncId, i == packages.Count - 1, packages[i], i == 0 ? marker : null);
                    sentPackages++;
                    var response = await ingest.ProcessPulledAsync(connection.Id, body, exportFull && i == 0 ? unchanged : null, ct);
                    Account(er, response, spec.Name);
                }
                if (er.Rejected > 0) report.Complete = false;
            }
            catch (Exception ex) when (full && !dryRun && ex is not OperationCanceledException)
            {
                // The failed full attempt is journaled (an empty delta package with the reason), so that Auto waits
                // OData:FullRetryAfter before the next one and the overdue rule can say why. Then the load fails as before.
                await JournalFailedAttemptAsync(connection.Id, spec.Name, batchId, sentPackages + 1, $"{ex.GetType().Name}: {ex.Message}");
                throw;
            }
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
    /// Returns why the list may be incomplete, or null: the count changed between pages, pages overlapped, the number of
    /// keys differs from odata.count, or — for a full export without odata.count — from /$count, or /$count is unavailable.
    /// </summary>
    private async Task<(List<Remote> Rows, string? Problem)> ListKeysAsync(IODataClient client, ODataProfile profile, ODataSource source, string? filter,
        ODataRecordMapper mapper, bool full, CancellationToken ct)
    {
        var rows = new List<Remote>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? problem = null;
        long? firstCount = null;
        var size = Math.Max(1, O.PageSize);
        for (var page = 0; ; page++)
        {
            if (page >= O.MaxPages) return (rows, $"stopped at OData:MaxPages ({O.MaxPages})");
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
                if (count != firstCount) problem ??= $"odata.count changed between pages ({firstCount} -> {count})";
            }
            var fresh = 0;
            foreach (var record in result.Items)
            {
                if (mapper.KeyOf(record) is not { } key) continue;
                if (!seen.Add(key))
                {
                    problem ??= "pages repeated records";
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
        if (firstCount is { } expected)
        {
            if (rows.Count != expected) problem ??= $"{rows.Count} keys listed, odata.count says {expected}";
        }
        else if (full)
        {
            // No odata.count: a page may have been cut silently, so the total is asked separately.
            try
            {
                var total = await client.CountAsync(source.Resource.Name, filter, ct);
                if (total != rows.Count) problem ??= $"no odata.count; {rows.Count} keys listed, /$count says {total}";
            }
            catch (AdapterException ex) when (ex is not AdapterAuthenticationException)
            {
                problem ??= "no odata.count and /$count is not available";
            }
        }
        return (rows, problem);
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

    /// <summary>An empty delta package that records a full attempt which ended with an error (never fails the load further).</summary>
    private async Task JournalFailedAttemptAsync(Guid connectionId, string entity, string batchId, int sequence, string reason)
    {
        try
        {
            if (reason.Length > 500) reason = reason[..500];
            var marker = new JsonObject { [ODataFullSyncStates.AttemptField] = new JsonObject { ["outcome"] = ODataFullSyncStates.OutcomeError, ["reason"] = reason } };
            var body = Envelope(entity, batchId, sequence, false, null, false, [], marker);
            await ingest.ProcessPulledAsync(connectionId, body, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not journal the failed full attempt of {Entity}: {ErrorType}", entity, ex.GetType().Name);
        }
    }

    /// <summary>Envelope field (kept in ingest_batches.envelope_extra) with what the OData loader decided about deletions.</summary>
    public const string MarkerField = "synect_odata";

    private byte[] Envelope(string entity, string batchId, int sequence, bool full, string? fullSyncId, bool last, IEnumerable<JsonObject> items,
        JsonObject? marker)
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
        if (marker is not null) envelope[MarkerField] = marker.DeepClone();
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
