using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Npgsql;
using Synect.Application.Abstractions;
using Synect.Application.Access;
using Synect.Domain.Access;
using Synect.Mcp.Hosting;
using Synect.Mcp.Responses;
using Synect.Tests.Postgres;

namespace Synect.Tests.Mcp;

/// <summary>The presented API key never ends up in logs, tables or error texts.</summary>
public class McpKeyLeakTests(McpFixture f) : IClassFixture<McpFixture>
{
    [Fact]
    public async Task Keys_are_not_in_logs_tables_or_error_messages()
    {
        Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);
        var texts = new List<string>();

        // Successful, failing and unauthenticated traffic with every key.
        await using (var client = await f.ClientAsync(f.KeyA))
        {
            foreach (var tool in McpToolTests.AllTools.Where(t => t != "request_data_refresh"))
                texts.Add((await McpFixture.CallAsync(client, tool, McpToolTests.ValidArgs[tool])).Body.GetRawText());
            texts.Add((await McpFixture.CallAsync(client, "get_cash_flow", new { from = "x", to = "y" })).Body.GetRawText());
            texts.Add((await McpFixture.CallAsync(client, "request_data_refresh")).Body.GetRawText());
        }
        var revoked = await Record.ExceptionAsync(() => f.ClientAsync(f.KeyRevoked));
        Assert.NotNull(revoked);
        texts.Add(revoked.ToString());
        var wrong = await Record.ExceptionAsync(() => f.ClientAsync(f.KeyA.Key + "x"));
        Assert.NotNull(wrong);
        texts.Add(wrong.ToString());

        // Every text column of every table (as JSON rows) of the test database.
        var tables = await f.QueryAdminAsync(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public'", r => r.GetString(0));
        foreach (var table in tables)
            texts.AddRange(await f.QueryAdminAsync($"SELECT row_to_json(t)::text FROM \"{table}\" t", r => r.GetString(0)));
        Assert.Contains("api_keys", tables);
        Assert.Contains("tool_audit_log", tables);

        var logs = string.Join("\n", f.ApiLogs.Lines) + string.Join("\n", f.Host.Logs.Lines);
        Assert.NotEmpty(f.ApiLogs.Lines);
        foreach (var key in f.AllKeys)
        {
            var secretPart = key[ApiKeyFormat.StoredPrefixLength..];
            Assert.DoesNotContain(secretPart, logs);
            Assert.All(texts, t => Assert.DoesNotContain(secretPart, t));
        }
    }
}

