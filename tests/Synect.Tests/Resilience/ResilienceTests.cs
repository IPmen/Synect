using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Adapters;
using Synect.Application.Resilience;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Infrastructure;
using static Synect.Tests.Infrastructure.TestData;

namespace Synect.Tests.Resilience;

public class RetryDelayTests
{
    private static readonly RetryOptions Options = new()
    {
        MaxAttempts = 5, BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(10),
    };

    [Theory]
    [InlineData(1, 1000)]
    [InlineData(2, 2000)]
    [InlineData(3, 4000)]
    [InlineData(4, 8000)]
    [InlineData(5, 10000)] // capped
    [InlineData(9, 10000)]
    public void Backoff_is_exponential_capped_and_jittered_between_half_and_full(int attempt, int fullMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(fullMs), AdapterCallPolicy.ComputeDelay(attempt, null, Options, 1.0));
        Assert.Equal(TimeSpan.FromMilliseconds(fullMs / 2.0), AdapterCallPolicy.ComputeDelay(attempt, null, Options, 0.0));
        Assert.InRange(AdapterCallPolicy.ComputeDelay(attempt, null, Options, 0.37),
            TimeSpan.FromMilliseconds(fullMs / 2.0), TimeSpan.FromMilliseconds(fullMs));
    }

    [Fact]
    public void Retry_after_is_a_lower_bound()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), AdapterCallPolicy.ComputeDelay(1, TimeSpan.FromSeconds(30), Options, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(750), AdapterCallPolicy.ComputeDelay(1, TimeSpan.FromMilliseconds(10), Options, 0.5));
    }

    [Fact]
    public void Only_transient_errors_are_retried()
    {
        var ct = CancellationToken.None;
        Assert.True(AdapterCallPolicy.IsTransient(new AdapterTransientException("503"), ct));
        Assert.True(AdapterCallPolicy.IsTransient(new AdapterRateLimitException("429", TimeSpan.FromSeconds(1)), ct));
        Assert.True(AdapterCallPolicy.IsTransient(new HttpRequestException("network"), ct));
        Assert.False(AdapterCallPolicy.IsTransient(new AdapterAuthenticationException("401"), ct));
        Assert.False(AdapterCallPolicy.IsTransient(new AdapterException("bad data"), ct));
        Assert.False(AdapterCallPolicy.IsTransient(new OperationCanceledException(), ct));
    }
}

public class RateLimiterTests
{
    private static AdapterRateLimiters Limiters(int perConnection, int perSource)
    {
        var options = new RateLimitOptions
        {
            PerConnection = new TokenBucketSettings { TokenLimit = perConnection, TokensPerPeriod = perConnection, ReplenishmentPeriod = TimeSpan.FromHours(1) },
            PerSource = new TokenBucketSettings { TokenLimit = perSource, TokensPerPeriod = perSource, ReplenishmentPeriod = TimeSpan.FromHours(1) },
        };
        return new AdapterRateLimiters(new StaticOptionsMonitor(options));
    }

    [Fact]
    public void Each_connection_has_its_own_bucket()
    {
        var limiters = Limiters(perConnection: 2, perSource: 100);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.True(limiters.ForConnection("KapitalBank", a).AttemptAcquire().IsAcquired);
        Assert.True(limiters.ForConnection("KapitalBank", a).AttemptAcquire().IsAcquired);
        Assert.False(limiters.ForConnection("KapitalBank", a).AttemptAcquire().IsAcquired);
        Assert.True(limiters.ForConnection("KapitalBank", b).AttemptAcquire().IsAcquired);
    }

    [Fact]
    public async Task Provider_ceiling_is_shared_by_all_connections()
    {
        var limiters = Limiters(perConnection: 100, perSource: 3);
        await limiters.AcquireAsync("Didox", Guid.NewGuid(), CancellationToken.None);
        await limiters.AcquireAsync("Didox", Guid.NewGuid(), CancellationToken.None);
        await limiters.AcquireAsync("Didox", Guid.NewGuid(), CancellationToken.None);

        // The 4th call waits for replenishment (1 hour) whatever the connection.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiters.AcquireAsync("Didox", Guid.NewGuid(), cts.Token));
        // Another provider is not affected.
        await limiters.AcquireAsync("KapitalBank", Guid.NewGuid(), CancellationToken.None);
    }

