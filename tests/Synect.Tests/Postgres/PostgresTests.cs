using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.Postgres;

/// <summary>Migrations, row-level security and DB-level invariants on a real PostgreSQL.</summary>
public class PostgresTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private TestHost Host => fixture.Host;

    private static readonly string[] TenantTables =
    [
        "connections", "sync_runs", "raw_objects", "bank_accounts", "bank_transactions",
        "bank_daily_balances", "edo_documents", "edo_document_status_logs", "counterparties",
        "sync_jobs", "connection_secrets", "analytics_counterparty_monthly", "tool_audit_log",
        "counterparty_aliases", "reconciliation_runs", "reconciliation_matches", "reconciliation_match_items",
        "reconciliation_findings", "reconciliation_match_decisions", "attention_items", "attention_item_events",
        "statement_imports",
    ];

    private void RequirePostgres() => Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);

    private async Task<(Guid Tenant, Guid Bank, Guid Edo)> SeedSyncedTenantAsync(string name)
    {
        var tenant = await Host.CreateTenantAsync(name);
        var bank = await Host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var edo = await Host.CreateConnectionAsync(tenant, AdapterKind.Edo);
        Assert.Equal(SyncRunResult.Succeeded, (await Host.SyncBankAsync(tenant, bank)).Result);
        Assert.Equal(SyncRunResult.Succeeded, (await Host.SyncEdoAsync(tenant, edo)).Result);
        return (tenant, bank, edo);
    }

    [Fact]
    public async Task Migrations_create_all_tables_with_forced_row_level_security()
    {
        RequirePostgres();

        foreach (var table in TenantTables)
        {
            var flags = await fixture.ScalarAsync<string>(
                $"SELECT relrowsecurity::text || ',' || relforcerowsecurity::text FROM pg_class WHERE relname = '{table}'");
            Assert.Equal("true,true", flags);
        }
        // No table with a tenant_id column is left out of the list above (api_keys is checked separately).
        var unlisted = await fixture.ScalarAsync<string>(
            "SELECT coalesce(string_agg(table_name, ',' ORDER BY table_name), '') FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND column_name = 'tenant_id' AND table_name <> 'api_keys' " +
            "AND table_name NOT IN ('account_tenants', 'api_key_tenants') AND table_name <> ALL (ARRAY['" +
            string.Join("','", TenantTables) + "'])");
        Assert.Equal("", unlisted);
        // api_keys: RLS enabled but not forced, so that only the owner-defined lookup function bypasses it.
        Assert.Equal("true,false", await fixture.ScalarAsync<string>(
            "SELECT relrowsecurity::text || ',' || relforcerowsecurity::text FROM pg_class WHERE relname = 'api_keys'"));
        // Account tables (stage 6.5): no organization data; RLS enabled WITHOUT any policy and not forced, so that no role
        // but the owner (administration, SECURITY DEFINER functions) sees a row.
        foreach (var table in new[] { "accounts", "account_tenants", "api_key_tenants", "account_tool_audit_log" })
        {
            Assert.Equal("true,false", await fixture.ScalarAsync<string>(
                $"SELECT relrowsecurity::text || ',' || relforcerowsecurity::text FROM pg_class WHERE relname = '{table}'"));
            Assert.Equal(0L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM pg_policies WHERE tablename = '{table}'"));
        }
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM pg_class WHERE relname = 'tenants'"));
        var nonTenantFirst = await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM pg_index i
            JOIN pg_class c ON c.oid = i.indrelid
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = i.indkey[0]
            WHERE c.relname <> 'tenants' AND c.relname NOT LIKE '\_\_%' AND c.relnamespace = 'public'::regnamespace
              AND a.attname <> 'tenant_id'
              -- Deliberate exception: API keys are looked up by hash before the tenant is known.
              AND NOT (c.relname = 'api_keys' AND a.attname = 'key_hash')
              -- Stage 6.5: account tables hold no tenant data; account keys have no tenant (primary key id, account_id);
              -- tool_audit_log keeps the foreign key indexes of api_key_id and account_id.
              AND c.relname NOT IN ('accounts', 'account_tenants', 'api_key_tenants', 'account_tool_audit_log')
              AND NOT (c.relname = 'api_keys' AND a.attname IN ('id', 'account_id'))
              AND NOT (c.relname = 'tool_audit_log' AND a.attname IN ('api_key_id', 'account_id'))
            """);
        Assert.Equal(0L, nonTenantFirst);
    }

    [Fact]
    public async Task Repeated_sync_is_idempotent_under_rls()
    {
        RequirePostgres();
        var (tenant, bank, _) = await SeedSyncedTenantAsync("PG idempotency");
        var before = await Host.CountAsync(tenant, db => db.BankTransactions);
        var rawBefore = await Host.CountAsync(tenant, db => db.RawObjects);

        Host.Time.Advance(TimeSpan.FromMinutes(5));
        await Host.SyncBankAsync(tenant, bank);

        Assert.True(before > 0);
        Assert.Equal(before, await Host.CountAsync(tenant, db => db.BankTransactions));
        Assert.Equal(rawBefore, await Host.CountAsync(tenant, db => db.RawObjects));
    }

    [Fact]
    public async Task Rls_isolates_tenants_even_when_ef_filters_are_bypassed()
    {
        RequirePostgres();
        var (a, _, _) = await SeedSyncedTenantAsync("PG tenant A");
        var (b, _, _) = await SeedSyncedTenantAsync("PG tenant B");

        foreach (var tenant in new[] { a, b })
        {
            var visible = await Host.QueryAsync(tenant, db => db.BankTransactions.IgnoreQueryFilters()
                .Select(t => t.TenantId).Distinct().ToListAsync());
            Assert.Equal([tenant], visible);

            var docs = await Host.QueryAsync(tenant, db => db.EdoDocuments.IgnoreQueryFilters()
                .Select(t => t.TenantId).Distinct().ToListAsync());
            Assert.Equal([tenant], docs);
        }

        // No tenant set in the transaction: nothing is visible at all.
        Assert.Equal(0, await Host.QueryAsync(null, db => db.BankTransactions.IgnoreQueryFilters().CountAsync()));
    }

    [Fact]
    public async Task Rls_rejects_writing_rows_of_another_tenant()
    {
        RequirePostgres();
        var a = await Host.CreateTenantAsync("PG writer A");
        var b = await Host.CreateTenantAsync("PG writer B");

        await using var scope = Host.Scope(a);
        var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.InTransactionAsync(ct =>
            db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO counterparties (id, tenant_id, inn, created_at, updated_at) VALUES ({Guid.NewGuid()}, {b}, '399999999', now(), now())", ct),
            CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Status_log_is_append_only_in_the_database()
    {
        RequirePostgres();
        var (tenant, _, _) = await SeedSyncedTenantAsync("PG append-only");

        await using var scope = Host.Scope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.InTransactionAsync(ct =>
            db.Database.ExecuteSqlRawAsync("UPDATE edo_document_status_logs SET status = 'x'", ct), CancellationToken.None));
        Assert.Contains("append-only", ex.MessageText);
    }

    [Fact]
    public async Task Queue_runs_a_requested_load_end_to_end_under_rls()
    {
        RequirePostgres();
        var tenant = await Host.CreateTenantAsync("PG queue");
        var bank = await Host.CreateConnectionAsync(tenant, AdapterKind.Bank);

        Assert.Equal(SyncRequestStatus.Queued, (await Host.RequestSyncAsync(tenant, bank)).Status);
        Assert.Equal(SyncRequestStatus.AlreadyQueued, (await Host.RequestSyncAsync(tenant, bank)).Status);
        await Host.RunQueueAsync();

        var job = (await Host.JobsAsync(tenant, bank)).Single(j => j.Kind == SyncJobKind.Manual);
        Assert.Equal(SyncJobStatus.Succeeded, job.Status);
        Assert.NotNull(job.SyncRunId);
        Assert.True(await Host.CountAsync(tenant, db => db.BankTransactions) > 0);
    }

    [Fact]
    public async Task Partial_unique_index_allows_one_active_job_per_connection()
    {
        RequirePostgres();
        var tenant = await Host.CreateTenantAsync("PG active job");
        var bank = await Host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        await Host.RequestSyncAsync(tenant, bank);

        await using var scope = Host.Scope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
        await Assert.ThrowsAsync<DbUpdateException>(() => db.InTransactionAsync(async ct =>
        {
            db.SyncJobs.Add(SyncJob.Create(tenant, bank, SyncJobKind.Scheduled, Host.Time.Now));
            await db.SaveChangesAsync(ct);
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Connection_secrets_are_isolated_by_rls_and_hold_no_plain_value()
    {
        RequirePostgres();
        var a = await Host.CreateTenantAsync("PG secrets A");
        var b = await Host.CreateTenantAsync("PG secrets B");
        var bankA = await Host.CreateConnectionAsync(a, AdapterKind.Bank);
        await Host.CreateConnectionAsync(b, AdapterKind.Bank);

        var visibleToB = await Host.QueryAsync(b, db => db.ConnectionSecrets.IgnoreQueryFilters()
            .Select(s => s.TenantId).Distinct().ToListAsync());
        Assert.Equal([b], visibleToB);
        Assert.Equal(0, await Host.QueryAsync(null, db => db.ConnectionSecrets.IgnoreQueryFilters().CountAsync()));

        var password = Host.Passwords[bankA];
        var plainRows = await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM connection_secrets WHERE encrypted_value LIKE '%{password}%'");
        Assert.Equal(0L, plainRows);
        Assert.Equal(1L, await fixture.ScalarAsync<long>($"SELECT count(*) FROM connection_secrets WHERE connection_id = '{bankA}'"));
    }
}
