using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Synect.Mcp.Hosting;

/// <summary>
/// HTTP limits of the MCP endpoint, applied to every request (initialize, tools/list, tools/call, ...), before and
/// independently of the per-tool limits of <see cref="ToolCallGate"/>:
/// a ceiling of requests per API key and per client IP (Mcp:HttpRateLimit:RequestsPerMinutePerKey / PerIp) and a strict
/// limit of failed authentications (HTTP 401) per IP (FailedAuthPerMinutePerIp). Over a limit the answer is 429 with
/// Retry-After. Fixed windows, in memory, per process. The client IP is the connection address: behind a reverse proxy
/// configure forwarded headers first.
/// </summary>
public sealed class McpHttpLimiter(IOptions<McpOptions> options, TimeProvider time)
{
    private readonly WindowCounter _perIp = new(time);
    private readonly WindowCounter _perKey = new(time);
    private readonly WindowCounter _failedAuth = new(time);

    private McpHttpRateLimitOptions O => options.Value.HttpRateLimit;
    private TimeSpan Window => O.Window > TimeSpan.Zero ? O.Window : TimeSpan.FromMinutes(1);

    /// <summary>Too many failed authentications from this IP in the current window.</summary>
    public bool IsBlockedForFailedAuth(string ip, out TimeSpan retryAfter) =>
        _failedAuth.IsExceeded(ip, Math.Max(1, O.FailedAuthPerMinutePerIp), Window, out retryAfter);

    public void RecordFailedAuth(string ip) => _failedAuth.Hit(ip, Window);

    public bool TryAcquireIp(string ip, out TimeSpan retryAfter) => _perIp.TryHit(ip, Math.Max(1, O.RequestsPerMinutePerIp), Window, out retryAfter);

    public bool TryAcquireKey(Guid apiKeyId, out TimeSpan retryAfter) =>
        _perKey.TryHit(apiKeyId.ToString("N"), Math.Max(1, O.RequestsPerMinutePerKey), Window, out retryAfter);

    /// <summary>Fixed-window counters keyed by string; expired entries are dropped from time to time.</summary>
    private sealed class WindowCounter(TimeProvider time)
    {
        private sealed class Entry
        {
            public DateTimeOffset Start;
            public int Count;
        }

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private int _operations;

        public bool TryHit(string key, int limit, TimeSpan window, out TimeSpan retryAfter)
        {
            var entry = Current(key, window, out var now);
            lock (entry)
            {
                if (entry.Count >= limit)
                {
                    retryAfter = entry.Start + window - now;
                    return false;
                }
                entry.Count++;
                retryAfter = TimeSpan.Zero;
                return true;
            }
        }

        public bool IsExceeded(string key, int limit, TimeSpan window, out TimeSpan retryAfter)
        {
            var entry = Current(key, window, out var now);
            lock (entry)
            {
                retryAfter = entry.Start + window - now;
                return entry.Count >= limit;
            }
        }

        public void Hit(string key, TimeSpan window)
        {
            var entry = Current(key, window, out _);
            lock (entry) entry.Count++;
        }

        private Entry Current(string key, TimeSpan window, out DateTimeOffset now)
        {
            now = time.GetUtcNow();
            var at = now;
            if (Interlocked.Increment(ref _operations) % 1024 == 0)
                foreach (var (k, e) in _entries)
                    if (at - e.Start >= window) _entries.TryRemove(k, out _);
            var entry = _entries.GetOrAdd(key, _ => new Entry { Start = at });
            lock (entry)
            {
                if (now - entry.Start >= window)
                {
                    entry.Start = now;
                    entry.Count = 0;
                }
            }
            return entry;
        }
    }
}

/// <summary>Applies <see cref="McpHttpLimiter"/> to the MCP route. Place after UseAuthentication and before UseAuthorization.</summary>
public sealed class McpHttpLimitMiddleware(RequestDelegate next, McpHttpLimiter limiter, ILogger<McpHttpLimitMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(McpServiceCollection.Route))
        {
            await next(context);
            return;
        }

        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (limiter.IsBlockedForFailedAuth(ip, out var retry))
        {
            logger.LogWarning("MCP request refused: too many failed authentications from {ClientIp}", ip);
            await TooManyAsync(context, retry, "too_many_failed_authentications");
            return;
        }
        if (!limiter.TryAcquireIp(ip, out retry))
        {
            await TooManyAsync(context, retry, "rate_limited");
            return;
        }
        if (SynectClaims.Identity(context.User) is { } id && !limiter.TryAcquireKey(id.ApiKeyId, out retry))
        {
            await TooManyAsync(context, retry, "rate_limited");
            return;
        }

        await next(context);

        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized) limiter.RecordFailedAuth(ip);
    }

    private static Task TooManyAsync(HttpContext context, TimeSpan retryAfter, string code)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync($$"""{"error":"{{code}}","retry_after_seconds":{{seconds}}}""");
    }
}

public static class McpHttpLimitExtensions
{
    /// <summary>HTTP request limits of the MCP endpoint (see <see cref="McpHttpLimiter"/>).</summary>
    public static IApplicationBuilder UseSynectMcpHttpLimits(this IApplicationBuilder app) => app.UseMiddleware<McpHttpLimitMiddleware>();
}
