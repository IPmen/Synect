using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Sync;
using Synect.Domain.Sync;

namespace Synect.Application.Queue;

/// <summary>
/// The queue engine. Each <see cref="TickAsync"/>: for every tenant, recover jobs whose heartbeat stopped,
/// plan due jobs and collect queued ones; then start jobs by priority while respecting the global limit,
/// the per-provider limit and "one load per connection". Every job runs in its own task, DI scope and
/// tenant context with a timeout and a periodic heartbeat, so a failing or hanging load never blocks the others.
/// </summary>
public sealed class SyncDispatcher(
    TenantScopeRunner tenants,
    IAdapterRegistry registry,
    IOptions<SyncQueueOptions> options,
    TimeProvider time,
    ILogger<SyncDispatcher> logger)
{
    private sealed record Candidate(Guid TenantId, Guid JobId, Guid ConnectionId, int Priority, DateTimeOffset EnqueuedAt,
        string SourceCode, ConnectionStatus ConnectionStatus);

    private readonly object _gate = new();
    private readonly HashSet<Guid> _running = new();
    private readonly HashSet<Guid> _busyConnections = new();
    private readonly Dictionary<string, int> _perProvider = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource _idle = CompletedSignal();

    public int RunningCount { get { lock (_gate) return _running.Count; } }

    /// <summary>Plans and starts jobs; returns how many were started. Does not wait for them.</summary>
    public async Task<int> TickAsync(CancellationToken stoppingToken)
    {
        var candidates = new List<Candidate>();
        foreach (var tenantId in await tenants.TenantIdsAsync(stoppingToken))
        {
            try
            {
                candidates.AddRange(await tenants.InTenantAsync(tenantId, async (sp, ct) =>
                {
                    await sp.GetRequiredService<SyncRecovery>().RecoverStaleAsync(ct);
                    await sp.GetRequiredService<SyncPlanner>().PlanAsync(ct);
                    // Time-based attention rules (stale data, waiting documents, snooze end); due once per Attention:PeriodicInterval.
                    await sp.GetRequiredService<Attention.AttentionTrigger>().PeriodicAsync(ct);
                    var db = sp.GetRequiredService<ISynectDbContext>();
                    var now = time.GetUtcNow();
                    return await db.InTransactionAsync(token =>
                        (from j in db.SyncJobs
                         join c in db.Connections on j.ConnectionId equals c.Id
                         where j.Status == SyncJobStatus.Queued && j.NotBefore <= now
                         select new Candidate(j.TenantId, j.Id, j.ConnectionId, j.Priority, j.EnqueuedAt, c.SourceCode, c.Status))
                        .ToListAsync(token), ct);
                }, stoppingToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One tenant's problem must not stop the others.
                logger.LogError("Planning failed for tenant {TenantId}: {ErrorType}", tenantId, ex.GetType().Name);
            }
        }

        var started = 0;
        foreach (var candidate in candidates.OrderBy(c => c.Priority).ThenBy(c => c.EnqueuedAt).ThenBy(c => c.JobId))
        {
            if (candidate.ConnectionStatus != ConnectionStatus.Active)
            {
                await FinishJobAsync(candidate, SyncJobStatus.Cancelled, null, "Connection is not active.");
                continue;
            }

            string provider;
            try { provider = registry.Get(candidate.SourceCode).ProviderName; }
            catch (KeyNotFoundException)
            {
                await FinishJobAsync(candidate, SyncJobStatus.Failed, null, $"No adapter for source '{candidate.SourceCode}'.");
                continue;
            }

            if (!TryReserve(candidate, provider, out var globalLimitReached))
            {
                if (globalLimitReached) break;
                continue;
            }

            var claimed = false;
            try
            {
                claimed = await ClaimAsync(candidate, stoppingToken);
            }
            finally
            {
                // A failed or lost claim must give the reserved slot back, otherwise WaitForIdleAsync never returns.
                if (!claimed) Release(candidate, provider);
            }
            if (!claimed) continue;

            _ = Task.Run(() => ExecuteAsync(candidate, provider, stoppingToken), CancellationToken.None);
            started++;
        }
        return started;
    }

    /// <summary>Completes when no job is reserved or running (tests, shutdown). Does not spin.</summary>
    public Task WaitForIdleAsync()
    {
        lock (_gate) return _idle.Task;
    }

    private bool TryReserve(Candidate c, string provider, out bool globalLimitReached)
    {
        lock (_gate)
        {
            globalLimitReached = _running.Count >= options.Value.MaxConcurrent;
            if (globalLimitReached) return false;
            if (_busyConnections.Contains(c.ConnectionId)) return false;
            if (_perProvider.GetValueOrDefault(provider) >= options.Value.MaxConcurrentFor(provider)) return false;

            if (_running.Count == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running.Add(c.JobId);
            _busyConnections.Add(c.ConnectionId);
            _perProvider[provider] = _perProvider.GetValueOrDefault(provider) + 1;
            return true;
        }
    }

    private void Release(Candidate c, string provider)
    {
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            if (!_running.Remove(c.JobId)) return;
            _busyConnections.Remove(c.ConnectionId);
            if (_perProvider.TryGetValue(provider, out var n)) _perProvider[provider] = Math.Max(0, n - 1);
            if (_running.Count == 0) idle = _idle;
        }
        idle?.TrySetResult();
    }

    private async Task<bool> ClaimAsync(Candidate c, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        // Conditional update: only one dispatcher can move the job from Queued to Running.
        var rows = await tenants.InTenantAsync(c.TenantId, (sp, token) =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            return db.InTransactionAsync(t => db.SyncJobs
                .Where(j => j.Id == c.JobId && j.Status == SyncJobStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, SyncJobStatus.Running)
                    .SetProperty(j => j.StartedAt, now)
                    .SetProperty(j => j.HeartbeatAt, now), t), token);
        }, ct);
        return rows == 1;
    }

    private async Task ExecuteAsync(Candidate c, string provider, CancellationToken stoppingToken)
    {
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatLoopAsync(c, heartbeatStop.Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(options.Value.JobTimeout);
            try
            {
                var outcome = await tenants.InTenantAsync(c.TenantId,
                    (sp, ct) => sp.GetRequiredService<ConnectionSyncService>().SyncAsync(c.ConnectionId, ct), timeout.Token);
                await StopAsync(heartbeatStop, heartbeat);
                await FinishJobAsync(c, outcome.Result == SyncRunResult.Succeeded ? SyncJobStatus.Succeeded : SyncJobStatus.Failed,
                    outcome.SyncRunId, outcome.Error);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                var reason = stoppingToken.IsCancellationRequested ? "Worker is stopping." : $"Timed out after {options.Value.JobTimeout}.";
                logger.LogWarning("Job {JobId} for connection {ConnectionId} cancelled: {Reason}", c.JobId, c.ConnectionId, reason);
                await StopAsync(heartbeatStop, heartbeat);
                await FinishJobAsync(c, SyncJobStatus.Failed, null, reason);
            }
            catch (Exception ex)
            {
                logger.LogError("Job {JobId} for connection {ConnectionId} failed: {ErrorType}", c.JobId, c.ConnectionId, ex.GetType().Name);
                await StopAsync(heartbeatStop, heartbeat);
                await FinishJobAsync(c, SyncJobStatus.Failed, null, ex.Message);
            }
        }
        finally
        {
            await StopAsync(heartbeatStop, heartbeat);
            Release(c, provider);
        }
    }

    /// <summary>Writes heartbeat_at every HeartbeatInterval while the job runs.</summary>
    private async Task HeartbeatLoopAsync(Candidate c, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(options.Value.HeartbeatInterval, time);
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now = time.GetUtcNow();
                await tenants.InTenantAsync(c.TenantId, (sp, token) =>
                {
                    var db = sp.GetRequiredService<ISynectDbContext>();
                    return db.InTransactionAsync(t => db.SyncJobs
                        .Where(j => j.Id == c.JobId && j.Status == SyncJobStatus.Running)
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.HeartbeatAt, now), t), token);
                }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // A missed heartbeat only risks an early stale verdict; the load itself continues.
            logger.LogWarning("Heartbeat for job {JobId} failed: {ErrorType}", c.JobId, ex.GetType().Name);
        }
    }

    private static async Task StopAsync(CancellationTokenSource stop, Task heartbeat)
    {
        if (!stop.IsCancellationRequested) await stop.CancelAsync();
        await heartbeat;
    }

    private async Task FinishJobAsync(Candidate c, SyncJobStatus status, Guid? runId, string? error)
    {
        try
        {
            await tenants.InTenantAsync(c.TenantId, async (sp, ct) =>
            {
                var db = sp.GetRequiredService<ISynectDbContext>();
                await db.InTransactionAsync(async t =>
                {
                    var job = await db.SyncJobs.SingleAsync(j => j.Id == c.JobId, t);
                    job.Finish(status, time.GetUtcNow(), runId, error);
                    await db.SaveChangesAsync(t);
                }, ct);
                return true;
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The heartbeat-based recovery will eventually mark the job as failed.
            logger.LogError("Could not finish job {JobId}: {ErrorType}", c.JobId, ex.GetType().Name);
        }
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }
}
