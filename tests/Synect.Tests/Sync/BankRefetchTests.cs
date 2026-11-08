using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Sync;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.Sync;

/// <summary>Incremental bank range: initial depth, then from the earliest unclosed day, else yesterday and today.</summary>
public class BankRefetchTests : IAsyncLifetime
{
    // Test clock: 2026-11-07 10:00 UTC = 15:00 in Tashkent.
    private static readonly DateOnly Today = new(2026, 11, 7);

    private TestHost _host = null!;
    private Guid _tenant;
    private Guid _bank;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateSqliteAsync(s => s.Configure<BankSyncOptions>(o => o.InitialDays = 10));
        _tenant = await _host.CreateTenantAsync("T");
        _bank = await _host.CreateConnectionAsync(_tenant, AdapterKind.Bank);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<(DateOnly? From, DateOnly? To)> SyncAndGetRangeAsync()
    {
        var outcome = await _host.SyncBankIncrementalAsync(_tenant, _bank);
        Assert.Equal(SyncRunResult.Succeeded, outcome.Result);
        var run = await _host.LastRunAsync(_tenant, _bank);
        return (run.RangeFrom, run.RangeTo);
    }

    [Fact]
    public async Task First_load_covers_initial_days_then_only_yesterday_and_today()
    {
        Assert.Equal((Today.AddDays(-9), Today), await SyncAndGetRangeAsync());
        Assert.Equal(10, await _host.CountAsync(_tenant, db => db.BankDailyBalances));

        Assert.Equal((Today.AddDays(-1), Today), await SyncAndGetRangeAsync());
    }

    [Fact]
    public async Task Rereads_from_the_earliest_day_that_is_not_closed()
    {
        var open = Today.AddDays(-6);
        _host.FakeBank.For(_bank).OpenDays.Add(open);
        _host.FakeBank.For(_bank).OpenDays.Add(Today.AddDays(-2));
        await SyncAndGetRangeAsync();
        Assert.Equal(2, await _host.CountAsync(_tenant, db => db.BankDailyBalances.Where(b => !b.IsDayClosed)));

        // Still open: the next load starts at the earliest open day.
        Assert.Equal((open, Today), await SyncAndGetRangeAsync());

        // The bank closes the days; this load still starts there and marks them closed.
        _host.FakeBank.For(_bank).OpenDays.Clear();
        Assert.Equal((open, Today), await SyncAndGetRangeAsync());
        Assert.Equal(0, await _host.CountAsync(_tenant, db => db.BankDailyBalances.Where(b => !b.IsDayClosed)));

        // Everything closed: back to yesterday and today.
        Assert.Equal((Today.AddDays(-1), Today), await SyncAndGetRangeAsync());
    }

    [Fact]
    public async Task Days_missed_while_the_worker_was_down_are_not_skipped()
    {
        await SyncAndGetRangeAsync();
        _host.Time.Advance(TimeSpan.FromDays(5));

        Assert.Equal((Today.AddDays(1), Today.AddDays(5)), await SyncAndGetRangeAsync());
        var dates = await _host.QueryAsync(_tenant, db => db.BankDailyBalances.Select(b => b.Date).ToListAsync());
        Assert.Equal(15, dates.Distinct().Count());
    }
}
