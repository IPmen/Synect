using Synect.Domain.Common;

namespace Synect.Domain.Sync;

public enum SyncRunResult
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>One execution of a data load for a single connection.</summary>
public class SyncRun : TenantEntity
{
    public Guid ConnectionId { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public SyncRunResult Result { get; private set; } = SyncRunResult.Running;

    /// <summary>
    /// True when the source confirmed the loaded data is final for the whole requested range
    /// (e.g. every bank day is closed, every EDO page was read).
    /// </summary>
    public bool IsComplete { get; private set; }

    /// <summary>Number of source records received during the run (including unchanged ones).</summary>
    public int RecordCount { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Requested range (inclusive), when the run is date based.</summary>
    public DateOnly? RangeFrom { get; private set; }
    public DateOnly? RangeTo { get; private set; }

    private SyncRun() { }

    public static SyncRun Start(Guid tenantId, Guid connectionId, DateTimeOffset at, DateOnly? from = null, DateOnly? to = null) =>
        new()
        {
            TenantId = tenantId,
            ConnectionId = connectionId,
            StartedAt = at,
            RangeFrom = from,
            RangeTo = to,
        };

    public void Succeed(DateTimeOffset at, bool isComplete, int recordCount)
    {
        Result = SyncRunResult.Succeeded;
        FinishedAt = at;
        IsComplete = isComplete;
        RecordCount = recordCount;
    }

    /// <summary>Records the date range actually loaded when it is only known during the run.</summary>
    public void SetRange(DateOnly from, DateOnly to)
    {
        RangeFrom = from;
        RangeTo = to;
    }

    /// <summary>The worker died while this run was in progress.</summary>
    public void Abandon(DateTimeOffset at) =>
        Fail(at, "Abandoned: no progress within the stale-run timeout (worker stopped?).", RecordCount);

    public void Fail(DateTimeOffset at, string error, int recordCount)
    {
        Result = SyncRunResult.Failed;
        FinishedAt = at;
        IsComplete = false;
        RecordCount = recordCount;
        Error = error.Length > 2000 ? error[..2000] : error;
    }
}
