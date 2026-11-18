namespace Synect.Application.Queue;

/// <summary>
/// Graceful stop of the sync queue (one per process). <see cref="BeginStop"/>: the dispatcher claims no new jobs, running
/// jobs continue. <see cref="CancelJobs"/>: running jobs are cancelled; a job cancelled this way goes back to Queued
/// (it is not a failed load and does not count for the circuit breaker) and the next worker process runs it again.
/// </summary>
public sealed class SyncShutdown
{
    private readonly CancellationTokenSource _jobs = new();
    private volatile bool _stopping;

    /// <summary>True after <see cref="BeginStop"/>: no new job may be claimed.</summary>
    public bool IsStopping => _stopping;

    /// <summary>Cancelled by <see cref="CancelJobs"/>; every running job observes it.</summary>
    public CancellationToken JobsToken => _jobs.Token;

    /// <summary>True when running jobs were cancelled because the worker is stopping.</summary>
    public bool JobsCancelled => _jobs.IsCancellationRequested;

    public void BeginStop() => _stopping = true;

    public void CancelJobs()
    {
        _stopping = true;
        try { _jobs.Cancel(); } catch (ObjectDisposedException) { }
    }
}
