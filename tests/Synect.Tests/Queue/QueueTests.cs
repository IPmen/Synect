using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.Queue;

public class QueueTests
{
    private static Task<TestHost> HostAsync(Action<SyncQueueOptions>? queue = null) =>
        TestHost.CreateSqliteAsync(s =>
        {
            // Small initial bank loads keep the timing assertions independent of machine load.
            s.Configure<Synect.Application.Sync.BankSyncOptions>(o => o.InitialDays = 2);
            s.Configure<SyncQueueOptions>(o =>
            {
                o.JobTimeout = TimeSpan.FromSeconds(30);
                o.MaxConcurrent = 8;
                o.DefaultMaxConcurrentPerSource = 8;
                queue?.Invoke(o);
            });
        });

    [Fact]
    public async Task A_failing_and_a_hanging_connection_do_not_block_the_others()
    {
        await using var host = await HostAsync(o => o.JobTimeout = TimeSpan.FromSeconds(8));
        var tenant = await host.CreateTenantAsync("T");
        var hanging = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var failing = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var good1 = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var good2 = await host.CreateConnectionAsync(tenant, AdapterKind.Edo);
        host.FakeBank.For(hanging).Hang = true;
        host.FakeBank.For(failing).AuthFailure = true;
        foreach (var c in new[] { hanging, failing, good1, good2 })
            Assert.Equal(SyncRequestStatus.Queued, (await host.RequestSyncAsync(tenant, c)).Status);

        var started = await host.Dispatcher.TickAsync(CancellationToken.None);
        Assert.Equal(4, started);

        // The good ones finish while the hanging one is still running.
        await WaitUntilAsync(async () => !(await host.JobsAsync(tenant, good1)).Single().IsActive
                                         && !(await host.JobsAsync(tenant, good2)).Single().IsActive);
        foreach (var good in new[] { good1, good2 })
        {
            var job = (await host.JobsAsync(tenant, good)).Single();
            Assert.True(job.Status == SyncJobStatus.Succeeded, $"Job {job.Kind} ended {job.Status}: {job.Error}");
        }
        Assert.Equal(SyncJobStatus.Running, (await host.JobsAsync(tenant, hanging)).Single().Status);

        await host.Dispatcher.WaitForIdleAsync();
        var hangJob = (await host.JobsAsync(tenant, hanging)).Single();
        Assert.Equal(SyncJobStatus.Failed, hangJob.Status);
        Assert.Contains("Timed out", hangJob.Error);
        Assert.Equal(SyncRunResult.Failed, (await host.LastRunAsync(tenant, hanging)).Result);
        Assert.Equal(SyncJobStatus.Failed, (await host.JobsAsync(tenant, failing)).Single().Status);
        Assert.Equal(SyncRunResult.Succeeded, (await host.LastRunAsync(tenant, good1)).Result);
    }

    [Fact]
    public async Task Only_one_load_per_connection_and_refresh_now_reports_already_running()
    {
        await using var host = await HostAsync(o => o.JobTimeout = TimeSpan.FromSeconds(1));
        var tenant = await host.CreateTenantAsync("T");
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        host.FakeBank.For(bank).Hang = true;

        Assert.Equal(SyncRequestStatus.Queued, (await host.RequestSyncAsync(tenant, bank)).Status);
        Assert.Equal(SyncRequestStatus.AlreadyQueued, (await host.RequestSyncAsync(tenant, bank)).Status);

        Assert.Equal(1, await host.Dispatcher.TickAsync(CancellationToken.None));
        await WaitUntilAsync(async () => (await host.JobsAsync(tenant, bank)).Single().Status == SyncJobStatus.Running);

        var result = await host.RequestSyncAsync(tenant, bank);
        Assert.Equal(SyncRequestStatus.AlreadyRunning, result.Status);
        Assert.Equal(0, await host.Dispatcher.TickAsync(CancellationToken.None));
        Assert.Single(await host.JobsAsync(tenant, bank));

        await host.Dispatcher.WaitForIdleAsync();
        // Exactly one run was started for the connection.
        Assert.Equal(1, await host.CountAsync(tenant, db => db.SyncRuns.Where(r => r.ConnectionId == bank)));
    }

