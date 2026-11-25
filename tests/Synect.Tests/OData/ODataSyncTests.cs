using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synect.Application.Abstractions;
using Synect.Application.Accounts;
using Synect.Application.Ingest;
using Synect.Application.OData;
using Synect.Application.Queue;
using Synect.Application.Secrets;
using Synect.Application.Sync;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.OData;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.OData;

/// <summary>
/// A throwaway PostgreSQL database (row-level security on) and a test host whose OData transport is the in-process fake
/// server router. Unthrottled, fast retries, pages of 2 records so that paging is exercised on tiny data.
/// </summary>
public sealed class ODataFixture : IAsyncLifetime
{
    private readonly PostgresFixture _pg = new();

    public bool Available => _pg.Available;
    public FakeODataRouter Router { get; } = new();
    public TestHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _pg.InitializeAsync();
        if (!_pg.Available) return;
        Host = TestHost.CreatePostgres(_pg.AppConnectionString, () => Task.CompletedTask, s =>
        {
            s.AddSingleton(new ODataTransport(Router));
            s.Configure<ODataOptions>(o =>
            {
                o.RequestsPerSecond = 0;
                o.RetryBaseDelay = TimeSpan.FromMilliseconds(1);
                o.MaxRetryDelay = TimeSpan.FromMilliseconds(5);
                o.PageSize = 2;
                o.RecordsPerKeyRequest = 2;
                // Tiny data: the mass-deletion guard from 5 absent records (production: 50); the share stays 20%.
                o.MassDeleteGuard.MinCount = 5;
            });
        });
    }

    public async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var connection = new NpgsqlConnection(_pg.AdminDbConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<T>();
        while (await reader.ReadAsync()) result.Add(read(reader));
        return result;
    }

    public Task<long> CountAsync(string table, Guid tenant, string where = "true") =>
        _pg.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE tenant_id = '{tenant}' AND {where}");

    public async Task<string?> ValueAsync(string sql) => (await QueryAsync(sql, r => r.IsDBNull(0) ? null : Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture))).SingleOrDefault();

    public Task ExecAdminAsync(string sql) => PostgresFixture.ExecAsync(_pg.AdminDbConnectionString, sql);

    public async ValueTask DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
        await _pg.DisposeAsync();
    }
}

