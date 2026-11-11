using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Npgsql;
using Synect.Application.Abstractions;
using Synect.Application.Accounts;
using Synect.Application.Freshness;
using Synect.Application.Ingest;
using Synect.Application.Queue;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Infrastructure.Persistence.Migrations;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Ingest;

/// <summary>Row-level security of the new tables, the key lookup function, accounts, logs, freshness of the 1C source.</summary>
public class IngestSecurityTests(IngestFixture f) : IClassFixture<IngestFixture>
{
    private void RequirePostgres() => Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);

    private static DateTimeOffset T(int minutes) => new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(5)).AddMinutes(minutes);

    private static readonly string[] AllNewTables = [.. Accounting1CIngest.ForcedTables, "ingest_keys"];

    private Task<int> CountAsAppAsync(Guid? tenant, string table, string where = "true") =>
        f.Host.QueryAsync(tenant, db => db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table} WHERE {where}").SingleAsync());

    [Fact]
    public async Task Application_role_sees_nothing_without_a_tenant_and_only_its_own_rows_with_one()
    {
        RequirePostgres();
        var a = await f.NewOrganizationAsync("Ingest RLS A");
        var b = await f.NewOrganizationAsync("Ingest RLS B");
        foreach (var org in new[] { a, b })
        {
            var steps = new OneCSampleScenarios(f.Client(org));
            await steps.BasicAsync();
            var client = f.Client(org);
            await client.SendAsync(client.Envelope("counterparties", "rls-full", 1, new object[] { OneCSampleClient.Counterparty("cp-001", "x", null, T(0)) }, "full", "RLS", false));
        }

        foreach (var table in AllNewTables)
        {
            Assert.True(await f.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE tenant_id IN ('{a.TenantId}', '{b.TenantId}')") > 0, table);
            Assert.Equal(0, await CountAsAppAsync(null, table));
            Assert.True(await CountAsAppAsync(a.TenantId, table) > 0, table);
            Assert.Equal(0, await CountAsAppAsync(a.TenantId, table, $"tenant_id <> '{a.TenantId}'"));
        }

        // Writing a row of another organization is refused by the database itself.
        await using var scope = f.Host.Scope(a.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.InTransactionAsync(ct => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE accounting_counterparties SET name = 'hijack' WHERE tenant_id = {b.TenantId};
            INSERT INTO ingest_full_sync_items (tenant_id, full_sync_id, entity, item_key, created_at) VALUES ({b.TenantId}, 'x', 'counterparties', 'k', now())
            """, ct), CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Equal(0L, await f.CountAsync("accounting_counterparties", b.TenantId, "name = 'hijack'"));
    }

    [Fact]
    public async Task Key_lookup_function_is_a_pinned_security_definer_and_returns_the_minimum()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest function");
        var rows = await f.QueryAdminAsync("""
            SELECT p.prosecdef, array_to_string(p.proconfig, ';'), coalesce(array_to_string(p.proacl, ','), ''), pg_get_function_result(p.oid)
            FROM pg_proc p WHERE p.proname = 'synect_resolve_ingest_key'
            """, r => (r.GetBoolean(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        var (definer, config, acl, result) = Assert.Single(rows);
        Assert.True(definer);
        Assert.Equal("search_path=pg_catalog, pg_temp", config);
        Assert.DoesNotContain(",=X/", "," + acl); // no EXECUTE for PUBLIC
        Assert.Contains(f.Postgres.AppRole + "=X/", acl);
        Assert.Equal("TABLE(ingest_key_id uuid, tenant_id uuid, connection_id uuid, tenant_tin text)", result);

        async Task<List<(Guid Key, Guid Tenant, string? Tin)>> ResolveAsync(byte[]? hash) =>
            await f.Host.QueryAsync<List<(Guid, Guid, string?)>>(null, async db =>
            {
                var list = await db.Database.SqlQuery<ResolvedRow>($"SELECT ingest_key_id, tenant_id, connection_id, tenant_tin FROM synect_resolve_ingest_key({hash})").ToListAsync();
                return list.Select(x => (x.IngestKeyId, x.TenantId, x.TenantTin)).ToList();
            });

        var found = Assert.Single(await ResolveAsync(IngestKeyFormat.Hash(org.Key.Key)));
        Assert.Equal((org.Key.Id, org.TenantId, org.Tin), found);
        Assert.Empty(await ResolveAsync(IngestKeyFormat.Hash(IngestKeyFormat.Generate())));
        Assert.Empty(await ResolveAsync(new byte[16]));
        Assert.Empty(await ResolveAsync(null));
        // An MCP key's hash never resolves as an ingest key.
        var mcp = await f.CreateMcpKeyAsync(org.TenantId);
        Assert.Empty(await ResolveAsync(IngestKeyFormat.Hash(mcp.Key)));
        // The application role cannot read ingest_keys without the organization's context.
        Assert.Equal(0, await CountAsAppAsync(null, "ingest_keys"));
        Assert.Equal(1, await CountAsAppAsync(org.TenantId, "ingest_keys"));
        // last_used_at is written by the function.
        Assert.NotNull(await f.ValueAsync("ingest_keys", org.TenantId, $"id = '{org.Key.Id}'", "last_used_at"));
    }

    private sealed class ResolvedRow
    {
        public Guid IngestKeyId { get; set; }
        public Guid TenantId { get; set; }
        public Guid ConnectionId { get; set; }
        public string? TenantTin { get; set; }
    }

    [Fact]
    public async Task Organizations_of_one_account_never_mix_and_a_disabled_organization_is_refused()
    {
        RequirePostgres();
        var one = await f.NewOrganizationAsync("Ingest account org 1");
        var two = await f.NewOrganizationAsync("Ingest account org 2");
        var account = Guid.NewGuid();
        await f.ExecAdminAsync($"INSERT INTO accounts (id, name, status, created_at) VALUES ('{account}', 'Ingest account {account:N}', 'Active', now())");
        await f.ExecAdminAsync($"INSERT INTO account_tenants (account_id, tenant_id, added_at) VALUES ('{account}', '{one.TenantId}', now()), ('{account}', '{two.TenantId}', now())");

        foreach (var (org, name) in new[] { (one, "Org one name"), (two, "Org two name") })
        {
            var client = f.Client(org);
            var r = await client.SendAsync(client.Envelope("counterparties", "acc-1", 1, new object[] { OneCSampleClient.Counterparty("shared-ref", name, null, T(0)) }));
            Assert.Equal(HttpStatusCode.OK, r.Status);
        }
        Assert.Equal("Org one name", await f.ValueAsync("accounting_counterparties", one.TenantId, "ref = 'shared-ref'", "name"));
        Assert.Equal("Org two name", await f.ValueAsync("accounting_counterparties", two.TenantId, "ref = 'shared-ref'", "name"));
        // The same batch_id in two organizations is two packages.
        Assert.Equal(1L, await f.CountAsync("ingest_batches", one.TenantId, "batch_id = 'acc-1'"));
        Assert.Equal(1L, await f.CountAsync("ingest_batches", two.TenantId, "batch_id = 'acc-1'"));
        // One organization's key cannot write into the other, even inside the account.
        var cross = await f.Client(one.Key.Key, two.Tin).SendAsync(f.Client(one.Key.Key, two.Tin).Envelope("counterparties", "acc-2", 1,
            new object[] { OneCSampleClient.Counterparty("x", "x", null, T(0)) }));
        Assert.Equal(HttpStatusCode.Forbidden, cross.Status);

        await f.ExecAdminAsync($"UPDATE account_tenants SET disabled_at = now() WHERE tenant_id = '{two.TenantId}'");
        var disabled = await f.Client(two).SendAsync(f.Client(two).Envelope("counterparties", "acc-3", 1, new object[] { OneCSampleClient.Counterparty("y", "y", null, T(0)) }));
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.Status);
    }

    [Fact]
    public async Task Disabled_connection_refuses_packages_and_1c_is_never_queued()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest disabled connection");
        await f.ExecAdminAsync($"UPDATE connections SET status = 'Disabled' WHERE id = '{org.Key.ConnectionId}'");
        var r = await f.Client(org).SendAsync(f.Client(org).Envelope("counterparties", "dis-1", 1, new object[] { OneCSampleClient.Counterparty("d", "d", null, T(0)) }));
        Assert.Equal(HttpStatusCode.Forbidden, r.Status);
        Assert.Equal("connection_disabled", r.Get("error"));
        Assert.Equal(0L, await f.CountAsync("accounting_counterparties", org.TenantId));

        await f.ExecAdminAsync($"UPDATE connections SET status = 'Active' WHERE id = '{org.Key.ConnectionId}'");
        var request = await f.Host.InTenantAsync(org.TenantId, sp => sp.GetRequiredService<SyncRequestService>().RequestSyncAsync(org.Key.ConnectionId, CancellationToken.None));
        Assert.Equal(SyncRequestStatus.NotLoadable, request.Status);
        Assert.Equal("Accounting1C", await f.ValueAsync("connections", org.TenantId, $"id = '{org.Key.ConnectionId}'", "adapter_kind"));
        Assert.Equal("1c", await f.ValueAsync("connections", org.TenantId, $"id = '{org.Key.ConnectionId}'", "source_code"));
    }

    [Fact]
    public async Task Logs_carry_counters_and_ids_but_no_item_content_or_keys()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest logs");
        var client = f.Client(org);
        const string secretName = "Very Private Name 7f3a";
        const string secretPurpose = "Secret purpose text 91bd";
        await client.SendAsync(client.Envelope("counterparties", "log-batch-1", 1, new object[]
        {
            OneCSampleClient.Counterparty("log-ref", secretName, "301000001", T(0)), OneCSampleClient.Counterparty("log-bad", secretName, "1", T(0)),
        }));
        var bank = OneCSampleClient.BankDocument("log-bd", "Payment", "c", null, 10m, T(0));
        bank["purpose"] = secretPurpose;
        await client.SendAsync(client.Envelope("bank_documents", "log-batch-2", 1, new object[] { bank }));

        var lines = f.ApiLogs.Lines.ToList();
        Assert.Contains(lines, l => l.Contains("log-batch-1") && l.Contains("accepted 1") && l.Contains("rejected 1"));
        Assert.DoesNotContain(lines, l => l.Contains(secretName) || l.Contains(secretPurpose) || l.Contains("log-ref"));
        foreach (var key in f.AllKeys) Assert.DoesNotContain(lines, l => l.Contains(key));
    }

    [Fact]
    public async Task Freshness_of_the_1c_source_is_the_last_accepted_package_with_its_own_threshold()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest freshness");
        async Task<SourceFreshness> OneCAsync() =>
            (await f.Host.InTenantAsync(org.TenantId, sp => sp.GetRequiredService<ISourceFreshnessQuery>().GetAsync(org.TenantId, CancellationToken.None)))
            .Single(s => s.Kind == AdapterKind.Accounting1C);

        var before = await OneCAsync();
        Assert.Null(before.LastSuccessfulSyncAt);
        Assert.Equal(TimeSpan.FromHours(48), before.StaleAfter);
        Assert.True(before.IsStale);

        // A package whose items were all refused does not count.
        var client = f.Client(org);
        await client.SendAsync(client.Envelope("counterparties", "fr-1", 1, new object[] { OneCSampleClient.Counterparty("", "x", null, T(0)) }));
        Assert.Null((await OneCAsync()).LastSuccessfulSyncAt);

        await client.SendAsync(client.Envelope("counterparties", "fr-2", 1, new object[] { OneCSampleClient.Counterparty("fr", "x", null, T(0)) }));
        Assert.NotNull((await OneCAsync()).LastSuccessfulSyncAt);

        var now = f.Host.Time.Now;
        await f.ExecAdminAsync($"UPDATE connections SET last_successful_sync_at = '{now.AddHours(-47):O}' WHERE id = '{org.Key.ConnectionId}'");
        Assert.False((await OneCAsync()).IsStale);
        await f.ExecAdminAsync($"UPDATE connections SET last_successful_sync_at = '{now.AddHours(-49):O}' WHERE id = '{org.Key.ConnectionId}'");
        Assert.True((await OneCAsync()).IsStale);

        // The portfolio rows carry the same threshold.
        var summary = await f.Host.InTenantAsync(org.TenantId, sp => sp.GetRequiredService<PortfolioQueries>().SummaryAsync(false, CancellationToken.None));
        Assert.Equal(TimeSpan.FromHours(48), summary.Data.Connections.Single(c => c.Kind == AdapterKind.Accounting1C).StaleAfter);

        // MCP: data_freshness lists the 1C source and its stale note uses 48 hours (real clock of the Api host).
        await f.ExecAdminAsync($"UPDATE connections SET last_successful_sync_at = now() - interval '49 hours' WHERE id = '{org.Key.ConnectionId}'");
        var mcp = await f.CreateMcpKeyAsync(org.TenantId);
        var http = f.Api.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + mcp.Key },
        }, http, null, true);
        await using var client2 = await McpClient.CreateAsync(transport);
        var result = await client2.CallToolAsync("get_data_freshness", new Dictionary<string, object?>());
        var body = JsonSerializer.SerializeToElement(result.StructuredContent).GetRawText();
        Assert.Contains("Accounting1C", body);
        Assert.Contains("Accounting1C source 1c was last loaded", body);
        Assert.Contains("more than 48 hours ago", body);
    }

    [Fact]
    public async Task Attention_rules_create_nothing_for_the_1c_source()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest attention");
        await f.ExecAdminAsync($"UPDATE connections SET created_at = now() - interval '30 days' WHERE id = '{org.Key.ConnectionId}'");
        await f.Host.InTenantAsync(org.TenantId, async sp =>
        {
            await sp.GetRequiredService<Synect.Application.Attention.AttentionEvaluator>()
                .EvaluateAsync(Enum.GetValues<Synect.Domain.Attention.AttentionCategory>(), CancellationToken.None);
            return true;
        });
        Assert.Equal(0L, await f.CountAsync("attention_items", org.TenantId));
    }
}
