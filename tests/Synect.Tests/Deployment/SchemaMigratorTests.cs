using System.Diagnostics;
using Npgsql;
using Synect.Infrastructure.Persistence;
using Synect.Tests.Postgres;

namespace Synect.Tests.Deployment;

/// <summary>`migrate` as a separate deployment step: owner role, lock_timeout, schema status.</summary>
public class SchemaMigratorTests
{
    [Fact]
    public void Lock_timeout_is_added_to_the_session_options_and_keeps_existing_ones()
    {
        var plain = new NpgsqlConnectionStringBuilder(
            SchemaMigrator.WithLockTimeout("Host=localhost;Database=synect;Username=synect_owner", TimeSpan.FromSeconds(5)));
        Assert.Equal("-c lock_timeout=5000", plain.Options);
        Assert.Equal("synect-migrate", plain.ApplicationName);

        var withOptions = new NpgsqlConnectionStringBuilder(
            SchemaMigrator.WithLockTimeout("Host=localhost;Username=u;Options=-c search_path=public", TimeSpan.FromSeconds(2)));
        Assert.Equal("-c search_path=public -c lock_timeout=2000", withOptions.Options);
    }

    [Fact]
    public async Task Migrate_applies_everything_once_reports_status_and_gives_up_on_locks_after_lock_timeout()
    {
        var admin = PostgresFixture.AdminConnectionString();
        Assert.SkipUnless(admin is not null, PostgresFixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var database = "synect_test_migrate_" + Guid.NewGuid().ToString("N")[..12];
        await PostgresFixture.ExecAsync(admin!, $"CREATE DATABASE {database}");
        var owner = new NpgsqlConnectionStringBuilder(admin!) { Database = database }.ConnectionString;
        try
        {
            var before = await SchemaMigrator.StatusAsync(owner, ct);
            Assert.True(before.DatabaseAvailable);
            Assert.False(before.Compatible);
            Assert.Null(before.AppliedLatest);
            Assert.NotEmpty(before.Pending);

            var applied = await SchemaMigrator.MigrateAsync(owner, TimeSpan.FromSeconds(5), ct);
            Assert.Equal(before.Pending, applied);

            var after = await SchemaMigrator.StatusAsync(owner, ct);
            Assert.True(after.Compatible);
            Assert.Equal(after.ExpectedLatest, after.AppliedLatest);
            Assert.Empty(after.Pending);
            Assert.Empty(await SchemaMigrator.MigrateAsync(owner, TimeSpan.FromSeconds(5), ct));

            // Another session holds a lock the migration needs: it fails after lock_timeout instead of waiting.
            await using var blocker = new NpgsqlConnection(owner);
            await blocker.OpenAsync(ct);
            await using var tx = await blocker.BeginTransactionAsync(ct);
            await using (var lockCmd = new NpgsqlCommand("LOCK TABLE __ef_migrations_history IN ACCESS EXCLUSIVE MODE", blocker, tx))
                await lockCmd.ExecuteNonQueryAsync(ct);

            var watch = Stopwatch.StartNew();
            var error = await Assert.ThrowsAnyAsync<Exception>(() => SchemaMigrator.MigrateAsync(owner, TimeSpan.FromSeconds(1), ct));
            watch.Stop();
            var postgres = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.NotNull(postgres);
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, postgres.SqlState);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Took {watch.Elapsed}");
            await tx.RollbackAsync(ct);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await PostgresFixture.ExecAsync(admin!, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }
}
