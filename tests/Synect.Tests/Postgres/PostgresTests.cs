using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synect.Application.Abstractions;
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
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT count(*) FROM pg_class WHERE relname = 'tenants'"));
        var nonTenantFirst = await fixture.ScalarAsync<long>("""
            SELECT count(*) FROM pg_index i
            JOIN pg_class c ON c.oid = i.indrelid
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = i.indkey[0]
            WHERE c.relname <> 'tenants' AND c.relname NOT LIKE '\_\_%' AND c.relnamespace = 'public'::regnamespace
              AND a.attname <> 'tenant_id'
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
}
