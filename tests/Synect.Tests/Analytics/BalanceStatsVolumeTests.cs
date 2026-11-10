using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synect.Application.Analytics;
using Synect.Application.Analytics.Banking;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Analytics;

/// <summary>
/// A11 on a realistic volume (30 accounts, three years of daily balances with gaps): the result equals
/// the per-day definition, and a very long period stays within the production timeout of 5 s.
/// </summary>
public class BalanceStatsVolumeTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed record Row(Guid Account, DateOnly Date, long Balance);

    [Fact]
    public async Task Stats_match_the_per_day_definition_and_a_long_period_fits_the_production_timeout()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        var tenant = await fixture.Host.CreateTenantAsync("A11 volume");
        var connection = await fixture.Host.CreateConnectionAsync(tenant, AdapterKind.Bank);

        // Deterministic data: about every fifth day has no row (carried forward), some balances repeat.
        await using (var c = new NpgsqlConnection(fixture.AdminDbConnectionString))
        {
            await c.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = new NpgsqlCommand($"""
                INSERT INTO bank_accounts (id, tenant_id, connection_id, account_number, mfo, currency, name, created_at, updated_at)
                SELECT gen_random_uuid(), '{tenant}', '{connection}', lpad(i::text, 20, '0'), '00974', 'UZS', 'A' || i, now(), now()
                FROM generate_series(1, 30) i;
                INSERT INTO bank_daily_balances (id, tenant_id, bank_account_id, date, opening_balance, closing_balance,
                    total_debit, total_credit, is_day_closed, updated_at)
                SELECT gen_random_uuid(), a.tenant_id, a.id, d::date, 0,
                    ((hashtext(a.account_number || d::text) % 1000) + 1000) * 1000, 0, 0, true, now()
                FROM bank_accounts a CROSS JOIN generate_series('2023-11-01'::date, '2026-11-06'::date, interval '1 day') d
                WHERE a.tenant_id = '{tenant}' AND hashtext(a.account_number || d::text || 'gap') % 5 <> 0;
                ANALYZE bank_daily_balances;
                """, c);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var rows = new List<Row>();
        await using (var c = new NpgsqlConnection(fixture.AdminDbConnectionString))
        {
            await c.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = new NpgsqlCommand(
                $"SELECT bank_account_id, date, closing_balance FROM bank_daily_balances WHERE tenant_id = '{tenant}'", c);
            await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add(new Row(reader.GetGuid(0), DateOnly.FromDateTime(reader.GetDateTime(1)), reader.GetInt64(2)));
        }
        var byAccount = rows.GroupBy(r => r.Account).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Date).ToList());

        // Production timeout on purpose: this test guards against a genuinely slow query.
        await using var host = TestHost.CreatePostgres(fixture.AppConnectionString, () => Task.CompletedTask,
            s => s.Configure<AnalyticsOptions>(o => o.QueryTimeout = TimeSpan.FromSeconds(5)));

        DatePeriod[] periods =
        [
            new(new(2024, 3, 5), new(2024, 3, 5)),      // one day, usually carried forward from earlier
            new(new(2025, 1, 1), new(2025, 12, 31)),
            new(new(2023, 10, 1), new(2023, 11, 10)),  // starts before the first row
            new(new(2026, 10, 20), new(2027, 1, 31)),  // ends after the last row: carried forward
        ];
        foreach (var period in periods)
        {
            var actual = (await host.InTenantAsync(tenant, sp =>
                sp.GetRequiredService<BalancesAnalytics>().GetStatsAsync(period, CancellationToken.None))).Data;
            var expected = Expected(byAccount, period);
            Assert.Equal(expected.Count, actual.Count);
            foreach (var e in expected)
            {
                var a = actual.Single(x => x.AccountId == e.AccountId);
                Assert.Equal((e.Days, e.Average, e.Minimum, e.MinimumDate, e.Maximum), (a.Days, a.Average, a.Minimum, a.MinimumDate, a.Maximum));
            }
        }

        var sw = Stopwatch.StartNew();
        var all = await host.InTenantAsync(tenant, sp => sp.GetRequiredService<BalancesAnalytics>()
            .GetStatsAsync(new DatePeriod(new(1950, 1, 1), new(2026, 12, 31)), CancellationToken.None));
        Assert.Equal(30, all.Data.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"A11 over 1950-2026 took {sw.Elapsed}");
    }

    /// <summary>Reference: every day of the period takes the last row on or before it.</summary>
    private static List<AccountBalanceStats> Expected(Dictionary<Guid, List<Row>> byAccount, DatePeriod period)
    {
        var result = new List<AccountBalanceStats>();
        foreach (var (account, list) in byAccount)
        {
            var values = new List<(DateOnly Date, long Balance)>();
            var i = -1;
            for (var d = period.From; d <= period.To; d = d.AddDays(1))
            {
                while (i + 1 < list.Count && list[i + 1].Date <= d) i++;
                if (i >= 0) values.Add((d, list[i].Balance));
            }
            if (values.Count == 0) continue;
            var min = values.Min(v => v.Balance);
            var avg = (long)Math.Round(values.Sum(v => (decimal)v.Balance) / values.Count, MidpointRounding.AwayFromZero);
            result.Add(new AccountBalanceStats(account, "", "UZS", values.Count, avg, min,
                values.First(v => v.Balance == min).Date, values.Max(v => v.Balance)));
        }
        return result;
    }
}
