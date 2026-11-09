using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Counterparties;

namespace Synect.Application.Sync;

/// <summary>A counterparty name seen in loaded data (one or <paramref name="Count"/> operations / documents).</summary>
public sealed record NameSighting(string? Inn, string? Name, CounterpartyNameSource Source, DateTimeOffset SeenAt, int Count = 1);

/// <summary>Maintains the tenant's counterparty directory keyed by INN and every spelling of each name (counterparty_aliases).</summary>
public sealed class CounterpartyStore(ISynectDbContext db)
{
    /// <summary>Adds unknown counterparties and fills missing names. Does not call SaveChanges.</summary>
    public async Task UpsertAsync(
        Guid tenantId, IEnumerable<(string? Inn, string? Name)> items, DateTimeOffset now, CancellationToken ct)
    {
        var byInn = items
            .Where(x => !string.IsNullOrWhiteSpace(x.Inn))
            .GroupBy(x => x.Inn!.Trim())
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).LastOrDefault(n => !string.IsNullOrWhiteSpace(n)));
        if (byInn.Count == 0) return;

        var inns = byInn.Keys.ToList();
        var existing = await db.Counterparties
            .Where(c => inns.Contains(c.Inn))
            .ToDictionaryAsync(c => c.Inn, ct);

        foreach (var (inn, name) in byInn)
        {
            if (existing.TryGetValue(inn, out var counterparty))
            {
                if (string.IsNullOrWhiteSpace(counterparty.Name) && !string.IsNullOrWhiteSpace(name))
                {
                    counterparty.Name = name;
                    counterparty.UpdatedAt = now;
                }
                continue;
            }

            db.Counterparties.Add(new Counterparty
            {
                TenantId = tenantId,
                Inn = inn,
                Name = name,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    /// <summary>
    /// Adds name spellings to counterparty_aliases: new (INN, source, name) rows, or more occurrences and a wider
    /// first/last seen range for known ones. Sightings without an INN or a name are ignored (payments without an
    /// INN are not searchable by name). On PostgreSQL this is one INSERT ... ON CONFLICT statement in the current
    /// transaction, so concurrent loads of the same tenant cannot collide; elsewhere (SQLite tests) tracked entities.
    /// </summary>
    public async Task RecordNamesAsync(Guid tenantId, IEnumerable<NameSighting> sightings, CancellationToken ct)
    {
        var rows = sightings
            .Where(s => !string.IsNullOrWhiteSpace(s.Inn) && !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => s with { Inn = s.Inn!.Trim(), Name = Cut(s.Name!.Trim()) })
            .GroupBy(s => (s.Inn!, s.Source, s.Name!))
            .Select(g => (Key: g.Key, Count: g.Sum(x => x.Count), First: g.Min(x => x.SeenAt), Last: g.Max(x => x.SeenAt)))
            .ToList();
        if (rows.Count == 0) return;

        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            var ids = rows.Select(_ => Guid.CreateVersion7()).ToArray();
            var inns = rows.Select(r => r.Key.Item1).ToArray();
            var names = rows.Select(r => r.Key.Item3).ToArray();
            var normalized = rows.Select(r => Cut(CounterpartyNameNormalizer.Normalize(r.Key.Item3))).ToArray();
            var sources = rows.Select(r => r.Key.Source.ToString()).ToArray();
            var counts = rows.Select(r => r.Count).ToArray();
            var firsts = rows.Select(r => r.First.ToUniversalTime()).ToArray();
            var lasts = rows.Select(r => r.Last.ToUniversalTime()).ToArray();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO counterparty_aliases (id, tenant_id, counterparty_inn, name, normalized_name, source, occurrences, first_seen_at, last_seen_at)
                SELECT u.id, {tenantId}, u.inn, u.name, u.normalized, u.source, u.cnt, u.first_seen, u.last_seen
                FROM unnest({ids}, {inns}, {names}, {normalized}, {sources}, {counts}, {firsts}, {lasts})
                    AS u(id, inn, name, normalized, source, cnt, first_seen, last_seen)
                ON CONFLICT (tenant_id, counterparty_inn, source, name) DO UPDATE SET
                    occurrences = counterparty_aliases.occurrences + excluded.occurrences,
                    normalized_name = excluded.normalized_name,
                    first_seen_at = least(counterparty_aliases.first_seen_at, excluded.first_seen_at),
                    last_seen_at = greatest(counterparty_aliases.last_seen_at, excluded.last_seen_at)
                """, ct);
            return;
        }

        var keyInns = rows.Select(r => r.Key.Item1).Distinct().ToList();
        var existing = (await db.CounterpartyAliases.Where(a => keyInns.Contains(a.CounterpartyInn)).ToListAsync(ct))
            .Concat(db.CounterpartyAliases.Local.Where(a => keyInns.Contains(a.CounterpartyInn)))
            .DistinctBy(a => a.Id)
            .ToDictionary(a => (a.CounterpartyInn, a.Source, a.Name));
        foreach (var r in rows)
        {
            if (existing.TryGetValue(r.Key, out var alias))
            {
                alias.Occurrences += r.Count;
                if (r.First < alias.FirstSeenAt) alias.FirstSeenAt = r.First;
                if (r.Last > alias.LastSeenAt) alias.LastSeenAt = r.Last;
                continue;
            }
            db.CounterpartyAliases.Add(new CounterpartyAlias
            {
                TenantId = tenantId,
                CounterpartyInn = r.Key.Item1,
                Source = r.Key.Source,
                Name = r.Key.Item3,
                NormalizedName = Cut(CounterpartyNameNormalizer.Normalize(r.Key.Item3)),
                Occurrences = r.Count,
                FirstSeenAt = r.First,
                LastSeenAt = r.Last,
            });
        }
    }

    private static string Cut(string s) => s.Length > CounterpartyAlias.MaxNameLength ? s[..CounterpartyAlias.MaxNameLength] : s;
}

/// <summary>Rebuilds counterparty_aliases of the current tenant from already loaded operations and documents.</summary>
public sealed class CounterpartyAliasRebuilder(ISynectDbContext db, ITenantContext tenant, CounterpartyStore store)
{
    public static readonly TimeSpan LocalOffset = TimeSpan.FromHours(5);

    /// <summary>Deletes and refills the tenant's aliases in one transaction. Returns the number of spellings.</summary>
    public Task<int> RebuildAsync(CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Tenant context is not set.");
            await db.CounterpartyAliases.ExecuteDeleteAsync(token);

            var bank = await db.BankTransactions.AsNoTracking()
                .Where(t => t.CounterpartyInn != null && t.CounterpartyInn != "" && t.CounterpartyName != null && t.CounterpartyName != "")
                .GroupBy(t => new { t.CounterpartyInn, t.CounterpartyName })
                .Select(g => new { g.Key.CounterpartyInn, g.Key.CounterpartyName, Count = g.Count(), First = g.Min(t => t.PostingDate), Last = g.Max(t => t.PostingDate) })
                .ToListAsync(token);
            var edo = await db.EdoDocuments.AsNoTracking()
                .Where(d => d.CounterpartyInn != null && d.CounterpartyInn != "" && d.CounterpartyName != null && d.CounterpartyName != "")
                .GroupBy(d => new { d.CounterpartyInn, d.CounterpartyName })
                .Select(g => new { g.Key.CounterpartyInn, g.Key.CounterpartyName, Count = g.Count(), First = g.Min(d => d.DocumentDate), Last = g.Max(d => d.DocumentDate) })
                .ToListAsync(token);

            var sightings = new List<NameSighting>();
            foreach (var b in bank)
            {
                sightings.Add(new NameSighting(b.CounterpartyInn, b.CounterpartyName, CounterpartyNameSource.Bank, Local(b.First), b.Count - 1));
                sightings.Add(new NameSighting(b.CounterpartyInn, b.CounterpartyName, CounterpartyNameSource.Bank, Local(b.Last), 1));
            }
            foreach (var e in edo)
            {
                var first = e.First is { } f ? Local(f) : DateTimeOffset.UnixEpoch;
                var last = e.Last is { } l ? Local(l) : first;
                sightings.Add(new NameSighting(e.CounterpartyInn, e.CounterpartyName, CounterpartyNameSource.Edo, first, e.Count - 1));
                sightings.Add(new NameSighting(e.CounterpartyInn, e.CounterpartyName, CounterpartyNameSource.Edo, last, 1));
            }
            await store.RecordNamesAsync(tenantId, sightings, token);
            await db.SaveChangesAsync(token);
            return sightings.Select(s => (s.Inn, s.Name, s.Source)).Distinct().Count();
        }, ct);

    public static DateTimeOffset Local(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), LocalOffset);
}
