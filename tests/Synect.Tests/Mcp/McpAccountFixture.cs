using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Npgsql;
using Synect.Application.Abstractions;
using Synect.Application.Access;
using Synect.Domain.Access;
using Synect.Domain.Attention;
using Synect.Domain.Sync;
using Synect.Infrastructure.Accounts;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Mcp;

/// <summary>
/// Two accounts on their own database behind the real Api host (stage 6.5). Account A serves three organizations
/// (Ўзбек Қурилиш, Ромашка Север, Ромашка Юг), account B one (Бета Трейд); a classic single-organization tenant ("Solo")
/// keeps its own key. Every organization has a fake bank loaded through the sync services; Ромашка Юг gets a critical
/// attention item and Ўзбек Қурилиш a high one, so that sorting is known.
/// </summary>
public sealed class McpAccountFixture : IAsyncLifetime
{
    private readonly PostgresFixture _pg = new();
    private readonly string _keysPath = Path.Combine(Path.GetTempPath(), "synect-test-keys-" + Guid.NewGuid().ToString("N"));
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public bool Available => _pg.Available;
    public PostgresFixture Postgres => _pg;
    public TestHost Host => _pg.Host;
    public WebApplicationFactory<Program> Api { get; private set; } = null!;

    public Guid AccountA { get; private set; }
    public Guid AccountB { get; private set; }
    public AccountOrganization Uzbek { get; private set; } = null!;
    public AccountOrganization North { get; private set; } = null!;
    public AccountOrganization South { get; private set; } = null!;
    public AccountOrganization Beta { get; private set; } = null!;
    public Guid Solo { get; private set; }
    public Dictionary<Guid, Guid> BankOf { get; } = [];
    public Guid CriticalItem { get; private set; }

    public CreatedApiKey KeyA { get; private set; } = null!;
    public CreatedApiKey KeyANorth { get; private set; } = null!;
    public CreatedApiKey KeyAReadOnly { get; private set; } = null!;
    public CreatedApiKey KeyB { get; private set; } = null!;
    public CreatedApiKey KeySolo { get; private set; } = null!;
    public CreatedApiKey KeyRevoked { get; private set; } = null!;

    public AccountAdmin Admin() => AccountAdmin.Create(_pg.AdminDbConnectionString, Host.Time);

    public async ValueTask InitializeAsync()
    {
        await _pg.InitializeAsync();
        if (!_pg.Available) return;

        var admin = Admin();
        await using (admin.Db)
        {
            AccountA = (await admin.CreateAccountAsync("Buxgalteriya A")).Id;
            AccountB = (await admin.CreateAccountAsync("Buxgalteriya B")).Id;
            Uzbek = (await admin.AddOrganizationAsync(AccountA, "ООО «Ўзбек Қурилиш»", "301111111", createTenant: true)).Organization;
            North = (await admin.AddOrganizationAsync(AccountA, "Ромашка Север", "302222222", createTenant: true)).Organization;
            South = (await admin.AddOrganizationAsync(AccountA, "Ромашка Юг", "303333333", createTenant: true)).Organization;
            Beta = (await admin.AddOrganizationAsync(AccountB, "Бета Трейд", "304444444", createTenant: true)).Organization;
            KeyA = (await admin.CreateAccountKeyAsync(AccountA, [ApiKey.ScopeRead, ApiKey.ScopeReview], "A all")).Key;
            KeyANorth = (await admin.CreateAccountKeyAsync(AccountA, [ApiKey.ScopeRead], "A north", [North.Tin!])).Key;
            KeyAReadOnly = (await admin.CreateAccountKeyAsync(AccountA, [ApiKey.ScopeRead], "A read only")).Key;
            KeyB = (await admin.CreateAccountKeyAsync(AccountB, [ApiKey.ScopeRead], "B all")).Key;
            KeyRevoked = (await admin.CreateAccountKeyAsync(AccountA, [ApiKey.ScopeRead], "A revoked")).Key;
            await admin.RevokeAccountKeyAsync(AccountA, KeyRevoked.Id.ToString());
        }
        Solo = await Host.CreateTenantAsync("Solo client");
        KeySolo = await Host.InTenantAsync(Solo, sp => sp.GetRequiredService<ApiKeyService>().CreateAsync("solo", [ApiKey.ScopeRead], CancellationToken.None));

        var seed = 70;
        foreach (var tenant in new[] { Uzbek.TenantId, North.TenantId, South.TenantId, Beta.TenantId, Solo })
        {
            var bank = await Host.CreateConnectionAsync(tenant, AdapterKind.Bank);
            Host.FakeBank.For(bank).Seed = seed++;
            await Host.SyncBankAsync(tenant, bank);
            BankOf[tenant] = bank;
        }
        // Known attention items (the rules may add their own; these decide the order).
        CriticalItem = await AddItemAsync(South.TenantId, AttentionSeverity.Critical, "test.critical");
        await AddItemAsync(Uzbek.TenantId, AttentionSeverity.High, "test.high");
        await AddItemAsync(Beta.TenantId, AttentionSeverity.Critical, "test.other_account");

        Api = CreateApi();
    }