    [Fact]
    public async Task Database_rejects_a_second_active_job_for_a_connection()
    {
        await using var host = await HostAsync();
        var tenant = await host.CreateTenantAsync("T");
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        await host.RequestSyncAsync(tenant, bank);

        await using var scope = host.Scope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
        db.SyncJobs.Add(SyncJob.Create(tenant, bank, SyncJobKind.Scheduled, host.Time.Now));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refresh_now_respects_the_minimum_interval_and_does_not_wait_for_the_load()
    {
        await using var host = await HostAsync(o => o.ManualRequestMinInterval = TimeSpan.FromMinutes(5));
        var tenant = await host.CreateTenantAsync("T");
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);

        var first = await host.RequestSyncAsync(tenant, bank);
        Assert.Equal(SyncRequestStatus.Queued, first.Status);
        // Returning did not run anything.
        Assert.Equal(0, await host.CountAsync(tenant, db => db.SyncRuns));
        await host.RunQueueAsync();

        host.Time.Advance(TimeSpan.FromMinutes(2));
        var tooSoon = await host.RequestSyncAsync(tenant, bank);
        Assert.Equal(SyncRequestStatus.TooSoon, tooSoon.Status);
        Assert.Equal(TimeSpan.FromMinutes(3), tooSoon.RetryAfter);

        host.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(SyncRequestStatus.Queued, (await host.RequestSyncAsync(tenant, bank)).Status);
    }

    [Fact]
    public async Task Initial_loads_run_with_low_priority()
    {
        await using var host = await HostAsync(o => o.MaxConcurrent = 1);
        var tenant = await host.CreateTenantAsync("T");
        var newConnection = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var existing = await host.CreateConnectionAsync(tenant, AdapterKind.Edo);
        await host.SyncEdoAsync(tenant, existing); // already loaded once: no initial job for it
        await host.InTenantAsync(tenant, sp => sp.GetRequiredService<SyncPlanner>().PlanAsync(CancellationToken.None));
        var initial = (await host.JobsAsync(tenant, newConnection)).Single();
        Assert.Equal(SyncJobKind.Initial, initial.Kind);
        Assert.Equal(SyncJob.PriorityLow, initial.Priority);

        host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.RequestSyncAsync(tenant, existing);
        await host.RunQueueAsync(); // one slot: the manual job wins

        Assert.Equal(SyncJobStatus.Succeeded, (await host.JobsAsync(tenant, existing)).Single().Status);
        Assert.Equal(SyncJobStatus.Queued, (await host.JobsAsync(tenant, newConnection)).Single().Status);

        await host.RunQueueAsync();
        Assert.Equal(SyncJobStatus.Succeeded, (await host.JobsAsync(tenant, newConnection)).Single().Status);
    }

