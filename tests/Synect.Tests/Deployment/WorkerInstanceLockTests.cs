using Npgsql;
using Synect.Infrastructure.Persistence;
using Synect.Tests.Postgres;

namespace Synect.Tests.Deployment;

/// <summary>Only one sync worker per database: PostgreSQL advisory lock, taken as the application role.</summary>
public class WorkerInstanceLockTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task A_second_worker_does_not_get_the_lock_until_the_first_releases_it()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        await using var first = new WorkerInstanceLock(fixture.AppConnectionString);
        await using var second = new WorkerInstanceLock(fixture.AppConnectionString);

        Assert.True(await first.TryAcquireAsync(ct));
        Assert.True(await first.IsHeldAsync(ct));
        Assert.False(await second.TryAcquireAsync(ct));
        Assert.False(await second.IsHeldAsync(ct));

        await first.DisposeAsync();
        Assert.True(await second.TryAcquireAsync(ct));
        Assert.True(await second.IsHeldAsync(ct));
    }

    [Fact]
    public async Task The_lock_disappears_with_the_session_and_its_loss_is_detected()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;

        await using var worker = new WorkerInstanceLock(fixture.AppConnectionString);
        Assert.True(await worker.TryAcquireAsync(ct));

        // The database ends the worker's session (as after a server restart or a network cut).
        await PostgresFixture.ExecAsync(fixture.AdminDbConnectionString,
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = 'synect-worker-lock' AND datname = current_database()");

        Assert.False(await worker.IsHeldAsync(ct));
        await using var next = new WorkerInstanceLock(fixture.AppConnectionString);
        Assert.True(await next.TryAcquireAsync(ct));
    }
}