    public async Task<Guid> AddItemAsync(Guid tenant, AttentionSeverity severity, string rule)
    {
        var now = Host.Time.GetUtcNow();
        return await Host.InTenantAsync(tenant, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            var item = new AttentionItem
            {
                RuleCode = rule, Category = AttentionCategory.Bank, Severity = severity, Title = "Test " + rule, Body = "Test item.",
                Details = "{}", EntityKind = "tenant", EntityRef = "", DedupKey = rule + ":" + Guid.NewGuid().ToString("N"),
                Status = AttentionStatus.New, FirstSeenAt = now, LastSeenAt = now, UpdatedAt = now,
            };
            await db.InTransactionAsync(async ct =>
            {
                db.AttentionItems.Add(item);
                await db.SaveChangesAsync(ct);
            }, CancellationToken.None);
            return item.Id;
        });
    }

    public WebApplicationFactory<Program> CreateApi(params (string Key, string Value)[] settings)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("ConnectionStrings:Synect", _pg.AppConnectionString);
            b.UseSetting("Secrets:KeysPath", _keysPath);
            b.UseSetting("Mcp:RateLimit:RequestsPerMinutePerKey", "10000");
            b.UseSetting("Mcp:HttpRateLimit:RequestsPerMinutePerKey", "100000");
            b.UseSetting("Mcp:HttpRateLimit:RequestsPerMinutePerIp", "100000");
            b.UseSetting("Mcp:HttpRateLimit:FailedAuthPerMinutePerIp", "100000");
            b.UseSetting("Analytics:QueryTimeout", TestHost.TestQueryTimeout.ToString());
            b.UseSetting("Mcp:CallTimeout", "00:00:30");
            foreach (var (key, value) in settings) b.UseSetting(key, value);
        });
        _factories.Add(factory);
        return factory;
    }

    public async Task<McpClient> ClientAsync(CreatedApiKey key, WebApplicationFactory<Program>? api = null)
    {
        api ??= Api;
        var http = api.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key.Key },
        }, http, null, true);
        return await McpClient.CreateAsync(transport);
    }

    public Task<(JsonElement Body, ModelContextProtocol.Protocol.CallToolResult Raw)> CallAsync(McpClient client, string tool, object? args = null) =>
        McpFixture.CallAsync(client, tool, args);

    public async Task<List<T>> QueryAdminAsync<T>(string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var connection = new NpgsqlConnection(_pg.AdminDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<T>();
        while (await reader.ReadAsync()) result.Add(read(reader));
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var f in _factories) await f.DisposeAsync();
        await _pg.DisposeAsync();
        try { if (Directory.Exists(_keysPath)) Directory.Delete(_keysPath, recursive: true); } catch (IOException) { }
    }
}
