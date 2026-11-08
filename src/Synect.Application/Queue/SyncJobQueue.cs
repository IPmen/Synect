using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Sync;

namespace Synect.Application.Queue;

/// <summary>Writes jobs into sync_jobs. Must be called inside a tenant transaction.</summary>
public sealed class SyncJobQueue(ISynectDbContext db, TimeProvider time)
{
    /// <summary>The Queued or Running job of a connection, if any.</summary>
    public Task<SyncJob?> ActiveJobAsync(Guid connectionId, CancellationToken ct) =>
        db.SyncJobs.SingleOrDefaultAsync(j => j.ConnectionId == connectionId
            && (j.Status == SyncJobStatus.Queued || j.Status == SyncJobStatus.Running), ct);

    /// <summary>
    /// Adds a job unless the connection already has an active one. Returns (job, created).
    /// The partial unique index on (tenant_id, connection_id) for active jobs closes the race between two writers.
    /// </summary>
    public async Task<(SyncJob Job, bool Created)> EnqueueAsync(
        Connection connection, SyncJobKind kind, DateTimeOffset? scheduledFor, CancellationToken ct)
    {
        if (await ActiveJobAsync(connection.Id, ct) is { } active) return (active, false);

        var job = SyncJob.Create(connection.TenantId, connection.Id, kind, time.GetUtcNow(), scheduledFor);
        db.SyncJobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
            return (job, true);
        }
        catch (DbUpdateException)
        {
            // Another writer enqueued first.
            db.ChangeTracker.Entries<SyncJob>().Where(e => e.Entity == job).ToList().ForEach(e => e.State = EntityState.Detached);
            var winner = await ActiveJobAsync(connection.Id, ct);
            if (winner is null) throw;
            return (winner, false);
        }
    }
}
