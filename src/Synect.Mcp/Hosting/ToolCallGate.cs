using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Synect.Mcp.Hosting;

/// <summary>Why a call was not admitted, and when to retry.</summary>
public sealed record GateRejection(string Message, TimeSpan RetryAfter);

/// <summary>
/// Admission of tool calls: Mcp:RateLimit:RequestsPerMinutePerKey per API key (fixed window) and
/// Mcp:RateLimit:MaxConcurrentPerTenant calls running at the same time per tenant (when the call works with one
/// organization) and Mcp:RateLimit:MaxConcurrentPerAccount per account (account keys). No queueing: a call over
/// a limit is rejected at once with the time to retry.
/// </summary>
public sealed class ToolCallGate : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _perKey;
    private readonly PartitionedRateLimiter<Guid> _perTenant;
    private readonly PartitionedRateLimiter<Guid> _perAccount;
    private readonly McpRateLimitOptions _options;

    public ToolCallGate(IOptions<McpOptions> options)
    {
        _options = options.Value.RateLimit;
        var perMinute = Math.Max(1, _options.RequestsPerMinutePerKey);
        var concurrent = Math.Max(1, _options.MaxConcurrentPerTenant);
        var window = _options.Window > TimeSpan.Zero ? _options.Window : TimeSpan.FromMinutes(1);
        _perKey = PartitionedRateLimiter.Create<Guid, Guid>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = window, QueueLimit = 0, AutoReplenishment = true }));
        _perTenant = PartitionedRateLimiter.Create<Guid, Guid>(tenant => RateLimitPartition.GetConcurrencyLimiter(tenant, _ =>
            new ConcurrencyLimiterOptions { PermitLimit = concurrent, QueueLimit = 0 }));
        var perAccount = Math.Max(1, _options.MaxConcurrentPerAccount);
        _perAccount = PartitionedRateLimiter.Create<Guid, Guid>(account => RateLimitPartition.GetConcurrencyLimiter(account, _ =>
            new ConcurrencyLimiterOptions { PermitLimit = perAccount, QueueLimit = 0 }));
    }

    /// <summary>Returns a lease to dispose when the call ends, or null with the rejection.</summary>
    /// <param name="tenantId">The organization of the call, or null when it reads several organizations.</param>
    /// <param name="accountId">The account of an account key, or null.</param>
    public IDisposable? TryEnter(Guid apiKeyId, Guid? tenantId, Guid? accountId, out GateRejection? rejection)
    {
        using (var minute = _perKey.AttemptAcquire(apiKeyId))
        {
            if (!minute.IsAcquired)
            {
                var retry = minute.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : _options.Window;
                rejection = new GateRejection(
                    $"Rate limit exceeded: at most {_options.RequestsPerMinutePerKey} tool calls per minute per API key. Retry in {Math.Ceiling(retry.TotalSeconds)} s.",
                    retry);
                return null;
            }
        }

        RateLimitLease? account = null;
        if (accountId is { } a)
        {
            account = _perAccount.AttemptAcquire(a);
            if (!account.IsAcquired)
            {
                account.Dispose();
                rejection = new GateRejection(
                    $"Too many tool calls at the same time: at most {_options.MaxConcurrentPerAccount} per account. Retry in 1 s, after the running calls finish.",
                    TimeSpan.FromSeconds(1));
                return null;
            }
        }

        RateLimitLease? running = null;
        if (tenantId is { } t)
        {
            running = _perTenant.AttemptAcquire(t);
            if (!running.IsAcquired)
            {
                running.Dispose();
                account?.Dispose();
                rejection = new GateRejection(
                    $"Too many tool calls at the same time: at most {_options.MaxConcurrentPerTenant} per client. Retry in 1 s, after the running calls finish.",
                    TimeSpan.FromSeconds(1));
                return null;
            }
        }

        rejection = null;
        return new Leases(running, account);
    }

    private sealed class Leases(RateLimitLease? tenant, RateLimitLease? account) : IDisposable
    {
        public void Dispose()
        {
            tenant?.Dispose();
            account?.Dispose();
        }
    }

    public void Dispose()
    {
        _perKey.Dispose();
        _perTenant.Dispose();
        _perAccount.Dispose();
    }
}
