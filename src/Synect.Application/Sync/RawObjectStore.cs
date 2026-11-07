using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Common;
using Synect.Domain.Raw;
using Synect.Domain.Sync;

namespace Synect.Application.Sync;

/// <summary>Raw layer writer: keeps exactly one row (the latest version) per external object.</summary>
public sealed class RawObjectStore(ISynectDbContext db)
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
}