/// <summary>
/// The 1C:Fresh OData connector end to end against the fake server: full and delta loads through the real receiver, version
/// comparison, disappearing and returning records, dry run, paging protection, the queue path with its circuit breaker, the
/// one-active-1C rule, push and pull kept apart, the scheduler and the connections overview.
/// </summary>
public sealed partial class ODataSyncTests(ODataFixture f) : IClassFixture<ODataFixture>
{
    private void RequirePostgres() => Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);

    private TestHost Host => f.Host;
    private DateOnly Today => DateOnly.FromDateTime(Host.Time.GetUtcNow().ToOffset(TimeSpan.FromHours(5)).DateTime);

    private async Task<(Guid Tenant, Guid Connection, FakeODataServer Server)> NewAsync(string name, bool seed = true)
    {
        var server = f.Router.Add(FakeODataServer.CreateUnique());
        if (seed) server.SeedSample(Today);
        var tenant = await Host.CreateTenantAsync(name);
        var r = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ODataConnectionService>()
            .CreateAsync(null, server.BaseUrl, server.Login, new SecretValue(server.Password), null, null, false, CancellationToken.None));
        return (tenant, r.ConnectionId, server);
    }

    private Task<ODataSyncReport> SyncAsync(Guid tenant, Guid connection, ODataSyncMode mode, bool dryRun = false) =>
        Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ODataSyncService>().RunAsync(connection, mode, dryRun, CancellationToken.None));

    private static ODataEntityReport E(ODataSyncReport r, string entity) => r.Entities.Single(e => e.Entity == entity);

    /// <summary>Records seen in a load count as "seen before" the next one only when the clock moved (full exports compare last_seen_at).</summary>
    private void Later() => Host.Time.Advance(TimeSpan.FromHours(2));

    [Fact]
    public async Task Full_load_goes_through_the_receiver_with_the_connection_as_source()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData full");
        var report = await SyncAsync(tenant, connection, ODataSyncMode.Full);

        Assert.Equal("full", report.Mode);
        Assert.All(report.Entities, e => Assert.Equal(0, e.Rejected));
        Assert.Equal(3, await f.CountAsync("accounting_counterparties", tenant));  // the folder is filtered out
        Assert.Equal(1, await f.CountAsync("accounting_contracts", tenant));
        Assert.Equal(1, await f.CountAsync("accounting_bank_accounts", tenant));   // only the organization's own account
        Assert.Equal(2, await f.CountAsync("accounting_documents", tenant));
        Assert.Equal(2, await f.CountAsync("accounting_bank_documents", tenant));
        Assert.Equal(3, await f.CountAsync("accounting_document_lines", tenant));  // sale: goods + services; purchase: goods
        Assert.Equal(1, await f.CountAsync("accounting_bank_document_settlements", tenant));

        // The source of every record: last_batch_id -> ingest_batches.connection_id; no ingest key.
        var sources = await f.QueryAsync($"""
            SELECT DISTINCT b.connection_id, b.ingest_key_id IS NULL FROM accounting_documents d
            JOIN ingest_batches b ON b.tenant_id = d.tenant_id AND b.id = d.last_batch_id WHERE d.tenant_id = '{tenant}'
            """, r => (r.GetGuid(0), r.GetBoolean(1)));
        Assert.Equal([(connection, true)], sources);
        Assert.Equal(0, await f.CountAsync("ingest_batches", tenant, $"connection_id <> '{connection}' OR ingest_key_id IS NOT NULL"));

        Assert.Equal("USD", await f.ValueAsync($"SELECT currency FROM accounting_documents WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.Purchase1}'"));
        Assert.Equal("50050", await f.ValueAsync($"SELECT amount_total_minor FROM accounting_documents WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.Purchase1}'"));
        Assert.Equal("UZS", await f.ValueAsync($"SELECT currency FROM accounting_contracts WHERE tenant_id = '{tenant}'"));
        Assert.Equal("00014", await f.ValueAsync($"SELECT bank_code FROM accounting_bank_accounts WHERE tenant_id = '{tenant}'"));
        Assert.Null(await f.ValueAsync($"SELECT counterparty_ref FROM accounting_bank_documents WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.Payment1}'"));
        Assert.Equal("LegalEntity", await f.ValueAsync($"SELECT kind FROM accounting_counterparties WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.CpA}'"));
        Assert.NotNull(await f.ValueAsync($"SELECT extra->>'data_version' FROM accounting_counterparties WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.CpA}'"));

        // Freshness of the connection like a package of the extension, and paging really happened ($skip with pages of 2).
        Assert.NotNull(await f.ValueAsync($"SELECT last_successful_sync_at FROM connections WHERE id = '{connection}'"));
        Assert.Equal(1, await f.CountAsync("ingest_freshness", tenant, $"connection_id = '{connection}'"));
        Assert.Contains(server.Requests, r => r.Resource == "Catalog_Контрагенты" && r.Query.Contains("$skip=2", StringComparison.Ordinal));
        Assert.All(server.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Empty(report.MissingProperties.Where(p => p.StartsWith("Catalog_Контрагенты.", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Only_records_with_another_DataVersion_are_read_and_sent_again()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData versions");
        await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Later();
        server.Update("Catalog_Контрагенты", FakeODataServer.CpA, r => r["Description"] = "ООО «Альфа Плюс»");
        var before = server.Requests.Count;

        var report = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        var cp = E(report, "counterparties");
        Assert.Equal((3, 0, 1, 2, 1), (cp.Listed, cp.New, cp.Changed, cp.Unchanged, cp.Sent));
        Assert.Equal(0, cp.MarkedDeleted);
        Assert.Equal(0, E(report, "documents").Sent);
        Assert.Equal(0, report.Entities.Sum(e => e.MarkedDeleted));
        Assert.Equal("ООО «Альфа Плюс»", await f.ValueAsync($"SELECT name FROM accounting_counterparties WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.CpA}'"));
        // The unchanged counterparties were not read in full again: one by-key request for the changed one.
        var byKey = server.Requests.Skip(before).Count(r => r.Resource == "Catalog_Контрагенты" && r.Query.Contains("Ref_Key eq guid'", StringComparison.Ordinal));
        Assert.Equal(1, byKey);
    }

    [Fact]
    public async Task A_record_gone_from_the_source_is_marked_deleted_and_lifted_when_it_returns()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData deletions");
        await SyncAsync(tenant, connection, ODataSyncMode.Full);
        var gone = server.Find("Catalog_Контрагенты", FakeODataServer.CpC);
        server.Remove("Catalog_Контрагенты", FakeODataServer.CpC);
        Later();

        var dry = await SyncAsync(tenant, connection, ODataSyncMode.Full, dryRun: true);
        Assert.Equal(1, E(dry, "counterparties").MissingInSource);
        Assert.Equal("False", await f.ValueAsync($"SELECT is_deleted FROM accounting_counterparties WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.CpC}'"));

        var full = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Assert.Equal(1, E(full, "counterparties").MarkedDeleted);
        Assert.Equal("True", await f.ValueAsync($"SELECT is_deleted FROM accounting_counterparties WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.CpC}'"));
        Assert.Equal("False", await f.ValueAsync($"SELECT is_deleted FROM accounting_counterparties WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.CpA}'"));

        // Back with the same DataVersion (it was never changed in 1C): read again and the mark is lifted.
        server.Resources["Catalog_Контрагенты"].Add(gone);
        Later();
        var back = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Assert.Equal(1, E(back, "counterparties").Restored);
        Assert.Equal("False", await f.ValueAsync($"SELECT is_deleted FROM accounting_counterparties WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.CpC}'"));

        // Marked for deletion in 1C itself: stored deleted, and not read again on every load.
        server.Update("Catalog_Контрагенты", FakeODataServer.CpB, r => r["DeletionMark"] = true);
        Later();
        await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Assert.Equal("True", await f.ValueAsync($"SELECT is_deleted FROM accounting_counterparties WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.CpB}'"));
        Later();
        var again = await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Assert.Equal((0, 0, 3), (E(again, "counterparties").Restored, E(again, "counterparties").Sent, E(again, "counterparties").Unchanged));
    }

    [Fact]
    public async Task Delta_compares_documents_within_the_date_window_and_never_marks_deletions()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData delta");
        await SyncAsync(tenant, connection, ODataSyncMode.Full);
        Later();
        server.Update("Document_РеализацияТоваровУслуг", FakeODataServer.Sale1, r => r["Комментарий"] = "исправлено");
        server.Update("Document_ПоступлениеТоваровУслуг", FakeODataServer.Purchase1, r => r["Комментарий"] = "вне окна"); // 100 days old
        server.Remove("Catalog_Контрагенты", FakeODataServer.CpC);

        var report = await SyncAsync(tenant, connection, ODataSyncMode.Delta);
        Assert.Equal("delta", report.Mode);
        var docs = E(report, "documents");
        Assert.Equal((1, 1, 1), (docs.Listed, docs.Changed, docs.Sent));
        Assert.Equal("исправлено", await f.ValueAsync($"SELECT comment FROM accounting_documents WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.Sale1}'"));
        Assert.Null(await f.ValueAsync($"SELECT comment FROM accounting_documents WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.Purchase1}'"));
        Assert.Equal("False", await f.ValueAsync($"SELECT is_deleted FROM accounting_counterparties WHERE tenant_id = '{tenant}' AND ref = '{FakeODataServer.CpC}'"));
        Assert.Contains(server.Requests, r => r.Resource == "Document_РеализацияТоваровУслуг" && r.Query.Contains("Date ge datetime'", StringComparison.Ordinal));
        Assert.Equal(0, report.Entities.Sum(e => e.MarkedDeleted));
    }

    [Fact]
    public async Task Dry_run_reads_and_compares_but_writes_nothing()
    {
        RequirePostgres();
        var (tenant, connection, _) = await NewAsync("OData dry run");
        var report = await SyncAsync(tenant, connection, ODataSyncMode.Full, dryRun: true);
        Assert.True(report.DryRun);
        Assert.Equal(3, E(report, "counterparties").New);
        Assert.Equal(3, E(report, "counterparties").Sent);
        Assert.Equal(2, E(report, "documents").Sent);
        Assert.Equal(0, await f.CountAsync("ingest_batches", tenant));
        Assert.Equal(0, await f.CountAsync("accounting_counterparties", tenant));
        Assert.Equal(0, await f.CountAsync("sync_runs", tenant));
        Assert.Null(await f.ValueAsync($"SELECT last_successful_sync_at FROM connections WHERE id = '{connection}'"));
    }

    [Fact]
    public async Task A_server_that_ignores_skip_does_not_make_the_load_loop()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData loop", seed: false);
        for (var i = 0; i < 7; i++)
            server.Add("Catalog_Контрагенты", FakeODataServer.Counterparty(Guid.NewGuid().ToString("D"), $"K{i}", "30100000" + i, "ЮридическоеЛицо"));
        server.IgnoreSkip = true;
        var ex = await Assert.ThrowsAsync<ODataProtocolException>(() => SyncAsync(tenant, connection, ODataSyncMode.Full));
        Assert.Contains("repeated", ex.Message);
        Assert.True(server.Requests.Count(r => r.Resource == "Catalog_Контрагенты") <= 3);
        Assert.Equal(0, await f.CountAsync("accounting_counterparties", tenant));
    }

    [Fact]
    public async Task Queue_load_records_a_run_and_an_access_error_opens_the_circuit_without_leaking_the_password()
    {
        RequirePostgres();
        var (tenant, connection, server) = await NewAsync("OData queue");
        var ok = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ConnectionSyncService>().SyncAsync(connection, CancellationToken.None));
        Assert.Equal(SyncRunResult.Succeeded, ok.Result);
        Assert.True(ok.IsComplete);

        var password = server.Password;
        server.Password = "changed-in-1c";
        Later();
        var before = server.Requests.Count;
        var failed = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ConnectionSyncService>().SyncAsync(connection, CancellationToken.None));
        Assert.Equal(SyncRunResult.Failed, failed.Result);
        Assert.Equal("NeedsAttention", await f.ValueAsync($"SELECT status FROM connections WHERE id = '{connection}'"));
        Assert.Contains("access denied", await f.ValueAsync($"SELECT status_reason FROM connections WHERE id = '{connection}'"));
        // Exactly one request with the old password: 401 is not retried and the load stops.
        Assert.Equal(1, server.Requests.Count - before);

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(server.Login + ":" + password));
        foreach (var line in Host.Logs.Lines)
        {
            Assert.DoesNotContain(password, line);
            Assert.DoesNotContain(basic, line);
            Assert.DoesNotContain("test_app/", line); // the application URL is never logged
        }
        var stored = await f.QueryAsync("SELECT coalesce(error, '') FROM sync_runs UNION ALL SELECT coalesce(status_reason, '') FROM connections UNION ALL SELECT settings::text FROM connections", r => r.GetString(0));
        Assert.DoesNotContain(stored, s => s.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_one_1C_connection_can_be_active_and_other_sources_are_not_affected()
    {
        RequirePostgres();
        var server = f.Router.Add(FakeODataServer.CreateUnique());
        var tenant = await Host.CreateTenantAsync("One 1C");
        Task<ODataConnectionResult> CreateOData(string? name = null) => Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ODataConnectionService>()
            .CreateAsync(name, server.BaseUrl, server.Login, new SecretValue(server.Password), null, null, false, CancellationToken.None));

        var extension = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<IngestKeyService>().EnsureConnectionAsync(CancellationToken.None));
        var refused = await Assert.ThrowsAsync<OneCConnectionConflictException>(() => CreateOData());
        Assert.Contains("onec_extension", refused.Message);

        // Didox, banks and file imports: as many as needed, whatever the 1C state.
        await Host.CreateConnectionAsync(tenant, AdapterKind.Edo);
        await Host.CreateConnectionAsync(tenant, AdapterKind.Edo);
        await Host.CreateConnectionAsync(tenant, AdapterKind.Bank);

        // Changing the source: the old one is disabled explicitly first.
        await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ODataConnectionService>().DisableAsync(extension.Id.ToString(), "moved to Clobus", CancellationToken.None));
        var odata = await CreateOData();
        Assert.Equal("clobus", odata.HostLabel);
        await Assert.ThrowsAsync<OneCConnectionConflictException>(() => CreateOData("second"));

        // A fresh organization with OData active gets no extension connection next to it.
        var other = await Host.CreateTenantAsync("One 1C, OData first");
        await Host.InTenantAsync(other, sp => sp.GetRequiredService<ODataConnectionService>()
            .CreateAsync(null, server.BaseUrl, server.Login, new SecretValue(server.Password), null, null, false, CancellationToken.None));
        await Assert.ThrowsAsync<OneCConnectionConflictException>(() =>
            Host.InTenantAsync(other, sp => sp.GetRequiredService<IngestKeyService>().EnsureConnectionAsync(CancellationToken.None)));

        // The database refuses it too, whatever the code path.
        var db = await Assert.ThrowsAsync<DbUpdateException>(() => Host.InTenantAsync(tenant, async sp =>
        {
            var ctx = sp.GetRequiredService<ISynectDbContext>();
            await ctx.InTransactionAsync(async ct =>
            {
                ctx.Connections.Add(new Connection { Name = "raw", SourceCode = Connection.Accounting1CSourceCode, AdapterKind = AdapterKind.Accounting1C, CreatedAt = Host.Time.GetUtcNow() });
                await ctx.SaveChangesAsync(ct);
            }, CancellationToken.None);
            return 0;
        }));
        Assert.Contains("ux_connections_one_active_1c", (db.InnerException as PostgresException)?.ConstraintName);
    }

    [Fact]
    public async Task Push_and_pull_packages_cannot_reach_each_others_connection()
    {
        RequirePostgres();
        var (tenant, odata, _) = await NewAsync("OData push guard", seed: false);
        var tin = "309" + Random.Shared.Next(100000, 999999);
        await f.ExecAdminAsync($"UPDATE tenants SET tin = '{tin}' WHERE id = '{tenant}'");
        var body = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schema_version"] = 1, ["organization_tin"] = tin, ["batch_id"] = "push-1", ["sequence"] = 1, ["exported_at"] = "2026-11-07T10:00:00Z",
            ["mode"] = "delta", ["entity"] = "counterparties", ["items"] = new JsonArray(new JsonObject { ["ref"] = "x1", ["name"] = "X" }),
        }.ToJsonString());
        var push = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<IngestService>()
            .ProcessAsync(new IngestKeyIdentity(Guid.NewGuid(), tenant, odata, tin), body, CancellationToken.None));
        Assert.Equal(403, push.HttpStatus);

        var other = await Host.CreateTenantAsync("Extension pull guard");
        var extension = await Host.InTenantAsync(other, sp => sp.GetRequiredService<IngestKeyService>().EnsureConnectionAsync(CancellationToken.None));
        var pulled = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schema_version"] = 1, ["organization_tin"] = IngestService.PulledOrganizationTin, ["batch_id"] = "pull-1", ["sequence"] = 1,
            ["exported_at"] = "2026-11-07T10:00:00Z", ["mode"] = "delta", ["entity"] = "counterparties", ["items"] = new JsonArray(),
        }.ToJsonString());
        var pull = await Host.InTenantAsync(other, sp => sp.GetRequiredService<IngestService>().ProcessPulledAsync(extension.Id, pulled, null, CancellationToken.None));
        Assert.Equal(403, pull.HttpStatus);
        Assert.Equal(0, await f.CountAsync("accounting_counterparties", tenant));
    }

    [Fact]
    public async Task Scheduler_plans_pull_connections_only_and_the_overview_shows_kind_transport_and_secrets_state()
    {
        RequirePostgres();
        var (tenant, odata, _) = await NewAsync("OData plan", seed: true);
        var pushTenant = await Host.CreateTenantAsync("Extension plan");
        var key = await Host.InTenantAsync(pushTenant, sp => sp.GetRequiredService<IngestKeyService>().CreateAsync("plan key", CancellationToken.None));

        Assert.Equal(1, await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<SyncPlanner>().PlanAsync(CancellationToken.None)));
        Assert.Equal(0, await Host.InTenantAsync(pushTenant, sp => sp.GetRequiredService<SyncPlanner>().PlanAsync(CancellationToken.None)));
        Assert.Equal("Initial", await f.ValueAsync($"SELECT kind FROM sync_jobs WHERE connection_id = '{odata}'"));
        await f.ExecAdminAsync($"DELETE FROM sync_jobs WHERE connection_id = '{odata}'");

        await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ODataSyncService>().SyncAsync(odata, CancellationToken.None));
        var pull = (await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<PortfolioQueries>().SummaryAsync(false, CancellationToken.None))).Data!
            .Connections.Single(c => c.ConnectionId == odata);
        Assert.Equal(("onec_fresh_odata", "pull", "clobus", true), (pull.ConnectionKind, pull.Transport, pull.HostLabel, pull.CredentialsSet));
        Assert.NotNull(pull.LastAttemptAt);
        Assert.NotNull(pull.LastSuccessfulSyncAt);
        Assert.Null(pull.IngestKeyPrefix);

        var push = (await Host.InTenantAsync(pushTenant, sp => sp.GetRequiredService<PortfolioQueries>().SummaryAsync(false, CancellationToken.None))).Data!
            .Connections.Single();
        Assert.Equal(("onec_extension", "push", "desktop", false), (push.ConnectionKind, push.Transport, push.HostLabel, push.CredentialsSet));
        Assert.Equal(key.KeyPrefix, push.IngestKeyPrefix);
        Assert.Null(push.LastAttemptAt); // no package received yet
    }
}