    [Fact]
    public async Task Concurrency_is_limited_per_source_and_globally()
    {
        await using var host = await HostAsync(o =>
        {
            o.MaxConcurrent = 3;
            o.MaxConcurrentPerSource["KapitalBank"] = 1;
            o.JobTimeout = TimeSpan.FromSeconds(1);
        });
        var tenant = await host.CreateTenantAsync("T");
        var banks = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
            host.FakeBank.For(bank).Hang = true;
            banks.Add(bank);
        }
        var edos = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var edo = await host.CreateConnectionAsync(tenant, AdapterKind.Edo);
            host.FakeEdo.For(edo).Hang = true;
            edos.Add(edo);
        }
        foreach (var c in banks.Concat(edos)) await host.RequestSyncAsync(tenant, c);

        // One bank (per-source limit) + two EDO (global limit of 3).
        Assert.Equal(3, await host.Dispatcher.TickAsync(CancellationToken.None));
        var running = await host.QueryAsync(tenant, db => db.SyncJobs.Where(j => j.Status == SyncJobStatus.Running)
            .Join(db.Connections, j => j.ConnectionId, c => c.Id, (j, c) => c.AdapterKind).ToListAsync());
        Assert.Equal(1, running.Count(k => k == AdapterKind.Bank));
        Assert.Equal(2, running.Count(k => k == AdapterKind.Edo));
        await host.Dispatcher.WaitForIdleAsync();
    }

    [Fact]
    public async Task Runs_and_jobs_left_running_by_a_crash_are_marked_failed()
    {
        await using var host = await HostAsync(o => o.StaleRunTimeout = TimeSpan.FromHours(2));
        var tenant = await host.CreateTenantAsync("T");
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        await host.InTenantAsync(tenant, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            await db.InTransactionAsync(async ct =>
            {
                db.SyncRuns.Add(SyncRun.Start(tenant, bank, host.Time.Now));
                var job = SyncJob.Create(tenant, bank, SyncJobKind.Manual, host.Time.Now);
                db.SyncJobs.Add(job);
                await db.SaveChangesAsync(ct);
                await db.SyncJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(u => u
                    .SetProperty(j => j.Status, SyncJobStatus.Running)
                    .SetProperty(j => j.StartedAt, host.Time.Now), ct);
            }, CancellationToken.None);
            return true;
        });

        host.Time.Advance(TimeSpan.FromHours(1));
        await host.InTenantAsync(tenant, sp => sp.GetRequiredService<SyncRecovery>().RecoverAsync(CancellationToken.None));
        Assert.Equal(SyncRunResult.Running, (await host.LastRunAsync(tenant, bank)).Result);

        host.Time.Advance(TimeSpan.FromHours(1.5));
        await host.Dispatcher.TickAsync(CancellationToken.None);
        await host.Dispatcher.WaitForIdleAsync();

        var run = await host.QueryAsync(tenant, db => db.SyncRuns.AsNoTracking().OrderBy(r => r.StartedAt).FirstAsync());
        Assert.Equal(SyncRunResult.Failed, run.Result);
        Assert.False(run.IsComplete);
        Assert.Contains("Abandoned", run.Error);
        Assert.Equal(SyncJobStatus.Failed, (await host.JobsAsync(tenant, bank)).First().Status);
    }

    [Fact]
    public async Task Planner_queues_due_slots_once_and_skips_inactive_connections()
    {
        await using var host = await HostAsync();
        var tenant = await host.CreateTenantAsync("T");
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var broken = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        await host.SyncBankAsync(tenant, bank);
        await host.SyncBankAsync(tenant, broken);
        host.FakeBank.For(broken).AuthFailure = true;
        host.FakeBank.For(broken).ExpireSessions(); // forces a new login, which is rejected
        await host.SyncBankAsync(tenant, broken); // auth error -> NeedsAttention

        var calculator = host.Services.GetRequiredService<SyncScheduleCalculator>();
        var slot = calculator.NightlySlot(bank, DateOnly.FromDateTime(host.Time.Now.ToOffset(TimeSpan.FromHours(5)).DateTime).AddDays(1));
        host.Time.Set(slot.Start.AddMinutes(1));

        await host.InTenantAsync(tenant, sp => sp.GetRequiredService<SyncPlanner>().PlanAsync(CancellationToken.None));
        await host.InTenantAsync(tenant, sp => sp.GetRequiredService<SyncPlanner>().PlanAsync(CancellationToken.None));

        var jobs = await host.JobsAsync(tenant, bank);
        var scheduled = Assert.Single(jobs);
        Assert.Equal(SyncJobKind.Scheduled, scheduled.Kind);
        Assert.Equal(slot.Start, scheduled.ScheduledFor);
        Assert.Empty(await host.JobsAsync(tenant, broken));

        await host.RunQueueAsync();
        await host.InTenantAsync(tenant, sp => sp.GetRequiredService<SyncPlanner>().PlanAsync(CancellationToken.None));
        Assert.Single(await host.JobsAsync(tenant, bank));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(50);
        }
    }
}
