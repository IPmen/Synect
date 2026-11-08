using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Application.Sync;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;
using static Synect.Tests.Infrastructure.TestData;

namespace Synect.Tests.Sync;

public class ConnectionStateTests : IAsyncLifetime
{
    private TestHost _host = null!;
    private Guid _tenant;
    private Guid _bank;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateSqliteAsync(s =>
        {
            s.Configure<CircuitBreakerOptions>(o => o.FailureThreshold = 3);
            s.Configure<Synect.Application.Resilience.RetryOptions>(o => o.MaxAttempts = 1);
        });
        _tenant = await _host.CreateTenantAsync("T");
        _bank = await _host.CreateConnectionAsync(_tenant, AdapterKind.Bank);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private Task<ReactivationResult> ReactivateAsync() =>
        _host.InTenantAsync(_tenant, sp => sp.GetRequiredService<SyncRequestService>().ReactivateAsync(_bank, CancellationToken.None));

    [Fact]
    public async Task Circuit_opens_after_n_failed_loads_in_a_row()
    {
        _host.FakeBank.For(_bank).TransientFailures = 1000;

        for (var i = 1; i <= 2; i++)
        {
            await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);
            var c = await _host.ConnectionAsync(_tenant, _bank);
            Assert.Equal(ConnectionStatus.Active, c.Status);
            Assert.Equal(i, c.ConsecutiveFailures);
        }

        _host.Time.Advance(TimeSpan.FromMinutes(1));
        await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);

        var opened = await _host.ConnectionAsync(_tenant, _bank);
        Assert.Equal(ConnectionStatus.NeedsAttention, opened.Status);
        Assert.Contains("3 failed loads in a row", opened.StatusReason);
        Assert.Equal(_host.Time.Now, opened.StatusChangedAt);
    }

    [Fact]
    public async Task Success_resets_the_failure_counter()
    {
        _host.FakeBank.For(_bank).TransientFailures = 2;
        await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);
        await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);
        Assert.Equal(2, (await _host.ConnectionAsync(_tenant, _bank)).ConsecutiveFailures);

        await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);

        var c = await _host.ConnectionAsync(_tenant, _bank);
        Assert.Equal(0, c.ConsecutiveFailures);
        Assert.Equal(ConnectionStatus.Active, c.Status);
    }

    [Fact]
    public async Task Authentication_error_opens_the_circuit_immediately_and_blocks_scheduling()
    {
        _host.FakeBank.For(_bank).AuthFailure = true;

        await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);

        var c = await _host.ConnectionAsync(_tenant, _bank);
        Assert.Equal(ConnectionStatus.NeedsAttention, c.Status);
        Assert.Equal(1, c.ConsecutiveFailures);
        Assert.Contains("Authentication failed", c.StatusReason);
        Assert.Equal(SyncRequestStatus.ConnectionNotActive, (await _host.RequestSyncAsync(_tenant, _bank)).Status);
        await _host.InTenantAsync(_tenant, sp => sp.GetRequiredService<SyncPlanner>().PlanAsync(CancellationToken.None));
        Assert.Empty(await _host.JobsAsync(_tenant, _bank));
    }

    [Fact]
    public async Task Reactivate_tests_the_connection_and_queues_the_first_load()
    {
        _host.FakeBank.For(_bank).AuthFailure = true;
        await _host.SyncBankAsync(_tenant, _bank, Day1, Day1);

        // Still broken: stays NeedsAttention with the new reason, nothing queued.
        var failed = await ReactivateAsync();
        Assert.Equal(ReactivationStatus.TestFailed, failed.Status);
        var c = await _host.ConnectionAsync(_tenant, _bank);
        Assert.Equal(ConnectionStatus.NeedsAttention, c.Status);
        Assert.Contains("Connection test failed", c.StatusReason);
        Assert.Empty(await _host.JobsAsync(_tenant, _bank));

        // Fixed: Active again, first load queued.
        _host.FakeBank.For(_bank).AuthFailure = false;
        _host.Time.Advance(TimeSpan.FromMinutes(5));
        var ok = await ReactivateAsync();
        Assert.Equal(ReactivationStatus.Reactivated, ok.Status);
        c = await _host.ConnectionAsync(_tenant, _bank);
        Assert.Equal(ConnectionStatus.Active, c.Status);
        Assert.Null(c.StatusReason);
        Assert.Equal(0, c.ConsecutiveFailures);
        Assert.Equal(_host.Time.Now, c.StatusChangedAt);
        var job = Assert.Single(await _host.JobsAsync(_tenant, _bank));
        Assert.Equal(SyncJobKind.Initial, job.Kind); // never loaded successfully before
        Assert.Equal(ok.JobId, job.Id);

        await _host.RunQueueAsync();
        Assert.Equal(SyncJobStatus.Succeeded, (await _host.JobsAsync(_tenant, _bank)).Single().Status);
        Assert.Equal(ReactivationStatus.AlreadyActive, (await ReactivateAsync()).Status);
    }

    [Fact]
    public async Task Disabled_connections_are_not_reactivated()
    {
        await _host.InTenantAsync(_tenant, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            await db.InTransactionAsync(async ct =>
            {
                var c = db.Connections.Single(x => x.Id == _bank);
                c.Disable("Closed by the client", _host.Time.Now);
                await db.SaveChangesAsync(ct);
            }, CancellationToken.None);
            return true;
        });

        Assert.Equal(ReactivationStatus.Disabled, (await ReactivateAsync()).Status);
        Assert.Equal(SyncRequestStatus.ConnectionNotActive, (await _host.RequestSyncAsync(_tenant, _bank)).Status);
    }
}
