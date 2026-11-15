using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Ingest;
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
    DateTimeOffset? LastRunFinishedAt)
{
    /// <summary>
    /// The source's own staleness threshold, when it has one: for 1C (pushed packages) the accounting threshold
    /// (Ingest:AccountingStaleAfter); null for the other sources (readers apply their usual threshold, e.g. Mcp:StaleAfter).
    /// </summary>
    public TimeSpan? StaleAfter { get; init; }

    /// <summary>
    /// For sources with their own threshold (1C): a data group it receives is stale, or it never received anything
    /// (<see cref="IngestFreshnessGroups.IsStale"/>).
    /// </summary>
    public bool? IsStale { get; init; }

    /// <summary>1C only: freshness of the accounting and the catalog data separately; null for the other sources.</summary>
    public IReadOnlyList<IngestGroupFreshness>? DataGroups { get; init; }
}

public interface ISourceFreshnessQuery
{
    /// <summary>Freshness of every connection of the tenant. Must be called in that tenant's context.</summary>
    Task<IReadOnlyList<SourceFreshness>> GetAsync(Guid tenantId, CancellationToken ct);
}

/// <remarks>
/// A 1C connection (<see cref="AdapterKind.Accounting1C"/>) has no sync runs: its last successful load is the time of the
/// last accepted package of any kind, and its freshness is judged per data group (ingest_freshness): accounting after
/// Ingest:AccountingStaleAfter (48 h), catalog after Ingest:CatalogStaleAfter (60 min).
/// </remarks>
public sealed class SourceFreshnessQuery(ISynectDbContext db, ITenantContext tenant, IOptions<IngestOptions> ingest, TimeProvider time) : ISourceFreshnessQuery
{
    public Task<IReadOnlyList<SourceFreshness>> GetAsync(Guid tenantId, CancellationToken ct)
    {
        if (tenant.TenantId != tenantId)
            throw new InvalidOperationException("Freshness can only be read in the context of the same tenant.");

        return db.InTransactionAsync<IReadOnlyList<SourceFreshness>>(async token =>
        {
            var connections = await db.Connections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(token);
            var groups = connections.Any(c => c.AdapterKind == AdapterKind.Accounting1C)
                ? await db.IngestFreshness.AsNoTracking().ToListAsync(token)
                : [];
            var now = time.GetUtcNow();
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

                var freshness = new SourceFreshness(c.Id, c.Name, c.SourceCode, c.AdapterKind, c.Status, c.StatusReason,
                    c.LastSuccessfulSyncAt, lastSuccess?.IsComplete, lastRun?.Result, lastRun?.FinishedAt);
                if (c.AdapterKind == AdapterKind.Accounting1C)
                {
                    var dataGroups = IngestFreshnessGroups.Evaluate(
                        groups.Where(g => g.ConnectionId == c.Id).ToDictionary(g => g.DataGroup, g => g.LastAcceptedAt), ingest.Value, now);
                    freshness = freshness with
                    {
                        StaleAfter = ingest.Value.AccountingStaleAfterOrDefault,
                        IsStale = IngestFreshnessGroups.IsStale(dataGroups),
                        DataGroups = dataGroups,
                    };
                }
                result.Add(freshness);
            }
            return result;
        }, ct);
    }
}