    private sealed class StaticOptionsMonitor(RateLimitOptions value) : IOptionsMonitor<RateLimitOptions>
    {
        public RateLimitOptions CurrentValue => value;
        public RateLimitOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<RateLimitOptions, string?> listener) => null;
    }
}

public class AdapterRetryTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Guid _tenant;
    private Guid _bank;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateSqliteAsync(s => s.Configure<RetryOptions>(o =>
        {
            o.MaxAttempts = 4;
            o.BaseDelay = TimeSpan.FromMilliseconds(1);
            o.MaxDelay = TimeSpan.FromMilliseconds(5);
        }));
        _tenant = await _host.CreateTenantAsync("T");
        _bank = await _host.CreateConnectionAsync(_tenant, AdapterKind.Bank);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private FakeBankScenario Bank => _host.FakeBank.For(_bank);

    /// <summary>Data calls of a successful one-day load: GetAccounts + GetTransactions + GetBalances.</summary>
    private const int CallsPerDay = 3;

    [Fact]
    public async Task Rate_limited_calls_are_retried_until_they_succeed()
    {
        Bank.RateLimitFailures = 2;
        Bank.RateLimitRetryAfter = TimeSpan.FromMilliseconds(5);

        var outcome = await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);

        Assert.Equal(SyncRunResult.Succeeded, outcome.Result);
        Assert.Equal(CallsPerDay + 2, Bank.CallCount);
        Assert.Contains(_host.Logs.Lines, l => l.Contains("AdapterRateLimitException") && l.Contains("retrying"));
    }

    [Fact]
    public async Task Transient_errors_are_retried_with_backoff()
    {
        Bank.TransientFailures = 3;

        var outcome = await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);

        Assert.Equal(SyncRunResult.Succeeded, outcome.Result);
        Assert.Equal(CallsPerDay + 3, Bank.CallCount);
    }

    [Fact]
    public async Task Retries_stop_after_max_attempts()
    {
        Bank.TransientFailures = 100;

        var outcome = await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);

        Assert.Equal(SyncRunResult.Failed, outcome.Result);
        Assert.Equal(4, Bank.CallCount); // GetAccounts tried MaxAttempts times
    }

    [Fact]
    public async Task Authentication_errors_are_not_retried()
    {
        Bank.IpBlocked = true; // envelope error 61002 on data methods

        var outcome = await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);

        Assert.Equal(SyncRunResult.Failed, outcome.Result);
        // One call, one re-login with a retry of that call (session handling), and no policy retries.
        Assert.Equal(2, Bank.CallCount);
        Assert.Equal(2, Bank.LoginCount);
        Assert.Equal(ConnectionStatus.NeedsAttention, (await _host.ConnectionAsync(_tenant, _bank)).Status);
    }

    [Fact]
    public async Task Bank_session_is_reused_and_renewed_after_expiry()
    {
        await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);
        await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);
        Assert.Equal(1, Bank.LoginCount);

        Bank.ExpireSessions(); // the next call gets HTTP 401
        var outcome = await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);

        Assert.Equal(SyncRunResult.Succeeded, outcome.Result);
        Assert.Equal(2, Bank.LoginCount);
    }

    [Fact]
    public async Task Didox_token_is_reused_for_its_lifetime_and_refreshed_ahead_of_expiry()
    {
        var edo = await _host.CreateConnectionAsync(_tenant, AdapterKind.Edo);
        var scenario = _host.FakeEdo.For(edo);

        await _host.SyncEdoAsync(_tenant, edo);
        Assert.Equal(1, scenario.LoginCount);

        _host.Time.Advance(TimeSpan.FromMinutes(349)); // 11 minutes left: still outside the 10-minute margin
        Assert.Equal(SyncRunResult.Succeeded, (await _host.SyncEdoAsync(_tenant, edo)).Result);
        Assert.Equal(1, scenario.LoginCount);

        _host.Time.Advance(TimeSpan.FromMinutes(2)); // 9 minutes left: refreshed before it expires
        Assert.Equal(SyncRunResult.Succeeded, (await _host.SyncEdoAsync(_tenant, edo)).Result);
        Assert.Equal(2, scenario.LoginCount);
    }
}
