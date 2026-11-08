using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Adapters;

namespace Synect.Application.Resilience;

/// <summary>Token bucket settings.</summary>
public sealed class TokenBucketSettings
{
    public int TokenLimit { get; set; } = 5;
    public int TokensPerPeriod { get; set; } = 5;
    public TimeSpan ReplenishmentPeriod { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>Adapters:{Provider}:RateLimit. Named options, the name is the provider (e.g. "KapitalBank").</summary>
public sealed class RateLimitOptions
{
    /// <summary>Bucket of each connection.</summary>
    public TokenBucketSettings PerConnection { get; set; } = new();

    /// <summary>Ceiling shared by all connections of the provider.</summary>
    public TokenBucketSettings PerSource { get; set; } = new() { TokenLimit = 20, TokensPerPeriod = 20 };
}

/// <summary>Sync:Retry.</summary>
public sealed class RetryOptions
{
    /// <summary>Total attempts including the first one.</summary>
    public int MaxAttempts { get; set; } = 4;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Per-connection and per-provider token buckets, created lazily and kept for the process lifetime.</summary>
public sealed class AdapterRateLimiters(IOptionsMonitor<RateLimitOptions> options) : IDisposable
{
    /// <summary>Token buckets own replenishment timers; they are released with the container.</summary>
    public void Dispose()
    {
        foreach (var limiter in _perSource.Values.Concat(_perConnection.Values)) limiter.Dispose();
        _perSource.Clear();
        _perConnection.Clear();
    }

    private readonly ConcurrentDictionary<string, RateLimiter> _perSource = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, RateLimiter> _perConnection = new();

    public RateLimiter ForSource(string provider) =>
        _perSource.GetOrAdd(provider, p => Create(options.Get(p).PerSource));

    public RateLimiter ForConnection(string provider, Guid connectionId) =>
        _perConnection.GetOrAdd(connectionId, _ => Create(options.Get(provider).PerConnection));

    /// <summary>Waits for one token from the connection bucket and one from the provider bucket.</summary>
    public async Task AcquireAsync(string provider, Guid connectionId, CancellationToken ct)
    {
        await AcquireOneAsync(ForConnection(provider, connectionId), ct);
        await AcquireOneAsync(ForSource(provider), ct);
    }

    private static async Task AcquireOneAsync(RateLimiter limiter, CancellationToken ct)
    {
        while (true)
        {
            using var lease = await limiter.AcquireAsync(1, ct);
            if (lease.IsAcquired) return;
            // Queue full: back off briefly and try again.
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }

    private static TokenBucketRateLimiter Create(TokenBucketSettings s) => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = Math.Max(1, s.TokenLimit),
        TokensPerPeriod = Math.Max(1, s.TokensPerPeriod),
        ReplenishmentPeriod = s.ReplenishmentPeriod <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : s.ReplenishmentPeriod,
        QueueLimit = 10_000,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    });
}

/// <summary>
/// Wraps every adapter call: rate limiting, then retries with exponential backoff and jitter for
/// transient failures (network, 5xx, 429). Authentication errors and other adapter errors are not retried.
/// </summary>
public sealed class AdapterCallPolicy(
    AdapterRateLimiters limiters,
    IOptions<RetryOptions> retryOptions,
    TimeProvider time,
    ILogger<AdapterCallPolicy> logger)
{
    public async Task<T> ExecuteAsync<T>(
        ISourceAdapter adapter, AdapterContext context, string operation, Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        var options = retryOptions.Value;
        for (var attempt = 1; ; attempt++)
        {
            await limiters.AcquireAsync(adapter.ProviderName, context.ConnectionId, ct);
            try
            {
                return await call(ct);
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < options.MaxAttempts)
            {
                var delay = ComputeDelay(attempt, (ex as AdapterRateLimitException)?.RetryAfter, options, Random.Shared.NextDouble());
                // Only the exception type is logged: adapter messages are safe by contract, but types are enough here.
                logger.LogWarning("Transient {ErrorType} in {Operation} for connection {ConnectionId}; attempt {Attempt}/{MaxAttempts}, retrying in {Delay}",
                    ex.GetType().Name, operation, context.ConnectionId, attempt, options.MaxAttempts, delay);
                await Task.Delay(delay, time, ct);
            }
        }
    }

    public static bool IsTransient(Exception ex, CancellationToken ct) => ex switch
    {
        AdapterAuthenticationException => false,
        AdapterTransientException => true,
        HttpRequestException => true,
        TimeoutException => true,
        OperationCanceledException => !ct.IsCancellationRequested && ex is TaskCanceledException { InnerException: TimeoutException },
        _ => false,
    };

    /// <summary>
    /// Exponential backoff with "equal jitter": base * 2^(attempt-1), capped at MaxDelay, then scaled into
    /// [50%, 100%]. A server-provided Retry-After is respected as a lower bound.
    /// </summary>
    public static TimeSpan ComputeDelay(int attempt, TimeSpan? retryAfter, RetryOptions options, double jitter01)
    {
        var exponential = options.BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(exponential, options.MaxDelay.TotalMilliseconds);
        var jittered = TimeSpan.FromMilliseconds(capped * (0.5 + 0.5 * Math.Clamp(jitter01, 0, 1)));
        return retryAfter is { } ra && ra > jittered ? ra : jittered;
    }
}
