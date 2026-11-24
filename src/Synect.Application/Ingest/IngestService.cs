using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Common;
using Synect.Domain.Accounting;
using Synect.Domain.Sync;

namespace Synect.Application.Ingest;

/// <summary>Per-item result of a package.</summary>
public sealed record IngestItemResult(int Index, string? Ref, string Status, string? Code = null, string? Message = null)
{
    public const string Accepted = "accepted";
    public const string Unchanged = "unchanged";
    public const string Rejected = "rejected";
}

/// <summary>What the receiver answers: an HTTP status and a JSON body.</summary>
public sealed record IngestResponse(int HttpStatus, object Body);

/// <summary>
/// Receives one package of the 1C extension for the organization of the ingest key (stage 8a: accounting and catalog). The caller sets the tenant
/// context from the key. In one transaction per package (serialized per organization with an advisory lock): the journal
/// row, every item (accepted / unchanged / rejected, one refused item never stops the others), the raw layer, full export
/// bookkeeping and the freshness of the 1C connection. A system error rolls the package back and is journaled as Failed.
/// Logs carry counters and identifiers only, never item content. Receiving a package recalculates nothing (no reconciliation,
/// analytics or attention rules): catalog packages come every minute, and accounting data has no reader that needs it yet.
/// </summary>
public sealed class IngestService(
    ISynectDbContext db,
    ITenantContext tenant,
    IngestWriter writer,
    IOptions<IngestOptions> options,
    TimeProvider time,
    ILogger<IngestService> logger)
{
    private IngestOptions O => options.Value;

    /// <summary>
    /// organization_tin of packages built by Synect itself from a pulled source (1C:Fresh over OData). The organization of such
    /// a package is the tenant context of the load, not a key, so the TIN of the envelope is a fixed placeholder.
    /// </summary>
    public const string PulledOrganizationTin = "000000000";

    /// <summary>Where a package came from: the 1C extension with an ingest key (push) or a load of Synect itself (pull).</summary>
    private sealed record Source(Guid? IngestKeyId, Guid TenantId, Guid ConnectionId, string? TenantTin)
    {
        public bool Pulled => IngestKeyId is null;
    }

    /// <summary>A package sent by the 1C extension with an ingest key.</summary>
    public Task<IngestResponse> ProcessAsync(IngestKeyIdentity key, byte[] body, CancellationToken ct) =>
        ProcessCoreAsync(new Source(key.IngestKeyId, key.TenantId, key.ConnectionId, key.TenantTin), body, null, ct);

    /// <summary>
    /// A package built by Synect from a pulled 1C source (1C:Fresh over OData) for the current organization and the given
    /// connection: the same parsing, writing, journal and full-export bookkeeping as a package of the extension, with no
    /// ingest key (ingest_batches.ingest_key_id is null, the source is the connection). organization_tin must be
    /// <see cref="PulledOrganizationTin"/>. <paramref name="unchangedKeys"/>: in a full export, keys of records that still
    /// exist in the source but were not sent again because their version did not change; they count as present, so the
    /// completed export does not mark them deleted (they are also touched as seen).
    /// </summary>
    public Task<IngestResponse> ProcessPulledAsync(Guid connectionId, byte[] body, IReadOnlyCollection<string>? unchangedKeys, CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Pulled packages are processed in a tenant context.");
        return ProcessCoreAsync(new Source(null, tenantId, connectionId, PulledOrganizationTin), body, unchangedKeys, ct);
    }

    private async Task<IngestResponse> ProcessCoreAsync(Source key, byte[] body, IReadOnlyCollection<string>? unchangedKeys, CancellationToken ct)
    {
        if (tenant.TenantId != key.TenantId) throw new InvalidOperationException("The tenant context must be the organization of the ingest key.");
        var started = Stopwatch.StartNew();
        var receivedAt = time.GetUtcNow();
        var sha = Convert.ToHexStringLower(SHA256.HashData(body));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            logger.LogWarning("Ingest package refused for tenant {TenantId}: invalid JSON ({Bytes} bytes)", key.TenantId, body.Length);
            return Error(400, IngestEnvelopeCodes.InvalidJson, "The request body is not valid JSON.");
        }

        using (document)
        {
            IngestEnvelope envelope;
            JsonElement items;
            try
            {
                (envelope, items) = IngestEnvelopeParser.Parse(document.RootElement, O, key.TenantTin);
            }
            catch (IngestEnvelopeException ex)
            {
                await JournalRefusalAsync(key, ex.BatchId, ex.Sequence, ex.Entity, ex.SchemaVersion, ex.Code, ex.Message, sha, body.Length, receivedAt, ct);
                logger.LogWarning("Ingest package {BatchId}#{Sequence} refused for tenant {TenantId}: {Code}", ex.BatchId, ex.Sequence, key.TenantId, ex.Code);
                return Error(ex.HttpStatus, ex.Code, ex.Message,
                    ex.Code == IngestEnvelopeCodes.UnsupportedSchemaVersion ? O.AcceptedSchemaVersions : null);
            }

            try
            {
                var response = await db.InTransactionAsync(token => ProcessInTransactionAsync(key, envelope, items, sha, body.Length, receivedAt, unchangedKeys, token), ct);
                logger.LogInformation(
                    "Ingest {Entity} package {BatchId}#{Sequence} for tenant {TenantId}: {Status} in {ElapsedMs} ms",
                    envelope.Entity.Name, envelope.BatchId, envelope.Sequence, key.TenantId, response.Status, started.ElapsedMilliseconds);
                if (response.Counts is { } c)
                    logger.LogInformation("Ingest package {BatchId}#{Sequence} counts: total {Total}, accepted {Accepted}, unchanged {Unchanged}, rejected {Rejected}, marked deleted {MarkedDeleted}, removed rows {RemovedRows}",
                        envelope.BatchId, envelope.Sequence, c.Total, c.Accepted, c.Unchanged, c.Rejected, c.MarkedDeleted, c.RemovedRows);
                return response.Response;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never the message of a database error: it may quote values of the package.
                logger.LogError("Ingest package {BatchId}#{Sequence} for tenant {TenantId} failed: {ErrorType}",
                    envelope.BatchId, envelope.Sequence, key.TenantId, ex.GetType().Name);
                db.ChangeTracker.Clear();
                await JournalAsync(key, envelope, IngestBatchStatus.Failed, $"internal_error: {ex.GetType().Name}", sha, body.Length, receivedAt, ct);
                return Error(500, "internal_error", "The package could not be stored because of a system error; send it again later.");
            }
        }
    }

    private sealed record Counts(int Total, int Accepted, int Unchanged, int Rejected, int MarkedDeleted, int RemovedRows);

    private sealed record Outcome(IngestResponse Response, string Status, Counts? Counts);

    private async Task<Outcome> ProcessInTransactionAsync(Source key, IngestEnvelope env, JsonElement items, string sha,
        int bytes, DateTimeOffset now, IReadOnlyCollection<string>? unchangedKeys, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"ingest:" + key.TenantId}, 0))", ct);

        var connection = await db.Connections.SingleOrDefaultAsync(c => c.Id == key.ConnectionId, ct);
        // The extension sends only to its own connection; a pulled package belongs only to the OData connection that loaded it.
        if (connection is null || connection.AdapterKind != AdapterKind.Accounting1C || connection.Status == ConnectionStatus.Disabled
            || connection.IsOneCOData != key.Pulled)
        {
            const string message = "The 1C connection of this organization is disabled.";
            return new Outcome(Error(403, IngestEnvelopeCodes.ConnectionDisabled, message), "rejected", null);
        }

        var batch = await db.IngestBatches.SingleOrDefaultAsync(b => b.BatchId == env.BatchId && b.Sequence == env.Sequence, ct);
        if (batch is { Status: IngestBatchStatus.Processed or IngestBatchStatus.ProcessedWithErrors })
        {
            if (batch.PayloadSha256 != sha)
                return new Outcome(Error(409, IngestEnvelopeCodes.BatchConflict,
                    "A different package was already processed under this batch_id and sequence; use a new batch_id."), "conflict", null);
            batch.Attempts++;
            await db.SaveChangesAsync(ct);
            return new Outcome(new IngestResponse(200, Duplicate(batch)), "duplicate", null);
        }

        if (batch is null)
        {
            batch = new IngestBatch { BatchId = env.BatchId, Sequence = env.Sequence };
            db.IngestBatches.Add(batch);
        }
        else
        {
            batch.Attempts++;
        }
        Fill(batch, key, env, sha, bytes, now);
        batch.Status = IngestBatchStatus.Processed;
        await db.SaveChangesAsync(ct);

        var phase = Stopwatch.StartNew();
        var phases = new List<string>(8);
        void Lap(string name)
        {
            phases.Add($"{name} {phase.ElapsedMilliseconds}");
            phase.Restart();
        }
        Lap("journal");

        var spec = env.Entity;
        var parser = new IngestItemParser(spec, O);
        var parsed = new List<ParsedItem>(items.GetArrayLength());
        var index = 0;
        foreach (var item in items.EnumerateArray()) parsed.Add(parser.Parse(index++, item, env.ExportedAt));

        Lap("parse");
        var valid = parsed.Where(p => p.IsValid).ToList();
        var stored = await writer.LoadAsync(spec, key.TenantId, valid, ct);
        var storedBefore = stored.Keys.ToHashSet(StringComparer.Ordinal);
        Lap("lookup");
        var results = new IngestItemResult[parsed.Count];
        var apply = new Dictionary<string, ParsedItem>(StringComparer.Ordinal);
        var notices = new Dictionary<string, ParsedItem>(StringComparer.Ordinal);
        var touch = new List<ParsedItem>();

        foreach (var item in parsed)
        {
            if (!item.IsValid)
            {
                results[item.Index] = new IngestItemResult(item.Index, item.DisplayKey, IngestItemResult.Rejected, item.ErrorCode, item.ErrorMessage);
                continue;
            }
            var key0 = item.Key!;
            stored.TryGetValue(key0, out var current);
            if (current is null && item.IsDeletionNotice)
            {
                results[item.Index] = new IngestItemResult(item.Index, item.DisplayKey, IngestItemResult.Unchanged, IngestCodes.UnknownRefDeleted,
                    "Deletion of a record Synect never received; nothing to mark.");
                continue;
            }
            if (current is not null && current.ContentHash == item.ContentHash)
            {
                // The same content as a record that is marked deleted only by a completed full export (that marking keeps the
                // content hash): the object exists in 1C after all, so the mark is lifted and the item is applied again.
                // An older version is never applied (the stored modified_at is not newer than the item's).
                if (current.IsDeleted && !item.IsDeleted && item.ModifiedAt >= current.ModifiedAt)
                {
                    results[item.Index] = new IngestItemResult(item.Index, item.DisplayKey, IngestItemResult.Accepted, IngestCodes.Restored,
                        "The record was marked deleted by a full export and was received again; the deletion mark was lifted.");
                    apply.Remove(key0);
                    notices.Remove(key0);
                    apply[key0] = item;
                    stored[key0] = new StoredRecord { Key = key0, ModifiedAt = item.ModifiedAt, ContentHash = item.ContentHash, IsDeleted = false };
                    continue;
                }
                results[item.Index] = new IngestItemResult(item.Index, item.DisplayKey, IngestItemResult.Unchanged, IngestCodes.SameContent);
                touch.Add(item);
                continue;
            }
            if (current is not null && item.ModifiedAt < current.ModifiedAt)
            {
                results[item.Index] = new IngestItemResult(item.Index, item.DisplayKey, IngestItemResult.Unchanged, IngestCodes.OlderThanStored,
                    "A newer version of this record is already stored; the item was not applied.");
                continue;
            }
            results[item.Index] = new IngestItemResult(item.Index, item.DisplayKey, IngestItemResult.Accepted);
            // The same record twice in one package: the later version wins (it is compared with the earlier one).
            apply.Remove(key0);
            notices.Remove(key0);
            if (item.IsDeletionNotice) notices[key0] = item;
            else apply[key0] = item;
            stored[key0] = new StoredRecord { Key = key0, ModifiedAt = item.ModifiedAt, ContentHash = item.ContentHash, IsDeleted = item.IsDeleted || (current?.IsDeleted ?? false) };
        }

        var toApply = apply.Values.ToList();
        await writer.UpsertAsync(spec, key.TenantId, batch.Id, env.SchemaVersion, now, toApply, ct);
        await writer.MarkDeletedAsync(spec, key.TenantId, batch.Id, env.SchemaVersion, now, notices.Values.ToList(), ct);
        Lap("upsert");
        await writer.ReplaceChildrenAsync(spec, key.TenantId, toApply.Where(i => i.Children is not null).ToList(), storedBefore, ct);
        Lap("children");
        await writer.TouchAsync(spec, key.TenantId, now, touch, ct);
        if (unchangedKeys is { Count: > 0 } && spec.HasRef) await writer.TouchRefsAsync(spec, key.TenantId, now, unchangedKeys, ct);
        Lap("touch");

        // Raw layer: every applied item as received; the last refused version of an item with a readable key separately.
        var rawRecords = new Dictionary<(string, string), (string Type, string Id, string Json, string Hash)>();
        foreach (var i in toApply.Concat(notices.Values))
            rawRecords[(spec.RawObjectType, i.Key!)] = (spec.RawObjectType, i.Key!, i.RawJson, i.ContentHash);
        foreach (var p in parsed.Where(p => !p.IsValid && p.Key is { Length: <= 256 } && p.ErrorCode != IngestCodes.InvalidText))
            rawRecords[(spec.RawObjectType + ".rejected", p.Key!)] = (spec.RawObjectType + ".rejected", p.Key!, p.RawJson, JsonHasher.Hash(p.RawJson));
        await writer.UpsertRawAsync(key.TenantId, connection.Id, connection.SourceCode, rawRecords.Values.ToList(), now, ct);
        Lap("raw");

        var accepted = results.Count(r => r.Status == IngestItemResult.Accepted);
        var unchanged = results.Count(r => r.Status == IngestItemResult.Unchanged);
        var rejected = results.Count(r => r.Status == IngestItemResult.Rejected);
        batch.ItemsTotal = parsed.Count;
        batch.Accepted = accepted;
        batch.Unchanged = unchanged;
        batch.Rejected = rejected;
        batch.Status = rejected > 0 ? IngestBatchStatus.ProcessedWithErrors : IngestBatchStatus.Processed;
        batch.ItemErrors = rejected == 0 ? null : JsonSerializer.Serialize(
            results.Where(r => r.Status == IngestItemResult.Rejected).Select(r => new { index = r.Index, key = r.Ref, code = r.Code, message = r.Message }));
        await db.SaveChangesAsync(ct);

        object? fullSync = null;
        if (env.IsFull)
        {
            var keys = parsed.Where(p => p.Key is not null && p.Key.Length <= 400).Select(p => p.Key!)
                .Concat(unchangedKeys?.Where(k => k.Length <= 400) ?? []).Distinct(StringComparer.Ordinal).ToList();
            await writer.TrackFullSyncAsync(key.TenantId, env.FullSyncId!, spec.Name, keys, now, ct);
            fullSync = await CompleteFullSyncAsync(spec, key.TenantId, batch, env, now, ct);
        }

        // A package with at least one item that was not refused shows that the 1C source is alive. Freshness is also kept
        // per data group, so that catalog packages every minute never make stale accounting data look fresh.
        if (accepted + unchanged > 0 || parsed.Count == 0)
        {
            connection.MarkSyncSucceeded(now);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO ingest_freshness (tenant_id, connection_id, data_group, last_accepted_at)
                VALUES ({key.TenantId}, {connection.Id}, {spec.Group.ToString()}, {now.ToUniversalTime()})
                ON CONFLICT (tenant_id, connection_id, data_group) DO UPDATE SET last_accepted_at = excluded.last_accepted_at
                WHERE ingest_freshness.last_accepted_at < excluded.last_accepted_at
                """, ct);
        }
        batch.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        Lap("finish");
        logger.LogDebug("Ingest package {BatchId}#{Sequence} phases, ms: {Phases}", env.BatchId, env.Sequence, string.Join(", ", phases));

        var status = rejected == 0 ? "accepted" : accepted + unchanged == 0 ? "rejected" : "partially_accepted";
        var bodyObject = new
        {
            status,
            ingest_batch_id = batch.Id,
            batch_id = env.BatchId,
            sequence = env.Sequence,
            entity = spec.Name,
            mode = env.Mode,
            schema_version = env.SchemaVersion,
            items_total = parsed.Count,
            accepted,
            unchanged,
            rejected,
            marked_deleted = batch.MarkedDeleted,
            removed = batch.RemovedRows,
            full_sync = fullSync,
            items = results.Select(r => new { index = r.Index, @ref = r.Ref, status = r.Status, code = r.Code, message = r.Message }),
        };
        return new Outcome(new IngestResponse(200, bodyObject), status,
            new Counts(parsed.Count, accepted, unchanged, rejected, batch.MarkedDeleted, batch.RemovedRows));
    }

    /// <summary>
    /// On the package with is_last_batch: deletions are marked (current-state tables: rows removed) only when every sequence
    /// 1..N of this export and entity was processed (a truncated or partly lost export changes nothing and says which
    /// sequences are missing). Only records absent from this full_sync_id are affected.
    /// </summary>
    private async Task<object> CompleteFullSyncAsync(IngestEntitySpec spec, Guid tenantId, IngestBatch batch, IngestEnvelope env, DateTimeOffset now, CancellationToken ct)
    {
        if (!env.IsLastBatch)
            return new { full_sync_id = env.FullSyncId, completed = false, reason = "waiting_for_last_batch", marked_deleted = 0, removed = 0 };

        var processed = await db.IngestBatches.AsNoTracking()
            .Where(b => b.FullSyncId == env.FullSyncId && b.Entity == spec.Name
                        && (b.Status == IngestBatchStatus.Processed || b.Status == IngestBatchStatus.ProcessedWithErrors))
            .Select(b => new { b.Sequence, b.ReceivedAt }).ToListAsync(ct);
        var sequences = processed.Select(p => p.Sequence).ToHashSet();
        var missing = Enumerable.Range(1, env.Sequence).Where(s => !sequences.Contains(s)).Take(100).ToList();
        if (missing.Count > 0 || sequences.Any(s => s > env.Sequence))
            return new
            {
                full_sync_id = env.FullSyncId, completed = false, reason = "missing_sequences", missing_sequences = missing, marked_deleted = 0,
                removed = 0,
            };

        var startedAt = processed.Min(p => p.ReceivedAt);
        var count = await writer.CompleteFullSyncAsync(spec, tenantId, batch.Id, env.FullSyncId!, startedAt, now, O.FullSyncRetention, ct);
        if (spec.CurrentState) batch.RemovedRows = count;
        else batch.MarkedDeleted = count;
        return new
        {
            full_sync_id = env.FullSyncId, completed = true, reason = (string?)null, marked_deleted = batch.MarkedDeleted, removed = batch.RemovedRows,
        };
    }

    private static object Duplicate(IngestBatch b)
    {
        object? errors = b.ItemErrors is null ? null : JsonSerializer.Deserialize<JsonElement>(b.ItemErrors);
        return new
        {
            status = "duplicate",
            ingest_batch_id = b.Id,
            batch_id = b.BatchId,
            sequence = b.Sequence,
            entity = b.Entity,
            mode = b.Mode,
            schema_version = b.SchemaVersion,
            items_total = b.ItemsTotal,
            accepted = b.Accepted,
            unchanged = b.Unchanged,
            rejected = b.Rejected,
            marked_deleted = b.MarkedDeleted,
            removed = b.RemovedRows,
            rejected_items = errors,
            message = "This package was already processed (same batch_id, sequence and content); nothing was changed. The counts are those of the first processing.",
        };
    }

    private static void Fill(IngestBatch batch, Source key, IngestEnvelope env, string sha, int bytes, DateTimeOffset now)
    {
        batch.ConnectionId = key.ConnectionId;
        batch.IngestKeyId = key.IngestKeyId;
        batch.Entity = env.Entity.Name;
        batch.Mode = env.Mode;
        batch.FullSyncId = env.FullSyncId;
        batch.IsLastBatch = env.IsLastBatch;
        batch.SchemaVersion = env.SchemaVersion;
        batch.ExtensionVersion = env.ExtensionVersion;
        batch.ExportedAt = env.ExportedAt;
        batch.ReceivedAt = now;
        batch.FinishedAt = null;
        batch.PayloadSha256 = sha;
        batch.PayloadBytes = bytes;
        batch.EnvelopeExtra = env.ExtraJson;
        batch.Error = null;
        batch.ItemErrors = null;
        batch.ItemsTotal = batch.Accepted = batch.Unchanged = batch.Rejected = batch.MarkedDeleted = batch.RemovedRows = 0;
    }

    /// <summary>A refused envelope is journaled when batch_id and sequence could be read (a processed row is never replaced).</summary>
    private Task JournalRefusalAsync(Source key, string? batchId, int? sequence, string? entity, int? schemaVersion,
        string code, string message, string sha, int bytes, DateTimeOffset now, CancellationToken ct)
    {
        if (batchId is null || sequence is null) return Task.CompletedTask;
        return WriteJournalAsync(key, batchId, sequence.Value, entity ?? "unknown", "unknown", null, false, schemaVersion ?? 0, null, null,
            IngestBatchStatus.Rejected, $"{code}: {message}", sha, bytes, now, ct);
    }

    private Task JournalAsync(Source key, IngestEnvelope env, IngestBatchStatus status, string error, string sha, int bytes,
        DateTimeOffset now, CancellationToken ct) =>
        WriteJournalAsync(key, env.BatchId, env.Sequence, env.Entity.Name, env.Mode, env.FullSyncId, env.IsLastBatch, env.SchemaVersion,
            env.ExtensionVersion, env.ExportedAt, status, error, sha, bytes, now, ct);

    private async Task WriteJournalAsync(Source key, string batchId, int sequence, string entity, string mode, string? fullSyncId,
        bool isLast, int schemaVersion, string? extension, DateTimeOffset? exportedAt, IngestBatchStatus status, string error, string sha,
        int bytes, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            if (error.Length > 2000) error = error[..2000];
            await db.InTransactionAsync(token => db.Database.ExecuteSqlAsync($"""
                INSERT INTO ingest_batches (id, tenant_id, connection_id, ingest_key_id, batch_id, sequence, entity, mode, full_sync_id,
                    is_last_batch, schema_version, extension_version, exported_at, received_at, finished_at, items_total, accepted, unchanged,
                    rejected, marked_deleted, status, error, payload_sha256, payload_bytes, attempts)
                VALUES ({Guid.CreateVersion7()}, {key.TenantId}, {key.ConnectionId}, {key.IngestKeyId}, {batchId}, {sequence}, {entity}, {mode},
                    {fullSyncId}, {isLast}, {schemaVersion}, {extension}, {exportedAt?.ToUniversalTime()}, {now.ToUniversalTime()},
                    {now.ToUniversalTime()}, 0, 0, 0, 0, 0, {status.ToString()}, {error}, {sha}, {bytes}, 1)
                ON CONFLICT (tenant_id, batch_id, sequence) DO UPDATE SET
                    status = excluded.status, error = excluded.error, received_at = excluded.received_at, finished_at = excluded.finished_at,
                    payload_sha256 = excluded.payload_sha256, payload_bytes = excluded.payload_bytes, attempts = ingest_batches.attempts + 1
                WHERE ingest_batches.status IN ('Rejected', 'Failed')
                """, token), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Could not journal ingest package {BatchId}#{Sequence} for tenant {TenantId}: {ErrorType}", batchId, sequence, key.TenantId, ex.GetType().Name);
        }
    }

    private static IngestResponse Error(int status, string code, string message, int[]? supported = null) =>
        new(status, supported is null
            ? new { status = "rejected", error = code, message }
            : (object)new { status = "rejected", error = code, message, supported_schema_versions = supported });
}
