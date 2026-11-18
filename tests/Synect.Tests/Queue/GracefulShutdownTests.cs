using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Queue;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.Queue;

/// <summary>Graceful stop of the worker: no new jobs, running ones finish or go back to the queue.</summary>
public class GracefulShutdownTests
{
    private static Task<TestHost> HostAsync() =>
        TestHost.CreateSqliteAsync(s => s.Configure<SyncQueueOptions>(o =>
        {
            o.JobTimeout = TimeSpan.FromMinutes(5);
            o.MaxConcurrent = 8;
            o.DefaultMaxConcurrentPerSource = 8;
        }));

    [Fact]
    public async Task A_job_still_running_after_the_drain_time_returns_to_the_queue_without_counting_as_a_failure()
    {
        await using var host = await HostAsync();
        var tenant = await host.CreateTenantAsync("T");
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        host.FakeBank.For(bank).Hang = true;
        await host.RequestSyncAsync(tenant, bank);
        Assert.Equal(1, await host.Dispatcher.TickAsync(CancellationToken.None));
        await WaitUntilAsync(async () => (await host.JobsAsync(tenant, bank)).Single().Status == SyncJobStatus.Running);

        var returned = await host.Dispatcher.DrainAsync(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, returned);
        Assert.Equal(0, host.Dispatcher.RunningCount);
        var job = (await host.JobsAsync(tenant, bank)).Single();
        Assert.Equal(SyncJobStatus.Queued, job.Status);
        Assert.Null(job.StartedAt);
        Assert.Null(job.FinishedAt);
        Assert.Equal(SyncDispatcher.ReturnedToQueueReason, job.Error);

        // The interrupted run is recorded, but the connection is not blamed (no circuit breaker step).
        var run = await host.LastRunAsync(tenant, bank);
        Assert.Equal(SyncRunResult.Failed, run.Result);
        Assert.Contains("worker is stopping", run.Error);
        var connection = await host.QueryAsync(tenant, db => db.Connections.AsNoTracking().SingleAsync(c => c.Id == bank));
        Assert.Equal(ConnectionStatus.Active, connection.Status);
        Assert.Equal(0, connection.ConsecutiveFailures);
    }

    [Fact]
    public async Task A_job_that_finishes_within_the_drain_time_completes_normally()
    {
        await using var host = await HostAsync();
        var tenant = await host.CreateTenantAsync("T");
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        host.FakeBank.For(bank).Hang = true;
        await host.RequestSyncAsync(tenant, bank);
        Assert.Equal(1, await host.Dispatcher.TickAsync(CancellationToken.None));
        await WaitUntilAsync(async () => (await host.JobsAsync(tenant, bank)).Single().Status == SyncJobStatus.Running);

        var drain = host.Dispatcher.DrainAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(200);
        Assert.False(drain.IsCompleted);
        host.FakeBank.For(bank).ReleaseHang();

        Assert.Equal(0, await drain);
        Assert.Equal(SyncJobStatus.Succeeded, (await host.JobsAsync(tenant, bank)).Single().Status);
        Assert.False(host.Services.GetRequiredService<SyncShutdown>().JobsCancelled);
    }

    [Fact]
    public async Task After_the_stop_began_no_new_job_is_taken()
    {
        await using var host = await HostAsync();
        var tenant = await host.CreateTenantAsync("T");
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        await host.RequestSyncAsync(tenant, bank);

        host.Services.GetRequiredService<SyncShutdown>().BeginStop();

        Assert.Equal(0, await host.Dispatcher.TickAsync(CancellationToken.None));
        Assert.Equal(SyncJobStatus.Queued, (await host.JobsAsync(tenant, bank)).Single().Status);
        Assert.Equal(0, await host.Dispatcher.DrainAsync(TimeSpan.FromSeconds(1)));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached.");
            await Task.Delay(20);
        }
    }
}
