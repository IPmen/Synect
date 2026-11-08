using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Domain.Analytics;
using Synect.Domain.Banking;
using Synect.Domain.Edo;

namespace Synect.Application.Analytics.Totals;

/// <summary>(counterparty INN, first day of month). Empty INN groups payments without an INN.</summary>
public readonly record struct CounterpartyMonthKey(string Inn, DateOnly Month)
{
    public static CounterpartyMonthKey Of(string? inn, DateOnly date) =>
        new(string.IsNullOrWhiteSpace(inn) ? string.Empty : inn.Trim(), new DateOnly(date.Year, date.Month, 1));
}

/// <summary>
/// Scoped collector of the (INN, month) pairs touched by the current load. Sync services add both the
/// old and the new key of every changed row, so a row that moved to another month or INN refreshes both.
/// </summary>
public sealed class AnalyticsChangeSet
{
    private readonly HashSet<CounterpartyMonthKey> _keys = [];

    public IReadOnlyCollection<CounterpartyMonthKey> Keys => _keys;

    public void Add(string? inn, DateOnly? date)
    {
        if (date is { } d) _keys.Add(CounterpartyMonthKey.Of(inn, d));
    }

    public void Clear() => _keys.Clear();
}

/// <summary>
/// Maintains analytics_counterparty_monthly. Recomputes exactly the given keys from the source tables and
/// upserts them (rows of other months are not touched). Idempotent: running it twice changes nothing.
/// Uses LINQ so that it runs on PostgreSQL and on the SQLite test database alike.
/// </summary>
public sealed class CounterpartyMonthlyRefresher(ISynectDbContext db, IOptions<AnalyticsOptions> options, TimeProvider time)
{
    /// <summary>Recomputes the given keys. Call inside a tenant transaction. Returns the number of rows changed.</summary>
    public async Task<int> RefreshAsync(IReadOnlyCollection<CounterpartyMonthKey> keys, CancellationToken ct)
    {
        if (keys.Count == 0) return 0;
        var changed = 0;
        // Bounded batches keep the IN lists and the month range reasonable.
        foreach (var batch in keys.OrderBy(k => k.Month).ThenBy(k => k.Inn).Chunk(2000))
            changed += await RefreshBatchAsync(batch, ct);
        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>Recomputes every key that exists in the source tables (initial fill, repair).</summary>
    public async Task<int> RebuildAllAsync(CancellationToken ct)
    {
        var bankKeys = await db.BankTransactions.WithoutErrors()
            .Select(t => new { Inn = t.CounterpartyInn ?? "", t.PostingDate.Year, t.PostingDate.Month })
            .Distinct().ToListAsync(ct);
        var edoKeys = await db.EdoDocuments.Where(d => d.DocumentDate != null)
            .Select(d => new { Inn = d.CounterpartyInn ?? "", d.DocumentDate!.Value.Year, d.DocumentDate.Value.Month })
            .Distinct().ToListAsync(ct);
        var existing = await db.CounterpartyMonthly
            .Select(r => new { Inn = r.CounterpartyInn, r.Month.Year, r.Month.Month })
            .Distinct().ToListAsync(ct);
        var keys = bankKeys.Concat(edoKeys).Concat(existing)
            .Select(k => CounterpartyMonthKey.Of(k.Inn, new DateOnly(k.Year, k.Month, 1)))
            .ToHashSet();
        return await RefreshAsync(keys, ct);
    }

    private async Task<int> RefreshBatchAsync(IReadOnlyCollection<CounterpartyMonthKey> keys, CancellationToken ct)
    {
        var keySet = keys.ToHashSet();
        var inns = keys.Select(k => k.Inn).Distinct().ToList();
        var from = keys.Min(k => k.Month);
        var to = keys.Max(k => k.Month).AddMonths(1);
        var o = options.Value.Edo;
        var counted = o.CountedStatuses;

        var bank = await db.BankTransactions.WithoutErrors()
            .Where(t => t.PostingDate >= from && t.PostingDate < to && inns.Contains(t.CounterpartyInn ?? ""))
            .GroupBy(t => new { Inn = t.CounterpartyInn ?? "", t.PostingDate.Year, t.PostingDate.Month, t.Currency })
            .Select(g => new
            {
                g.Key.Inn, g.Key.Year, g.Key.Month, g.Key.Currency,
                Incoming = g.Sum(t => t.Direction == TransactionDirection.Incoming ? t.Amount : 0L),
                Outgoing = g.Sum(t => t.Direction == TransactionDirection.Outgoing ? t.Amount : 0L),
                Count = g.Count(),
            })
            .ToListAsync(ct);

        var edo = await db.EdoDocuments
            .Where(d => d.DocumentDate != null && d.DocumentDate >= from && d.DocumentDate < to
                        && counted.Contains(d.Status) && inns.Contains(d.CounterpartyInn ?? ""))
            .GroupBy(d => new { Inn = d.CounterpartyInn ?? "", d.DocumentDate!.Value.Year, d.DocumentDate.Value.Month, d.Owner })
            .Select(g => new { g.Key.Inn, g.Key.Year, g.Key.Month, g.Key.Owner, Total = g.Sum(d => d.TotalSum), Count = g.Count() })
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var computed = new Dictionary<(CounterpartyMonthKey Key, string Currency), CounterpartyMonthly>();
        CounterpartyMonthly Row(CounterpartyMonthKey key, string currency)
        {
            if (!computed.TryGetValue((key, currency), out var row))
            {
                row = new CounterpartyMonthly { CounterpartyInn = key.Inn, Month = key.Month, Currency = currency, UpdatedAt = now };
                computed[(key, currency)] = row;
            }
            return row;
        }

        foreach (var b in bank)
        {
            var key = new CounterpartyMonthKey(b.Inn, new DateOnly(b.Year, b.Month, 1));
            if (!keySet.Contains(key)) continue;
            var row = Row(key, b.Currency);
            row.BankIncoming = b.Incoming;
            row.BankOutgoing = b.Outgoing;
            row.BankCount = b.Count;
        }
        foreach (var e in edo)
        {
            var key = new CounterpartyMonthKey(e.Inn, new DateOnly(e.Year, e.Month, 1));
            if (!keySet.Contains(key)) continue;
            var row = Row(key, o.Currency);
            if (e.Owner == EdoDocumentOwner.Incoming) { row.EdoIncomingTotal = e.Total; row.EdoIncomingCount = e.Count; }
            else { row.EdoOutgoingTotal = e.Total; row.EdoOutgoingCount = e.Count; }
        }

        var existing = await db.CounterpartyMonthly
            .Where(r => r.Month >= from && r.Month < to && inns.Contains(r.CounterpartyInn))
            .ToListAsync(ct);

        var changed = 0;
        foreach (var row in existing)
        {
            var key = new CounterpartyMonthKey(row.CounterpartyInn, row.Month);
            if (!keySet.Contains(key)) continue;
            if (computed.Remove((key, row.Currency), out var fresh))
            {
                if (row.SameValuesAs(fresh)) continue;
                row.BankIncoming = fresh.BankIncoming;
                row.BankOutgoing = fresh.BankOutgoing;
                row.BankCount = fresh.BankCount;
                row.EdoIncomingTotal = fresh.EdoIncomingTotal;
                row.EdoOutgoingTotal = fresh.EdoOutgoingTotal;
                row.EdoIncomingCount = fresh.EdoIncomingCount;
                row.EdoOutgoingCount = fresh.EdoOutgoingCount;
                row.UpdatedAt = now;
            }
            else
            {
                db.CounterpartyMonthly.Remove(row); // nothing left for this key and currency
            }
            changed++;
        }

        foreach (var row in computed.Values)
        {
            db.CounterpartyMonthly.Add(row);
            changed++;
        }
        return changed;
    }
}
