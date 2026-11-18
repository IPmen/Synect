using Microsoft.EntityFrameworkCore;
using Npgsql;
using Synect.Application.Abstractions;

namespace Synect.Infrastructure.Persistence;

/// <summary>
/// Applies the EF Core migrations as the schema owner (synect_owner), as a separate deployment step: Api and Worker
/// never migrate at startup, they only check the schema (<see cref="SchemaCompatibility"/>). Every session of the
/// migration gets <c>lock_timeout</c> (default 5 s), so a migration that would wait behind locks held by the running
/// application fails fast instead of blocking every query queued behind it.
/// </summary>
public static class SchemaMigrator
{
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The owner connection string with <c>-c lock_timeout=...</c> added to the session options.</summary>
    public static string WithLockTimeout(string ownerConnectionString, TimeSpan lockTimeout)
    {
        var builder = new NpgsqlConnectionStringBuilder(ownerConnectionString);
        var option = $"-c lock_timeout={(long)lockTimeout.TotalMilliseconds}";
        builder.Options = string.IsNullOrWhiteSpace(builder.Options) ? option : builder.Options + " " + option;
        builder.ApplicationName ??= "synect-migrate";
        return builder.ConnectionString;
    }

    public static SynectDbContext CreateContext(string ownerConnectionString, TimeSpan lockTimeout) =>
        new(new DbContextOptionsBuilder<SynectDbContext>()
            .UseSynectNpgsql(WithLockTimeout(ownerConnectionString, lockTimeout))
            .Options, new TenantContext());

    public static async Task<SchemaStatus> StatusAsync(string ownerConnectionString, CancellationToken ct)
    {
        await using var db = CreateContext(ownerConnectionString, DefaultLockTimeout);
        return await new SchemaCompatibility(db).CheckAsync(ct);
    }

    /// <summary>Applies every pending migration; returns the migrations applied by this call.</summary>
    public static async Task<IReadOnlyList<string>> MigrateAsync(string ownerConnectionString, TimeSpan lockTimeout, CancellationToken ct)
    {
        await using var db = CreateContext(ownerConnectionString, lockTimeout);
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        // Long index builds must not hit a command timeout; locks are bounded by lock_timeout instead.
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
        await db.Database.MigrateAsync(ct);
        return pending;
    }
}
