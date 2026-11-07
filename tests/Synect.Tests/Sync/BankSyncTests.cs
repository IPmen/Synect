using Microsoft.EntityFrameworkCore;
using Synect.Domain.Banking;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Infrastructure;
using static Synect.Tests.Infrastructure.TestData;

namespace Synect.Tests.Sync;

public class BankSyncTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Guid _tenant;
    private Guid _connection;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateSqliteAsync();
        _tenant = await _host.CreateTenantAsync("Tenant A");
        _connection = await _host.CreateConnectionAsync(_tenant, AdapterKind.Bank);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private FakeBankScenario Scenario => _host.FakeBank.For(_connection);

    private async Task<(int Tx, int Raw, int Balances, int Accounts, int Counterparties)> CountsAsync() => (
        await _host.CountAsync(_tenant, db => db.BankTransactions),
        await _host.CountAsync(_tenant, db => db.RawObjects),
        await _host.CountAsync(_tenant, db => db.BankDailyBalances),
        await _host.CountAsync(_tenant, db => db.BankAccounts),
        await _host.CountAsync(_tenant, db => db.Counterparties));

    [Fact]
    public async Task Repeated_sync_of_same_range_is_idempotent()
    {
        var first = await _host.SyncBankAsync(_tenant, _connection);
        var afterFirst = await CountsAsync();
        var txFirst = await _host.QueryAsync(_tenant, db => db.BankTransactions.AsNoTracking().OrderBy(t => t.OperationId).ToListAsync());

        _host.Time.Advance(TimeSpan.FromHours(1));
        var second = await _host.SyncBankAsync(_tenant, _connection);
        var afterSecond = await CountsAsync();
        var txSecond = await _host.QueryAsync(_tenant, db => db.BankTransactions.AsNoTracking().OrderBy(t => t.OperationId).ToListAsync());

        Assert.Equal(SyncRunResult.Succeeded, first.Result);
        Assert.Equal(SyncRunResult.Succeeded, second.Result);
        Assert.True(first.IsComplete);
        Assert.Equal(first.RecordCount, second.RecordCount);
        Assert.True(afterFirst.Tx > 0);
        Assert.Equal(afterFirst, afterSecond);
        Assert.Equal(5, afterFirst.Balances);
        // Nothing was rewritten: the re-delivered operations matched by b2_id and hash.
        Assert.Equal(txFirst.Select(t => (t.Id, t.UpdatedAt)), txSecond.Select(t => (t.Id, t.UpdatedAt)));
        Assert.Equal(2, await _host.CountAsync(_tenant, db => db.SyncRuns));
    }

    [Fact]
    public async Task Operations_repeated_in_one_response_are_stored_once()
    {
        await _host.SyncBankAsync(_tenant, _connection, Day1, Day1);
        var normal = await _host.CountAsync(_tenant, db => db.BankTransactions);

        Scenario.DuplicateDays.Add(Day1);
        await _host.SyncBankAsync(_tenant, _connection, Day1, Day1);

        Assert.Equal(normal, await _host.CountAsync(_tenant, db => db.BankTransactions));
    }

    [Fact]
    public async Task Unclosed_day_is_not_final_until_the_bank_closes_it()
    {
        Scenario.OpenDays.Add(Day5);

        var first = await _host.SyncBankAsync(_tenant, _connection);
        Assert.Equal(SyncRunResult.Succeeded, first.Result);
        Assert.False(first.IsComplete);
        Assert.Equal(0, await _host.CountAsync(_tenant, db => db.BankTransactions.Where(t => t.PostingDate != Day5 && !t.IsFinal)));
        Assert.True(await _host.CountAsync(_tenant, db => db.BankTransactions.Where(t => t.PostingDate == Day5 && !t.IsFinal)) > 0);
        Assert.False(await _host.QueryAsync(_tenant, db => db.BankDailyBalances.Where(b => b.Date == Day5).Select(b => b.IsDayClosed).SingleAsync()));

        Scenario.OpenDays.Clear();
        var second = await _host.SyncBankAsync(_tenant, _connection);

        Assert.True(second.IsComplete);
        Assert.Equal(0, await _host.CountAsync(_tenant, db => db.BankTransactions.Where(t => !t.IsFinal)));
        Assert.True(await _host.QueryAsync(_tenant, db => db.BankDailyBalances.Where(b => b.Date == Day5).Select(b => b.IsDayClosed).SingleAsync()));
    }

    [Fact]
    public async Task Empty_day_produces_zero_balance_and_no_operations()
    {
        Scenario.EmptyDays.Add(Day1);

        var outcome = await _host.SyncBankAsync(_tenant, _connection, Day1, Day1);

        Assert.Equal(SyncRunResult.Succeeded, outcome.Result);
        Assert.True(outcome.IsComplete);
        Assert.Equal(0, await _host.CountAsync(_tenant, db => db.BankTransactions));
        var balance = await _host.QueryAsync(_tenant, db => db.BankDailyBalances.SingleAsync());
        Assert.Equal(0, balance.TotalDebit);
        Assert.Equal(0, balance.TotalCredit);
        Assert.Equal(balance.OpeningBalance, balance.ClosingBalance);
    }

    [Fact]
    public async Task Daily_totals_match_stored_operations()
    {
        await _host.SyncBankAsync(_tenant, _connection);

        var balances = await _host.QueryAsync(_tenant, db => db.BankDailyBalances.AsNoTracking().ToListAsync());
        var txs = await _host.QueryAsync(_tenant, db => db.BankTransactions.AsNoTracking().ToListAsync());
        foreach (var b in balances)
        {
            var day = txs.WithoutErrors().Where(t => t.PostingDate == b.Date).ToList();
            Assert.Equal(b.TotalCredit, day.Where(t => t.SignedAmount > 0).Sum(t => t.Amount));
            Assert.Equal(b.TotalDebit, day.Where(t => t.SignedAmount < 0).Sum(t => t.Amount));
            Assert.Equal(b.ClosingBalance - b.OpeningBalance, day.Sum(t => t.SignedAmount));
        }
    }

    [Fact]
    public async Task Operations_rejected_by_the_bank_are_stored_flagged_and_excluded_from_balances()
    {
        Scenario.ErroneousDays.Add(Day1);

        await _host.SyncBankAsync(_tenant, _connection, Day1, Day1);

        var txs = await _host.QueryAsync(_tenant, db => db.BankTransactions.AsNoTracking().ToListAsync());
        var failed = Assert.Single(txs, t => t.HasError);
        Assert.Equal(FakeBankDataSource.ErroneousOperationErrorCode, failed.ErrorCode);
        Assert.Equal(FakeBankDataSource.ErroneousOperationMessage, failed.ErrorMessage);
        Assert.All(txs.Where(t => !t.HasError), t => Assert.Null(t.ErrorCode));

        var balance = await _host.QueryAsync(_tenant, db => db.BankDailyBalances.AsNoTracking().SingleAsync());
        var effective = await _host.QueryAsync(_tenant, db => db.BankTransactions.WithoutErrors().AsNoTracking().ToListAsync());
        Assert.Equal(txs.Count - 1, effective.Count);
        Assert.Equal(balance.ClosingBalance - balance.OpeningBalance, effective.Sum(t => t.SignedAmount));
        Assert.NotEqual(balance.ClosingBalance - balance.OpeningBalance, txs.Sum(t => t.SignedAmount));

        // Re-delivery of the erroneous operation is idempotent too.
        await _host.SyncBankAsync(_tenant, _connection, Day1, Day1);
        Assert.Equal(txs.Count, await _host.CountAsync(_tenant, db => db.BankTransactions));
    }

    [Fact]
    public async Task Authentication_error_marks_connection_as_needing_attention()
    {
        Scenario.AuthFailure = true;

        var failed = await _host.SyncBankAsync(_tenant, _connection);

        Assert.Equal(SyncRunResult.Failed, failed.Result);
        Assert.False(failed.IsComplete);
        Assert.Contains("Authentication failed", failed.Error);
        var connection = await _host.QueryAsync(_tenant, db => db.Connections.SingleAsync());
        Assert.Equal(ConnectionStatus.NeedsAttention, connection.Status);
        Assert.Null(connection.LastSuccessfulSyncAt);
        Assert.Equal(0, await _host.CountAsync(_tenant, db => db.BankTransactions));

        Scenario.AuthFailure = false;
        var recovered = await _host.SyncBankAsync(_tenant, _connection);

        Assert.Equal(SyncRunResult.Succeeded, recovered.Result);
        connection = await _host.QueryAsync(_tenant, db => db.Connections.SingleAsync());
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(_host.Time.Now, connection.LastSuccessfulSyncAt);
    }
}
