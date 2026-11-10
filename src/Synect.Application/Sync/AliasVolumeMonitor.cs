using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Analytics.Counterparties;

namespace Synect.Application.Sync;

/// <summary>
/// Warns (log level Warning, at most once a day per tenant and process) when a tenant has more counterparty name
/// spellings than Search:AliasWarningThreshold: name search compares one tenant's spellings one by one under RLS, so
/// very large alias tables get slow (see the README).
/// </summary>
public sealed class AliasVolumeMonitor(TimeProvider time, IOptions<SearchOptions> options, ILogger<AliasVolumeMonitor> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastWarning = new();

    /// <summary>Counts the tenant's aliases (inside the caller's tenant transaction) and logs a warning when over the threshold.</summary>
    public async Task CheckAsync(ISynectDbContext db, Guid tenantId, CancellationToken ct)
    {
        var threshold = options.Value.AliasWarningThreshold;
        if (threshold <= 0) return;
        var now = time.GetUtcNow();
        if (_lastWarning.TryGetValue(tenantId, out var last) && now - last < Interval) return;

        var count = await db.CounterpartyAliases.CountAsync(ct);
        if (count <= threshold) return;
        if (_lastWarning.TryGetValue(tenantId, out last) && now - last < Interval) return;
        _lastWarning[tenantId] = now;
        logger.LogWarning(
            "Tenant {TenantId} has {AliasCount} counterparty name spellings, more than Search:AliasWarningThreshold = {Threshold}; name search may become slow",
            tenantId, count, threshold);
    }
}
