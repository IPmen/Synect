using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Freshness;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;
using static Synect.Tests.Infrastructure.TestData;

namespace Synect.Tests.Freshness;

public class SourceFreshnessTests
{
    [Fact]
    public async Task Reports_last_success_state_and_completeness_per_source()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var tenant = await host.CreateTenantAsync("T");
        var other = await host.CreateTenantAsync("Other");
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var edo = await host.CreateConnectionAsync(tenant, AdapterKind.Edo);
        var neverLoaded = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        await host.CreateConnectionAsync(other, AdapterKind.Bank);

        // Bank: complete load, then an incomplete one (open day).
        await host.SyncBankAsync(tenant, bank, Day1, Day1);
        host.Time.Advance(TimeSpan.FromHours(1));
        host.FakeBank.For(bank).OpenDays.Add(Day1);
        await host.SyncBankAsync(tenant, bank, Day1, Day1);
        var bankSuccessAt = host.Time.Now;

        // EDO: one success, then an authentication failure.
        await host.SyncEdoAsync(tenant, edo);
        var edoSuccessAt = host.Time.Now;
        host.Time.Advance(TimeSpan.FromHours(1));
        host.FakeEdo.For(edo).AuthFailure = true;
        host.Services.GetRequiredService<Synect.Infrastructure.Adapters.Didox.DidoxTokenCache>().Invalidate(edo);
        await host.SyncEdoAsync(tenant, edo);

        var freshness = await host.InTenantAsync(tenant, sp =>
            sp.GetRequiredService<ISourceFreshnessQuery>().GetAsync(tenant, CancellationToken.None));

        Assert.Equal(3, freshness.Count);
        var b = freshness.Single(f => f.ConnectionId == bank);
        Assert.Equal(ConnectionStatus.Active, b.Status);
        Assert.Equal(bankSuccessAt, b.LastSuccessfulSyncAt);
        Assert.False(b.LastSuccessfulRunIsComplete);
        Assert.Equal(SyncRunResult.Succeeded, b.LastRunResult);

        var e = freshness.Single(f => f.ConnectionId == edo);
        Assert.Equal(ConnectionStatus.NeedsAttention, e.Status);
        Assert.Contains("Authentication failed", e.StatusReason);
        Assert.Equal(edoSuccessAt, e.LastSuccessfulSyncAt);
        Assert.True(e.LastSuccessfulRunIsComplete);
        Assert.Equal(SyncRunResult.Failed, e.LastRunResult);
        Assert.Equal(AdapterKind.Edo, e.Kind);

        var n = freshness.Single(f => f.ConnectionId == neverLoaded);
        Assert.Null(n.LastSuccessfulSyncAt);
        Assert.Null(n.LastSuccessfulRunIsComplete);
        Assert.Null(n.LastRunResult);
    }

    [Fact]
    public async Task Cannot_read_another_tenants_freshness()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var tenant = await host.CreateTenantAsync("T");
        var other = await host.CreateTenantAsync("Other");

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.InTenantAsync(tenant, sp =>
            sp.GetRequiredService<ISourceFreshnessQuery>().GetAsync(other, CancellationToken.None)));
    }
}