/// <summary>
/// api_keys: the application role sees only the keys of the tenant in app.tenant_id; before the tenant is known it
/// can only call synect_resolve_api_key, which returns (id, tenant_id, scopes) for one exact hash. The schema owner here
/// is an ordinary (non-superuser) role, as in production, to prove the function works without bypassing RLS elsewhere.
/// </summary>
public class ApiKeyLookupSecurityTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg = new();
    private string OwnerRole => "synect_own_" + _pg.AppRole[^12..];
    private Guid _tenantA, _tenantB;
    private CreatedApiKey _keyA = null!, _keyB = null!;

    public async ValueTask InitializeAsync()
    {
        await _pg.InitializeAsync();
        if (!_pg.Available) return;
        // Hand the key table and the function to a non-superuser owner.
        await PostgresFixture.ExecAsync(_pg.AdminDbConnectionString, $"""
            CREATE ROLE {OwnerRole} NOLOGIN;
            GRANT USAGE ON SCHEMA public TO {OwnerRole};
            ALTER TABLE api_keys OWNER TO {OwnerRole};
            ALTER FUNCTION synect_resolve_api_key(bytea) OWNER TO {OwnerRole};
            ALTER TABLE accounts OWNER TO {OwnerRole};
            ALTER TABLE account_tenants OWNER TO {OwnerRole};
            ALTER TABLE api_key_tenants OWNER TO {OwnerRole};
            GRANT SELECT ON tenants TO {OwnerRole};
            """);
        _tenantA = await _pg.Host.CreateTenantAsync("Keys A");
        _tenantB = await _pg.Host.CreateTenantAsync("Keys B");
        _keyA = await CreateAsync(_tenantA, ApiKey.ScopeRead);
        _keyB = await CreateAsync(_tenantB, ApiKey.ScopeRead, ApiKey.ScopeSyncRequest);
    }

    public async ValueTask DisposeAsync()
    {
        await _pg.DisposeAsync(); // drops the database, then the app role
        if (_pg.Available && PostgresFixture.AdminConnectionString() is { } admin)
            await PostgresFixture.ExecAsync(admin, $"DROP ROLE IF EXISTS {OwnerRole}");
    }

    private Task<CreatedApiKey> CreateAsync(Guid tenant, params string[] scopes) =>
        _pg.Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ApiKeyService>().CreateAsync("k", scopes, CancellationToken.None));

    private async Task<List<string>> AsAppAsync(string? tenant, string sql)
    {
        await using var connection = new NpgsqlConnection(_pg.AppConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, tx))
        {
            set.Parameters.AddWithValue("t", tenant ?? "");
            await set.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, tx);
        var result = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) result.Add(Convert.ToString(reader.GetValue(0))!);
        await tx.CommitAsync();
        return result;
    }

    [Fact]
    public async Task App_role_sees_only_its_tenants_keys_and_resolves_only_exact_hashes()
    {
        Assert.SkipUnless(_pg.Available, PostgresFixture.SkipReason);

        Assert.Empty(await AsAppAsync(null, "SELECT id FROM api_keys"));
        Assert.Equal([_keyA.Id.ToString()], await AsAppAsync(_tenantA.ToString(), "SELECT id FROM api_keys"));
        Assert.Equal([_keyB.Id.ToString()], await AsAppAsync(_tenantB.ToString(), "SELECT id FROM api_keys"));

        // Writing another tenant's key is impossible from tenant A.
        Assert.Empty(await AsAppAsync(_tenantA.ToString(), $"UPDATE api_keys SET revoked_at = now() WHERE id = '{_keyB.Id}' RETURNING id"));

        // The lookup function: no tenant context needed, returns only id, tenant and scopes for the exact hash.
        var hashB = Convert.ToHexString(ApiKeyFormat.Hash(_keyB.Key));
        var resolved = await AsAppAsync(null, $"SELECT row_to_json(r)::text FROM synect_resolve_api_key('\\x{hashB}'::bytea) r");
        var row = Assert.Single(resolved);
        Assert.Contains(_tenantB.ToString(), row);
        Assert.DoesNotContain("key_hash", row);
        Assert.DoesNotContain("name", row);

        Assert.Empty(await AsAppAsync(null, $"SELECT api_key_id FROM synect_resolve_api_key('\\x{new string('0', 64)}'::bytea)"));
        Assert.Empty(await AsAppAsync(null, "SELECT api_key_id FROM synect_resolve_api_key('\\x00'::bytea)"));
        Assert.Empty(await AsAppAsync(null, "SELECT api_key_id FROM synect_resolve_api_key(NULL)"));

        // Still no access to keys without a tenant context after the lookup.
        Assert.Empty(await AsAppAsync(null, "SELECT id FROM api_keys"));

        // The resolver used by the host gives the same answer; a revoked key resolves to nothing.
        await using var scope = _pg.Host.Scope(null);
        var resolver = new Synect.Infrastructure.Access.PostgresApiKeyResolver(scope.ServiceProvider.GetRequiredService<Synect.Infrastructure.Persistence.SynectDbContext>());
        var identity = await resolver.ResolveAsync(ApiKeyFormat.Hash(_keyB.Key), CancellationToken.None);
        Assert.Equal(_tenantB, identity!.TenantId);
        Assert.Equal([ApiKey.ScopeRead, ApiKey.ScopeSyncRequest], identity.Scopes);
        await _pg.Host.InTenantAsync(_tenantA, sp => sp.GetRequiredService<ApiKeyService>().RevokeAsync(_keyA.KeyPrefix, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(ApiKeyFormat.Hash(_keyA.Key), CancellationToken.None));

        // last_used_at is written by the function for the resolved key.
        Assert.Single(await AsAppAsync(_tenantB.ToString(), "SELECT last_used_at FROM api_keys WHERE last_used_at IS NOT NULL"));
    }

    [Fact]
    public async Task Audit_rows_are_isolated_by_tenant()
    {
        Assert.SkipUnless(_pg.Available, PostgresFixture.SkipReason);
        var writer = _pg.Host.Services.GetRequiredService<ToolAuditWriter>();
        await writer.WriteAsync(new ToolAuditEntry(_tenantB, _keyB.Id, "get_data_freshness", "{}", DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(3), 2, ToolCallStatus.Ok, null), CancellationToken.None);

        Assert.Single(await AsAppAsync(_tenantB.ToString(), "SELECT id FROM tool_audit_log"));
        Assert.Empty(await AsAppAsync(_tenantA.ToString(), "SELECT id FROM tool_audit_log"));
        Assert.Empty(await AsAppAsync(null, "SELECT id FROM tool_audit_log"));
    }
}

/// <summary>Synect.Mcp depends on Application (analytics, freshness, sync requests) only: no adapters, queue internals, secrets or Infrastructure.</summary>
public class McpArchitectureTests
{
    private static readonly string[] ForbiddenNamespaces =
    [
        "Synect.Infrastructure", "Synect.Application.Adapters", "Synect.Application.Sync", "Synect.Application.Resilience",
        "Synect.Application.Secrets", "Synect.Application.Queue", "Synect.Domain.Raw",
    ];

    /// <summary>The only allowed types from the queue namespace: the "refresh now" request and its result.</summary>
    private static readonly Type[] AllowedQueueTypes =
        [typeof(Synect.Application.Queue.SyncRequestService), typeof(Synect.Application.Queue.SyncRequestResult), typeof(Synect.Application.Queue.SyncRequestStatus)];

