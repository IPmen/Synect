using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Synect.Application.Access;
using Synect.Application.Ingest;
using Synect.Domain.Access;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Ingest;

/// <summary>An organization with a TIN, its 1C connection and an ingest key.</summary>
public sealed record IngestOrganization(Guid TenantId, string Tin, CreatedIngestKey Key);

/// <summary>
/// The real Synect.Api host (in-memory server) on a throwaway PostgreSQL database where the application runs as a role
/// without owner rights (row-level security applies). Tests create their own organizations, so they do not depend on order.
/// The default host has generous limits; tests of limits create their own host.
/// </summary>
public sealed class IngestFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private readonly string _keysPath = Path.Combine(Path.GetTempPath(), "synect-test-keys-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private int _tin;

    public bool Available => _postgres.Available;
    public PostgresFixture Postgres => _postgres;
    public TestHost Host => _postgres.Host;
    public CapturingLoggerProvider ApiLogs { get; } = new();
    public WebApplicationFactory<Program> Api { get; private set; } = null!;

    /// <summary>Every ingest and MCP key created by the fixture, for the leak checks.</summary>
    public List<string> AllKeys { get; } = [];

    public async ValueTask InitializeAsync()
    {
        await _postgres.InitializeAsync();
        if (!_postgres.Available) return;
        Api = CreateApi();
    }

    public WebApplicationFactory<Program> CreateApi(params (string Key, string Value)[] settings)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Synect", _postgres.AppConnectionString);
            b.UseSetting("Secrets:KeysPath", _keysPath);
            b.UseSetting("Ingest:RateLimit:PackagesPerMinute", "100000");
            b.UseSetting("Ingest:RateLimit:FailedAuthPerMinutePerIp", "100000");
            b.UseSetting("Mcp:HttpRateLimit:FailedAuthPerMinutePerIp", "100000");
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            b.ConfigureLogging(l => l.AddProvider(ApiLogs).SetMinimumLevel(LogLevel.Trace));
        });
        _factories.Add(factory);
        return factory;
    }

    public string NextTin() => (310_000_000 + Interlocked.Increment(ref _tin)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A new organization with a TIN and an ingest key (its 1C connection is created with the key).</summary>
    public async Task<IngestOrganization> NewOrganizationAsync(string name, string? tin = null)
    {
        var tenant = await Host.CreateTenantAsync(name);
        tin ??= NextTin();
        await ExecAdminAsync($"UPDATE tenants SET tin = '{tin}' WHERE id = '{tenant}'");
        var key = await CreateKeyAsync(tenant, "test key");
        return new IngestOrganization(tenant, tin, key);
    }

    public async Task<CreatedIngestKey> CreateKeyAsync(Guid tenant, string name)
    {
        var key = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<IngestKeyService>().CreateAsync(name, CancellationToken.None));
        AllKeys.Add(key.Key);
        return key;
    }

    public async Task<CreatedApiKey> CreateMcpKeyAsync(Guid tenant)
    {
        var key = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ApiKeyService>().CreateAsync("mcp", [ApiKey.ScopeRead], CancellationToken.None));
        AllKeys.Add(key.Key);
        return key;
    }

    public OneCSampleClient Client(IngestOrganization org, WebApplicationFactory<Program>? api = null) =>
        new((api ?? Api).CreateClient(), org.Key.Key, org.Tin);

    public OneCSampleClient Client(string key, string tin, WebApplicationFactory<Program>? api = null) =>
        new((api ?? Api).CreateClient(), key, tin);

    public Task ExecAdminAsync(string sql) => PostgresFixture.ExecAsync(_postgres.AdminDbConnectionString, sql);

    public async Task<List<T>> QueryAdminAsync<T>(string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var connection = new NpgsqlConnection(_postgres.AdminDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<T>();
        while (await reader.ReadAsync()) result.Add(read(reader));
        return result;
    }

    public Task<T> ScalarAsync<T>(string sql) => _postgres.ScalarAsync<T>(sql);

    /// <summary>One column of one row of an organization (as the owner, bypassing RLS), or null.</summary>
    public async Task<string?> ValueAsync(string table, Guid tenant, string where, string column)
    {
        var rows = await QueryAdminAsync($"SELECT ({column})::text FROM {table} WHERE tenant_id = '{tenant}' AND {where}", r => r.IsDBNull(0) ? null : r.GetString(0));
        return rows.SingleOrDefault();
    }

    public Task<long> CountAsync(string table, Guid tenant, string where = "true") =>
        ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE tenant_id = '{tenant}' AND {where}");

    public static JsonElement Item(JsonElement body, int index) => body.GetProperty("items")[index];

    public async ValueTask DisposeAsync()
    {
        foreach (var f in _factories) await f.DisposeAsync();
        await _postgres.DisposeAsync();
        try { if (Directory.Exists(_keysPath)) Directory.Delete(_keysPath, recursive: true); } catch (IOException) { }
    }
}
