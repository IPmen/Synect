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
            .Where(c => c.Status == ConnectionStatus.Active && c.AdapterKind != AdapterKind.Accounting)
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

/// <summary>Marks runs and jobs left Running by a crashed worker as Failed.</summary>
public sealed class SyncRecovery(ISynectDbContext db, TimeProvider time, IOptions<SyncQueueOptions> options)
{
    public Task<int> RecoverAsync(CancellationToken ct) => db.InTransactionAsync(async token =>
    {
        var now = time.GetUtcNow();
        var cutoff = now - options.Value.StaleRunTimeout;

        var runs = await db.SyncRuns.Where(r => r.Result == SyncRunResult.Running && r.StartedAt < cutoff).ToListAsync(token);
        foreach (var run in runs) run.Abandon(now);

        var jobs = await db.SyncJobs.Where(j => j.Status == SyncJobStatus.Running && j.StartedAt < cutoff).ToListAsync(token);
        foreach (var job in jobs) job.Finish(SyncJobStatus.Failed, now, null, "Abandoned: worker stopped while the job was running.");

        if (runs.Count + jobs.Count > 0) await db.SaveChangesAsync(token);
        return runs.Count + jobs.Count;
    }, ct);
}
