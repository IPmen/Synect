using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Analytics;
using Synect.Application.Analytics.Banking;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Analytics;

public class AnalyticsTimeoutTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Query_exceeding_the_timeout_fails_with_a_clear_error()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        var tenant = await fixture.Host.CreateTenantAsync("timeout");
        var bank = await fixture.Host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        await fixture.Host.SyncBankAsync(tenant, bank);

        // Second host on the same database with a 1 ms analytics timeout.
        await using var strict = TestHost.CreatePostgres(fixture.AppConnectionString, () => Task.CompletedTask,
            s => s.Configure<AnalyticsOptions>(o => o.QueryTimeout = TimeSpan.FromMilliseconds(1)));
        var period = new DatePeriod(new DateOnly(1950, 1, 1), new DateOnly(2026, 12, 31));

        var ex = await Assert.ThrowsAsync<AnalyticsTimeoutException>(() => strict.InTenantAsync(tenant, sp =>
            sp.GetRequiredService<BalancesAnalytics>().GetStatsAsync(period, CancellationToken.None)));
        Assert.Contains("did not finish", ex.Message);

        // The normal host with the default 5 s answers the same query.
        var ok = await fixture.Host.InTenantAsync(tenant, sp =>
            sp.GetRequiredService<BalancesAnalytics>().GetStatsAsync(period, CancellationToken.None));
        Assert.NotEmpty(ok.Data);
    }
}
