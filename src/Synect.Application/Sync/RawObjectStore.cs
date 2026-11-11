using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Common;
using Synect.Domain.Raw;
using Synect.Domain.Sync;

namespace Synect.Application.Sync;

/// <summary>Raw layer writer: keeps exactly one row (the latest version) per external object.</summary>
public sealed class RawObjectStore(ISynectDbContext db, LoadProfile profile)
{

    /// <summary>
    /// Upserts raw records and returns the payload hash for each input record, in input order.
    /// Does not call SaveChanges.
    /// </summary>
    public async Task<IReadOnlyList<string>> UpsertAsync(
        Connection connection, IReadOnlyList<RawRecord> records, DateTimeOffset now, CancellationToken ct)
    {
        var hashes = new string[records.Count];
        foreach (var group in records.Select((r, i) => (Record: r, Index: i)).GroupBy(x => x.Record.ObjectType))
        {
            var objectType = group.Key;
            var ids = group.Select(x => x.Record.ExternalId).Distinct().ToList();
            var existing = await db.RawObjects
                .Where(r => r.ConnectionId == connection.Id && r.ObjectType == objectType && ids.Contains(r.ExternalId))
                .ToDictionaryAsync(r => r.ExternalId, StringComparer.Ordinal, ct);

            foreach (var (record, index) in group)
            {
                var hash = JsonHasher.Hash(record.Json);
                hashes[index] = hash;

                if (!existing.TryGetValue(record.ExternalId, out var raw))
                {
                    raw = new RawObject
                    {
                        TenantId = connection.TenantId,
                        ConnectionId = connection.Id,
                        SourceCode = connection.SourceCode,
                        ObjectType = record.ObjectType,
                        ExternalId = record.ExternalId,
                        FirstSeenAt = now,
                    };
                    db.RawObjects.Add(raw);
                    existing[record.ExternalId] = raw;
                }

                raw.Apply(record.Json, hash, now);
            }
        }
        return hashes;
    }

    /// <summary>
    /// Same result as <see cref="UpsertAsync"/> in one INSERT ... ON CONFLICT statement (PostgreSQL only): new objects are
    /// inserted, known ones get last_seen_at and, when the hash differs, the new payload. Nothing is tracked by EF.
    /// The last record of a repeated (type, external id) wins.
    /// </summary>
    /// <param name="refreshUnchanged">
    /// Also set last_seen_at of unchanged objects (one UPDATE). Statement imports pass false: the receipt of a file is
    /// recorded in statement_imports, and rewriting thousands of unchanged rows would dominate a repeated import.
    /// </param>
    public async Task<IReadOnlyList<string>> UpsertBulkAsync(
        Connection connection, IReadOnlyList<RawRecord> records, DateTimeOffset now, CancellationToken ct, bool refreshUnchanged = true)
    {
        string[] hashes;
        using (profile.Measure("raw_hash"))
            hashes = records.Count > 1000
                ? records.AsParallel().AsOrdered().Select(r => JsonHasher.Hash(r.Json)).ToArray()
                : records.Select(r => JsonHasher.Hash(r.Json)).ToArray();
        var last = new Dictionary<(string, string), int>();
        for (var i = 0; i < records.Count; i++) last[(records[i].ObjectType, records[i].ExternalId)] = i;
        // Sorted by key: neighbouring keys go to the same index pages (far fewer page reads on a large table).
        var index = last.OrderBy(k => k.Key.Item1, StringComparer.Ordinal).ThenBy(k => k.Key.Item2, StringComparer.Ordinal).Select(k => k.Value).ToArray();
        if (index.Length == 0) return hashes;

        // Stored hashes first (one query per object type and 10 000 ids): unchanged objects only get last_seen_at, in one
        // UPDATE without sending their payload again; only new and changed payloads are written.
        var lookup = profile.Measure("raw_lookup");
        var stored = new Dictionary<(string, string), string>();
        foreach (var byType in index.GroupBy(i => records[i].ObjectType))
        {
            foreach (var chunk in byType.Select(i => records[i].ExternalId).Chunk(10_000))
            {
                var keys = chunk.ToList();
                var type = byType.Key;
                foreach (var row in await db.RawObjects.AsNoTracking()
                             .Where(r => r.ConnectionId == connection.Id && r.ObjectType == type && keys.Contains(r.ExternalId))
                             .Select(r => new { r.ExternalId, r.Hash }).ToListAsync(ct))
                    stored[(type, row.ExternalId)] = row.Hash;
            }
        }
        lookup.Dispose();
        var at = now.ToUniversalTime();
        var same = index.Where(i => stored.TryGetValue((records[i].ObjectType, records[i].ExternalId), out var h) && h == hashes[i]).ToList();
        if (same.Count > 0 && refreshUnchanged)
        {
            var sameTypes = same.Select(i => records[i].ObjectType).ToArray();
            var sameIds = same.Select(i => records[i].ExternalId).ToArray();
            await db.Database.ExecuteSqlAsync($"""
                UPDATE raw_objects r SET last_seen_at = {at}
                FROM unnest({sameTypes}, {sameIds}) AS u(t, e)
                WHERE r.tenant_id = {connection.TenantId} AND r.connection_id = {connection.Id} AND r.object_type = u.t AND r.external_id = u.e
                """, ct);
        }
        var sameSet = same.ToHashSet();
        index = index.Where(i => !sameSet.Contains(i)).ToArray();
        if (index.Length == 0) return hashes;

        var ids = index.Select(_ => Guid.CreateVersion7()).ToArray();
        var types = index.Select(i => records[i].ObjectType).ToArray();
        var external = index.Select(i => records[i].ExternalId).ToArray();
        var payloads = index.Select(i => records[i].Json).ToArray();
        var hash = index.Select(i => hashes[i]).ToArray();
        using var _ = profile.Measure("raw_sql");
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO raw_objects (id, tenant_id, connection_id, source_code, object_type, external_id, payload, hash, first_seen_at, last_seen_at, updated_at)
            SELECT u.id, {connection.TenantId}, {connection.Id}, {connection.SourceCode}, u.t, u.e, u.p::jsonb, u.h, {at}, {at}, {at}
            FROM unnest({ids}, {types}, {external}, {payloads}, {hash}) AS u(id, t, e, p, h)
            ON CONFLICT (tenant_id, connection_id, object_type, external_id) DO UPDATE SET
                last_seen_at = excluded.last_seen_at,
                payload = CASE WHEN raw_objects.hash = excluded.hash THEN raw_objects.payload ELSE excluded.payload END,
                updated_at = CASE WHEN raw_objects.hash = excluded.hash THEN raw_objects.updated_at ELSE excluded.updated_at END,
                hash = excluded.hash
            """, ct);
        return hashes;
    }
}
