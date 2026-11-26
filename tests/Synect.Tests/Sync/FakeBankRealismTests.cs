using System.Text.Json;
using Synect.Infrastructure.Adapters.Fake;

namespace Synect.Tests.Sync;

/// <summary>The demo bank reads like a real one: continuous balances, amounts that fit the currency, fees as fees.</summary>
public class FakeBankRealismTests
{
    private static async Task<JsonElement> DayAsync(FakeBankDataSource source, Guid connection, string sid, string account, DateOnly day) =>
        JsonDocument.Parse(await source.StatementJsonAsync(connection, sid, account, day, CancellationToken.None)).RootElement.GetProperty("result");

    private static (FakeBankDataSource Source, Guid Connection, string Sid) Demo()
    {
        var source = new FakeBankDataSource();
        var connection = Guid.NewGuid();
        var s = source.For(connection);
        s.AccountCount = 2;
        s.AccountCurrencies = ["000", "840"];
        s.CounterpartyPoolSize = 40;
        s.NoInnShare = 0.05;
        var login = JsonDocument.Parse(source.LoginJson(connection, "demo", "demo")).RootElement;
        return (source, connection, login.GetProperty("result").GetProperty("sid").GetString()!);
    }

    [Theory]
    [InlineData(0, "000")]
    [InlineData(1, "840")]
    public async Task The_opening_balance_of_a_day_is_the_closing_balance_of_the_day_before(int index, string currency)
    {
        var (source, connection, sid) = Demo();
        var account = FakeBankDataSource.AccountNumber(42, index, currency);
        var day = new DateOnly(2026, 10, 1);
        var previous = await DayAsync(source, connection, sid, account, day);
        for (var i = 1; i <= 40; i++)
        {
            var today = await DayAsync(source, connection, sid, account, day.AddDays(i));
            Assert.Equal(previous.GetProperty("saldo_out").GetInt64(), today.GetProperty("saldo_in").GetInt64());
            Assert.Equal(today.GetProperty("saldo_in").GetInt64() + today.GetProperty("total_credit").GetInt64() - today.GetProperty("total_debit").GetInt64(),
                today.GetProperty("saldo_out").GetInt64());
            Assert.True(today.GetProperty("saldo_out").GetInt64() >= 0);
            previous = today;
        }
    }

    [Fact]
    public async Task Foreign_currency_amounts_fit_the_currency_and_operations_without_inn_are_bank_fees()
    {
        var (source, connection, sid) = Demo();
        var usd = FakeBankDataSource.AccountNumber(42, 1, "840");
        var uzs = FakeBankDataSource.AccountNumber(42, 0, "000");
        var fees = 0;
        for (var d = new DateOnly(2026, 6, 1); d < new DateOnly(2026, 11, 1); d = d.AddDays(1))
        {
            var day = await DayAsync(source, connection, sid, usd, d);
            Assert.True(day.GetProperty("saldo_out").GetInt64() < 10_000_000_00L, "a USD balance stays below 10 m");
            foreach (var op in day.GetProperty("content").EnumerateArray())
                Assert.True(op.GetProperty("amount").GetInt64() <= 40_000_00L);
            foreach (var op in (await DayAsync(source, connection, sid, uzs, d)).GetProperty("content").EnumerateArray())
            {
                var side = op.GetProperty("dir").GetInt32() == 1 ? "dt" : "ct";
                if (op.GetProperty("inn_" + side).GetString() != "") continue;
                fees++;
                Assert.Equal(2, op.GetProperty("dir").GetInt32());
                Assert.Contains("fee", op.GetProperty("purpose").GetString());
                Assert.True(op.GetProperty("amount").GetInt64() <= 300_000_00L);
            }
        }
        Assert.True(fees > 0);
    }
}
