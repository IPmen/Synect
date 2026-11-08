using Synect.Domain.Common;

namespace Synect.Domain.Sync;

public enum SyncJobKind
{
    /// <summary>First load of a new connection. Low priority.</summary>
    Initial = 1,

    /// <summary>Planned by the scheduler (nightly window or daytime refresh).</summary>
    Scheduled = 2,

    /// <summary>"Refresh now" requested by a user.</summary>
    Manual = 3,

    /// <summary>First load after a connection was reactivated.</summary>
    Reactivation = 4,
}

public enum SyncJobStatus
{
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>
/// A queued or executed load of one connection. At most one job per connection can be Queued or
/// Running at a time (enforced by a partial unique index), which rules out overlapping loads.
/// </summary>
public class SyncJob : TenantEntity
{
    public const int PriorityManual = 0;
    public const int PriorityNormal = 1;
    public const int PriorityLow = 2;

    public Guid ConnectionId { get; private set; }
    public SyncJobKind Kind { get; private set; }

    /// <summary>Lower runs first.</summary>
    public int Priority { get; private set; }

    public SyncJobStatus Status { get; private set; } = SyncJobStatus.Queued;

    /// <summary>Schedule slot this job was created for (dedupe key for scheduled jobs).</summary>
    public DateTimeOffset? ScheduledFor { get; private set; }

    public DateTimeOffset EnqueuedAt { get; private set; }
    public DateTimeOffset NotBefore { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>Last sign of life of a Running job, written periodically by the dispatcher.</summary>
    public DateTimeOffset? HeartbeatAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public Guid? SyncRunId { get; private set; }
    public string? Error { get; private set; }

    private SyncJob() { }

    public static SyncJob Create(Guid tenantId, Guid connectionId, SyncJobKind kind, DateTimeOffset now, DateTimeOffset? scheduledFor = null) =>
        new()
        {
            TenantId = tenantId,
            ConnectionId = connectionId,
            Kind = kind,
            Priority = PriorityFor(kind),
            EnqueuedAt = now,
            NotBefore = now,
            ScheduledFor = scheduledFor,
        };

    public static int PriorityFor(SyncJobKind kind) => kind switch
    {
        SyncJobKind.Manual => PriorityManual,
        SyncJobKind.Initial => PriorityLow,
        _ => PriorityNormal,
    };

    public bool IsActive => Status is SyncJobStatus.Queued or SyncJobStatus.Running;

    /// <summary>A user asked for a refresh while this job waits: run it as soon as possible.</summary>
    public void Expedite(DateTimeOffset now)
    {
        if (Status != SyncJobStatus.Queued) return;
        Priority = Math.Min(Priority, PriorityManual);
        if (NotBefore > now) NotBefore = now;
    }

    public void Finish(SyncJobStatus status, DateTimeOffset at, Guid? syncRunId, string? error)
    {
        Status = status;
        FinishedAt = at;
        SyncRunId = syncRunId ?? SyncRunId;
        Error = error is { Length: > 2000 } ? error[..2000] : error;
    }
}
