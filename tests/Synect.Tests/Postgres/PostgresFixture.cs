using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Synect.Application.Abstractions;
using Synect.Infrastructure;
using Synect.Infrastructure.Persistence;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.Postgres;

/// <summary>
/// Creates a throwaway database on the local PostgreSQL, applies the real migrations as the admin
/// (table owner), and runs the application as a separate non-owner role so that row-level
/// security is actually enforced. Everything is dropped afterwards.
///
/// Configure an admin (superuser) connection string, without committing it:
///   dotnet user-secrets set "ConnectionStrings:SynectTestAdmin" "Host=localhost;Username=postgres;Password=..." --project tests/Synect.Tests
/// or the environment variable SYNECT_TEST_PG. When missing, these tests are skipped.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string SkipReason =
        "PostgreSQL tests skipped: set user-secret ConnectionStrings:SynectTestAdmin (tests/Synect.Tests) or env SYNECT_TEST_PG.";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];
    private string? _admin;

    public bool Available => Host is not null;
    public TestHost Host { get; private set; } = null!;
    public string DatabaseName => "synect_test_" + _suffix;
    public string AppRole => "synect_rls_" + _suffix;
    public string AdminDbConnectionString { get; private set; } = string.Empty;

    public static string? AdminConnectionString() =>
        Environment.GetEnvironmentVariable("SYNECT_TEST_PG") is { Length: > 0 } env
            ? env
            : new ConfigurationBuilder().AddUserSecrets<PostgresFixture>(optional: true).Build()
                .GetConnectionString("SynectTestAdmin");

    public async ValueTask InitializeAsync()
    {
        _admin = AdminConnectionString();
        if (_admin is null) return;

        await ExecAsync(_admin, $"CREATE DATABASE {DatabaseName}");
        AdminDbConnectionString = new NpgsqlConnectionStringBuilder(_admin) { Database = DatabaseName }.ConnectionString;

        var options = new DbContextOptionsBuilder<SynectDbContext>().UseSynectNpgsql(AdminDbConnectionString).Options;
        await using (var ctx = new SynectDbContext(options, new TenantContext()))
            await ctx.Database.MigrateAsync();

        await ExecAsync(AdminDbConnectionString, $"""
            CREATE ROLE {AppRole} NOLOGIN;
            GRANT USAGE ON SCHEMA public TO {AppRole};
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {AppRole};
            """);

        // The application session switches to the restricted role right at connection start.
        var app = new NpgsqlConnectionStringBuilder(AdminDbConnectionString) { Options = $"-c role={AppRole}" }.ConnectionString;
        Host = TestHost.CreatePostgres(app, CleanupAsync);
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
    }

    private async Task CleanupAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await ExecAsync(_admin!, $"DROP DATABASE IF EXISTS {DatabaseName} WITH (FORCE)");
        await ExecAsync(_admin!, $"DROP ROLE IF EXISTS {AppRole}");
    }

    public static async Task ExecAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
