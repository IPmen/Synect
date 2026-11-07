using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Counterparties;

namespace Synect.Application.Sync;

/// <summary>Maintains the tenant's counterparty directory keyed by INN.</summary>
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
}
