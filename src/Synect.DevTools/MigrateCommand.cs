using Microsoft.Extensions.Configuration;
using Synect.Infrastructure.Persistence;

namespace Synect.DevTools;

/// <summary>
/// `migrate`: the deployment step that applies EF Core migrations as the schema owner (ConnectionStrings:SynectOwner,
/// from user-secrets locally or the environment on a server). Uses the same engine as `dotnet ef database update` and
/// EF bundles (MigrateAsync with the EF 9+ database-wide migration lock), plus lock_timeout on every session.
/// Prints migration names only, never the connection string.
/// </summary>
public static class MigrateCommand
{
    public static async Task<int> RunAsync(IConfiguration configuration, CliArgs options)
    {
        var owner = configuration.GetConnectionString("SynectOwner");
        if (string.IsNullOrWhiteSpace(owner))
        {
            Console.Error.WriteLine("ConnectionStrings:SynectOwner is not set (user-secrets, or the ConnectionStrings__SynectOwner environment variable).");
            return 1;
        }

        if (options.Get("status") == "true")
        {
            var status = await SchemaMigrator.StatusAsync(owner, CancellationToken.None);
            Print(status);
            return status.Compatible ? 0 : 2;
        }

        var lockTimeout = TimeSpan.FromSeconds(options.Int("lock-timeout", (int)SchemaMigrator.DefaultLockTimeout.TotalSeconds));
        var before = await SchemaMigrator.StatusAsync(owner, CancellationToken.None);
        if (!before.DatabaseAvailable)
        {
            Console.Error.WriteLine($"Database is not available: {before.Error}");
            return 1;
        }
        Console.WriteLine($"Applied: {before.AppliedLatest ?? "(none)"}; pending: {before.Pending.Count}; lock_timeout: {lockTimeout.TotalSeconds:0} s");
        foreach (var name in before.Pending) Console.WriteLine($"  pending  {name}");

        var applied = await SchemaMigrator.MigrateAsync(owner, lockTimeout, CancellationToken.None);
        foreach (var name in applied) Console.WriteLine($"  applied  {name}");

        var after = await SchemaMigrator.StatusAsync(owner, CancellationToken.None);
        Print(after);
        return after.Compatible ? 0 : 2;
    }

    private static void Print(SchemaStatus s)
    {
        Console.WriteLine($"Database available: {s.DatabaseAvailable}{(s.Error is null ? "" : $" ({s.Error})")}");
        Console.WriteLine($"Schema expected by this build: {s.ExpectedLatest}");
        Console.WriteLine($"Schema applied in the database: {s.AppliedLatest ?? "(none)"}");
        Console.WriteLine($"Compatible: {s.Compatible}; pending: {s.Pending.Count}; applied but unknown to this build: {s.Unknown.Count}");
        foreach (var name in s.Pending) Console.WriteLine($"  pending  {name}");
        foreach (var name in s.Unknown) Console.WriteLine($"  unknown  {name}");
    }
}
