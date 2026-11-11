using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Domain.Sync;

namespace Synect.Application.Queue;

/// <summary>
/// Per-tenant scheduler step: queues initial loads of new connections (low priority) and scheduled
/// loads whose slot is due. Only Active connections are planned.
/// </summary>
public sealed class SyncPlanner(ISynectDbContext db, SyncJobQueue queue, SyncScheduleCalculator schedule, TimeProvider time)
{
    public Task<int> PlanAsync(CancellationToken ct) => db.InTransactionAsync(async token =>
    {
        var now = time.GetUtcNow();
        var queued = 0;
        var connections = await db.Connections
            .Where(c => c.Status == ConnectionStatus.Active && (c.AdapterKind == AdapterKind.Bank || c.AdapterKind == AdapterKind.Edo))
            .ToListAsync(token);

        foreach (var connection in connections)
        {
            var hasJobs = await db.SyncJobs.AnyAsync(j => j.ConnectionId == connection.Id, token);
            if (connection.LastSuccessfulSyncAt is null && !hasJobs)
            {
                if ((await queue.EnqueueAsync(connection, SyncJobKind.Initial, null, token)).Created) queued++;
                continue;
            }

            if (schedule.CurrentSlot(connection.Id, connection.AdapterKind, now) is not { } slot) continue;

            // The slot is covered by a job created for it, or by any job enqueued after it started (e.g. manual).
            var covered = await db.SyncJobs.AnyAsync(j => j.ConnectionId == connection.Id
                && (j.ScheduledFor == slot.Start || j.EnqueuedAt >= slot.Start), token);
            if (covered) continue;

            if ((await queue.EnqueueAsync(connection, SyncJobKind.Scheduled, slot.Start, token)).Created) queued++;
        }
        return queued;
    }, ct);
}

/// <summary>Marks runs and jobs that can no longer finish as Failed (is_complete = false).</summary>
public sealed class SyncRecovery(ISynectDbContext db, TimeProvider time, IOptions<SyncQueueOptions> options)
{
    public const string RestartReason = "Abandoned: the worker restarted while the job was running.";
    public const string HeartbeatReason = "Abandoned: no heartbeat within the timeout.";

    /// <summary>
    /// Called once at worker startup. The worker runs as a single instance, so nothing can be running yet:
    /// every Running job and run belongs to a process that died.
    /// </summary>
    public Task<int> RecoverAfterRestartAsync(CancellationToken ct) => db.InTransactionAsync(async token =>
    {
        var now = time.GetUtcNow();
        var jobs = await db.SyncJobs.Where(j => j.Status == SyncJobStatus.Running).ToListAsync(token);
        foreach (var job in jobs) job.Finish(SyncJobStatus.Failed, now, null, RestartReason);
        var runs = await db.SyncRuns.Where(r => r.Result == SyncRunResult.Running).ToListAsync(token);
        foreach (var run in runs) run.Abandon(now, RestartReason);
        if (jobs.Count + runs.Count > 0) await db.SaveChangesAsync(token);
        return jobs.Count + runs.Count;
    }, ct);

    /// <summary>
    /// Called on every dispatcher tick: fails Running jobs whose heartbeat is older than HeartbeatTimeout,
    /// together with the Running runs of their connections, and runs left Running without any Running job.
    /// </summary>
    public Task<int> RecoverStaleAsync(CancellationToken ct) => db.InTransactionAsync(async token =>
    {
        var now = time.GetUtcNow();
        var cutoff = now - options.Value.HeartbeatTimeout;

        var staleJobs = await db.SyncJobs
            .Where(j => j.Status == SyncJobStatus.Running && (j.HeartbeatAt ?? j.StartedAt) < cutoff)
            .ToListAsync(token);
        foreach (var job in staleJobs) job.Finish(SyncJobStatus.Failed, now, null, HeartbeatReason);
        var staleConnections = staleJobs.Select(j => j.ConnectionId).ToList();

        var liveConnections = await db.SyncJobs
            .Where(j => j.Status == SyncJobStatus.Running && !staleConnections.Contains(j.ConnectionId))
            .Select(j => j.ConnectionId)
            .ToListAsync(token);
        var runs = await db.SyncRuns
            .Where(r => r.Result == SyncRunResult.Running
                && (staleConnections.Contains(r.ConnectionId) || (!liveConnections.Contains(r.ConnectionId) && r.StartedAt < cutoff)))
            .ToListAsync(token);
        foreach (var run in runs) run.Abandon(now, HeartbeatReason);

        if (staleJobs.Count + runs.Count > 0) await db.SaveChangesAsync(token);
        return staleJobs.Count + runs.Count;
    }, ct);
}