    [Fact]
    public void Mcp_assembly_does_not_reference_infrastructure()
    {
        var references = typeof(McpServiceCollection).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.Contains("Synect.Application", references);
        Assert.DoesNotContain("Synect.Infrastructure", references);
        Assert.DoesNotContain(references, r => r.StartsWith("Npgsql", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Mcp_types_use_no_adapters_queue_internals_or_secrets()
    {
        var types = typeof(McpServiceCollection).Assembly.GetTypes();
        Assert.Contains(types, t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var violations = new List<string>();
        foreach (var type in types)
        {
            var referenced = new List<Type>();
            referenced.AddRange(type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));
            referenced.AddRange(type.GetFields(all).Select(x => x.FieldType));
            referenced.AddRange(type.GetProperties(all).Select(x => x.PropertyType));
            foreach (var method in type.GetMethods(all))
            {
                referenced.Add(method.ReturnType);
                referenced.AddRange(method.GetParameters().Select(p => p.ParameterType));
                referenced.AddRange(method.GetMethodBody()?.LocalVariables.Select(v => v.LocalType) ?? []);
            }
            foreach (var t in referenced.SelectMany(Expand).Distinct())
            {
                if (AllowedQueueTypes.Contains(t)) continue;
                if (ForbiddenNamespaces.Any(ns => t.Namespace?.StartsWith(ns, StringComparison.Ordinal) == true))
                    violations.Add($"{type.FullName} -> {t.FullName}");
            }
        }
        Assert.Empty(violations);
    }

    private static IEnumerable<Type> Expand(Type t)
    {
        yield return t;
        if (t.HasElementType) foreach (var e in Expand(t.GetElementType()!)) yield return e;
        if (t.IsGenericType) foreach (var a in t.GetGenericArguments().SelectMany(Expand)) yield return a;
    }
}

/// <summary>Response helpers without a database.</summary>
public class McpResponseUnitTests
{
    [Theory]
    [InlineData(0, "UZS", "0.00")]
    [InlineData(5, "UZS", "0.05")]
    [InlineData(123_45, "UZS", "123.45")]
    [InlineData(-1_000_01, "USD", "-1000.01")]
    [InlineData(long.MinValue, "UZS", "-92233720368547758.08")]
    public void Money_has_minor_units_and_a_decimal_string(long minor, string currency, string expected)
    {
        var money = MoneyValue.Of(minor, currency);
        Assert.Equal(minor, money.AmountMinor);
        Assert.Equal(expected, money.Amount);
        Assert.Equal(currency, money.Currency);
    }

    [Theory]
    [InlineData("20208000100000000001", "****0001")]
    [InlineData("123", "****")]
    [InlineData(null, null)]
    public void Account_numbers_keep_the_last_four_characters(string? number, string? expected) =>
        Assert.Equal(expected, TextSafety.MaskAccount(number, mask: true));

    [Fact]
    public void Untrusted_text_loses_control_and_invisible_characters_and_is_cut()
    {
        var input = "A" + (char)0x07 + "B" + (char)0x202E + "C" + (char)0x200B + "D\r\n\tE  F" + new string('x', 400);
        var value = TextSafety.Untrusted(input, 300)!;
        Assert.StartsWith("A BCD E F", value.Text); // control characters become spaces, invisible ones are dropped
        Assert.Equal(301, value.Text.Length);
        Assert.True(value.Truncated);
        Assert.DoesNotContain(value.Text, c => char.IsControl(c) || c == (char)0x202E || c == (char)0x200B);
        Assert.False(TextSafety.Untrusted("short", 300)!.Truncated);
    }

    [Fact]
    public void Audit_parameters_redact_secret_names_and_cut_long_values()
    {
        var args = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            from = "2026-10-01", limit = 5, api_key = "sk_synect_secret", token = "t", note = new string('n', 1000),
        }).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        var json = AuditParameters.Serialize(args);
        Assert.Contains("\"from\":\"2026-10-01\"", json);
        Assert.Contains("\"limit\":5", json);
        Assert.DoesNotContain("sk_synect_secret", json);
        Assert.Contains("[redacted]", json);
        Assert.True(json.Length < 400, json);
    }

    [Fact]
    public void Keys_have_256_bits_of_randomness_and_a_fixed_shape()
    {
        var a = ApiKeyFormat.Generate();
        var b = ApiKeyFormat.Generate();
        Assert.NotEqual(a, b);
        Assert.StartsWith(ApiKeyFormat.Prefix, a);
        Assert.Equal(ApiKeyFormat.Prefix.Length + 43, a.Length); // 32 bytes in base64url
        Assert.True(ApiKeyFormat.LooksLikeKey(a));
        Assert.False(ApiKeyFormat.LooksLikeKey(a + "!"));
        Assert.False(ApiKeyFormat.LooksLikeKey("sk_other_" + a));
        Assert.Equal(32, ApiKeyFormat.Hash(a).Length);
        Assert.DoesNotContain(a[ApiKeyFormat.StoredPrefixLength..], new CreatedApiKey(Guid.NewGuid(), "n", a[..16], ["read"], a).ToString());
    }
}
