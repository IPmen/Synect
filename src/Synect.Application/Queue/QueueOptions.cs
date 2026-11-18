namespace Synect.Application.Queue;

/// <summary>Sync:Queue.</summary>
public sealed class SyncQueueOptions
{
    /// <summary>How often the worker plans and dispatches jobs.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Loads running at the same time, all connections together.</summary>
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>Loads running at the same time per provider when not listed in <see cref="MaxConcurrentPerSource"/>.</summary>
    public int DefaultMaxConcurrentPerSource { get; set; } = 2;

    /// <summary>Per-provider overrides, e.g. { "KapitalBank": 2, "Didox": 3 }.</summary>
    public Dictionary<string, int> MaxConcurrentPerSource { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A single load is cancelled after this time.</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often a running job writes heartbeat_at.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A Running job whose heartbeat is older than this is considered dead and marked Failed (with its run).
    /// Must be well above <see cref="HeartbeatInterval"/>. Jobs left Running by a previous process are
    /// failed right at worker startup (single worker instance).
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Minimum time between two "refresh now" requests for the same connection.</summary>
    public TimeSpan ManualRequestMinInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Graceful stop: how long running loads may continue to finish before they are cancelled and returned to the
    /// queue. Must stay below Hosting:ShutdownTimeout (60 s) with room for returning the jobs.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>How often a worker that did not get the single-instance lock (another worker runs) tries again.</summary>
    public TimeSpan InstanceLockRetryInterval { get; set; } = TimeSpan.FromSeconds(15);

    public int MaxConcurrentFor(string provider) =>
        MaxConcurrentPerSource.TryGetValue(provider, out var limit) ? limit : DefaultMaxConcurrentPerSource;
}

/// <summary>Sync:Schedule. Times are local times of <see cref="UtcOffset"/>.</summary>
public sealed class ScheduleOptions
{
    /// <summary>Uzbekistan, UTC+5.</summary>
    public TimeSpan UtcOffset { get; set; } = TimeSpan.FromHours(5);

    /// <summary>Nightly load window start (inclusive). Must be before <see cref="NightlyWindowEnd"/> on the same day.</summary>
    public TimeSpan NightlyWindowStart { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Nightly load window end (exclusive).</summary>
    public TimeSpan NightlyWindowEnd { get; set; } = TimeSpan.FromHours(5);

    /// <summary>Extra daytime refresh period for EDO (Didox) connections, hours. 0 disables daytime refreshes.</summary>
    public int EdoDaytimeRefreshHours { get; set; } = 4;

    /// <summary>Daytime refreshes happen between these local times.</summary>
    public TimeSpan DaytimeStart { get; set; } = TimeSpan.FromHours(8);
    public TimeSpan DaytimeEnd { get; set; } = TimeSpan.FromHours(20);
}
