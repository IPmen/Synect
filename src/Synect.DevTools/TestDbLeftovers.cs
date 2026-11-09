using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Synect.DevTools;

/// <summary>
/// Lists (and with --drop removes) temporary databases and roles left in the local cluster by PostgreSQL tests:
/// databases synect_test_*, roles synect_rls_* and synect_own_*. Uses the test admin connection string
/// (user-secret ConnectionStrings:SynectTestAdmin or env SYNECT_TEST_PG); never prints it.
/// </summary>
public static class TestDbLeftovers
{
    public static async Task<int> RunAsync(IConfiguration configuration, bool drop)
    {
        var admin = Environment.GetEnvironmentVariable("SYNECT_TEST_PG") is { Length: > 0 } env
            ? env
            : configuration.GetConnectionString("SynectTestAdmin");
        if (string.IsNullOrEmpty(admin))
        {
            Console.Error.WriteLine("ConnectionStrings:SynectTestAdmin (user-secrets) or SYNECT_TEST_PG is not set.");
            return 2;
        }

        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        var databases = await ListAsync(connection, @"SELECT datname FROM pg_database WHERE datname LIKE 'synect\_test\_%' ORDER BY 1");
        var roles = await ListAsync(connection, @"SELECT rolname FROM pg_roles WHERE rolname LIKE 'synect\_rls\_%' OR rolname LIKE 'synect\_own\_%' ORDER BY 1");
        Console.WriteLine($"Temporary test databases: {databases.Count}");
        foreach (var d in databases) Console.WriteLine("  " + d);
        Console.WriteLine($"Temporary test roles: {roles.Count}");
        foreach (var r in roles) Console.WriteLine("  " + r);

        if (drop)
        {
            foreach (var d in databases) await ExecAsync(connection, $"DROP DATABASE IF EXISTS \"{d}\" WITH (FORCE)");
            foreach (var r in roles) await ExecAsync(connection, $"DROP ROLE IF EXISTS \"{r}\"");
            Console.WriteLine($"Dropped {databases.Count} databases and {roles.Count} roles.");
        }
        return 0;
    }

    private static async Task<List<string>> ListAsync(NpgsqlConnection connection, string sql)
    {
        var result = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
