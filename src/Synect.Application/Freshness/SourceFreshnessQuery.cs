using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Sync;

namespace Synect.Application.Freshness;

/// <summary>How fresh the data of one source is, for freshness notes in tool and API answers.</summary>
/// <param name="LastSuccessfulSyncAt">Time of the last successful load; null if never loaded.</param>
/// <param name="LastSuccessfulRunIsComplete">Whether that successful load was complete (e.g. every bank day closed).</param>
/// <param name="LastRunResult">Result of the most recent finished load, successful or not.</param>
public sealed record SourceFreshness(
    Guid ConnectionId,
    string ConnectionName,
    string SourceCode,
    AdapterKind Kind,
    ConnectionStatus Status,
    string? StatusReason,
    DateTimeOffset? LastSuccessfulSyncAt,
    bool? LastSuccessfulRunIsComplete,
    SyncRunResult? LastRunResult,
    DateTimeOffset? LastRunFinishedAt);

public interface ISourceFreshnessQuery
{
    /// <summary>Freshness of every connection of the tenant. Must be called in that tenant's context.</summary>
    Task<IReadOnlyList<SourceFreshness>> GetAsync(Guid tenantId, CancellationToken ct);
}

public sealed class SourceFreshnessQuery(ISynectDbContext db, ITenantContext tenant) : ISourceFreshnessQuery
{
    public Task<IReadOnlyList<SourceFreshness>> GetAsync(Guid tenantId, CancellationToken ct)
    {
        if (tenant.TenantId != tenantId)
            throw new InvalidOperationException("Freshness can only be read in the context of the same tenant.");

        return db.InTransactionAsync<IReadOnlyList<SourceFreshness>>(async token =>
        {
            var connections = await db.Connections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(token);
            var result = new List<SourceFreshness>(connections.Count);
            foreach (var c in connections)
            {
                var lastSuccess = await db.SyncRuns.AsNoTracking()
                    .Where(r => r.ConnectionId == c.Id && r.Result == SyncRunResult.Succeeded)
                    .OrderByDescending(r => r.FinishedAt)
                    .Select(r => new { r.IsComplete })
                    .FirstOrDefaultAsync(token);
                var lastRun = await db.SyncRuns.AsNoTracking()
                    .Where(r => r.ConnectionId == c.Id && r.Result != SyncRunResult.Running)
                    .OrderByDescending(r => r.FinishedAt)
                    .Select(r => new { r.Result, r.FinishedAt })
                    .FirstOrDefaultAsync(token);

                result.Add(new SourceFreshness(c.Id, c.Name, c.SourceCode, c.AdapterKind, c.Status, c.StatusReason,
                    c.LastSuccessfulSyncAt, lastSuccess?.IsComplete, lastRun?.Result, lastRun?.FinishedAt));
            }
            return result;
        }, ct);
    }
}
