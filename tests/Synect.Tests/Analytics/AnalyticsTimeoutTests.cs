using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synect.Application.Analytics;
using Synect.Application.Analytics.Banking;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Analytics;

public class AnalyticsTimeoutTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    /// <summary>
    /// The query is made slow by construction instead of racing a 1 ms timeout against a query over five days of data:
    /// with 1 ms the result depended on the Windows timer tick (about 15.6 ms) and on how busy the thread pool was, so the
    /// fast query sometimes finished before the cancellation fired (it failed once in a loaded full run). Here an admin
    /// transaction holds an ACCESS EXCLUSIVE lock on bank_daily_balances, the analytics query waits for it, and the timeout
    /// (statement_timeout on the server, the cancellation token on the client) must end the wait.
    /// </summary>
    [Fact]
    public async Task Query_exceeding_the_timeout_fails_with_a_clear_error()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        var tenant = await fixture.Host.CreateTenantAsync("timeout");
        var bank = await fixture.Host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        await fixture.Host.SyncBankAsync(tenant, bank);

        // Second host on the same database with a short analytics timeout.
        var timeout = TimeSpan.FromMilliseconds(200);
        await using var strict = TestHost.CreatePostgres(fixture.AppConnectionString, () => Task.CompletedTask,
            s => s.Configure<AnalyticsOptions>(o => o.QueryTimeout = timeout));
        var period = new DatePeriod(new DateOnly(1950, 1, 1), new DateOnly(2026, 12, 31));

        await using (var blocker = new NpgsqlConnection(fixture.AdminDbConnectionString))
        {
            await blocker.OpenAsync();
            await using var tx = await blocker.BeginTransactionAsync();
            await using (var lockCommand = new NpgsqlCommand("LOCK TABLE bank_daily_balances IN ACCESS EXCLUSIVE MODE", blocker, tx))
                await lockCommand.ExecuteNonQueryAsync();

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<AnalyticsTimeoutException>(() => strict.InTenantAsync(tenant, sp =>
                sp.GetRequiredService<BalancesAnalytics>().GetStatsAsync(period, CancellationToken.None)));
            Assert.Contains("did not finish", ex.Message);
            // Ended by the timeout, not by the lock going away (the lock is held until after this point).
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
            await tx.RollbackAsync();
        }

        // A timeout while the connection is still being opened is a timeout too, not a raw driver error. A local socket
        // accepts the connection and never answers the PostgreSQL startup message. Npgsql does not abort that wait on the
        // cancellation token (it would wait for the driver's connection Timeout, 15 s by default and here), so analytics
        // bounds the open itself with the same budget as the query (stage 6.5): the error comes after about QueryTimeout.
        using (var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            silent.Start();
            var port = ((System.Net.IPEndPoint)silent.LocalEndpoint).Port;
            var mute = new NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { Host = "127.0.0.1", Port = port, Pooling = false, Timeout = 15 }.ConnectionString;
            await using var unreachable = TestHost.CreatePostgres(mute, () => Task.CompletedTask,
                s => s.Configure<AnalyticsOptions>(o => o.QueryTimeout = timeout));
            var sw = Stopwatch.StartNew();
            var connect = await Assert.ThrowsAsync<AnalyticsTimeoutException>(() => unreachable.InTenantAsync(tenant, sp =>
                sp.GetRequiredService<BalancesAnalytics>().GetStatsAsync(period, CancellationToken.None)));
            Assert.Contains("did not finish", connect.Message);
            // Bounded by QueryTimeout (200 ms), far below the driver's 15 s connection Timeout.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"took {sw.Elapsed}");
            silent.Stop();
        }

        // Without the lock the regular test host (TestHost.TestQueryTimeout) answers the same query.
        var ok = await fixture.Host.InTenantAsync(tenant, sp =>
            sp.GetRequiredService<BalancesAnalytics>().GetStatsAsync(period, CancellationToken.None));
        Assert.NotEmpty(ok.Data);
    }
}
