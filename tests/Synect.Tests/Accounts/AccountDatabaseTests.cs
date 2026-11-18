using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synect.Application.Abstractions;
using Synect.Application.Access;
using Synect.Application.Accounts;
using Synect.Domain.Access;
using Synect.Domain.Accounts;
using Synect.Domain.Sync;
using Synect.Infrastructure.Access;
using Synect.Infrastructure.Accounts;
using Synect.Infrastructure.Persistence;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Accounts;

/// <summary>
/// Accounts in PostgreSQL (stage 6.5): the application role cannot read the account tables, the SECURITY DEFINER
/// functions return only what a presented key may see, multi-organization work never mixes tenants, and the
/// administration (import, delete-tenant) behaves.
/// </summary>
public class AccountDatabaseTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static int _tin = 500_000_000;

    private static string NextTin() => Interlocked.Increment(ref _tin).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private AccountAdmin Admin() => AccountAdmin.Create(fixture.AdminDbConnectionString, fixture.Host.Time);

    private void Require() => Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);

    private async Task<(Account Account, List<AccountOrganization> Orgs)> AccountAsync(string name, params string[] orgNames)
    {
        var admin = Admin();
        await using var _ = admin.Db;
        var account = await admin.CreateAccountAsync(name + " " + Guid.NewGuid().ToString("N")[..6]);
        var orgs = new List<AccountOrganization>();
        foreach (var org in orgNames)
            orgs.Add((await admin.AddOrganizationAsync(account.Id, org, NextTin(), createTenant: true)).Organization);
        return (account, orgs);
    }

    private async Task<T> WithAdminAsync<T>(Func<AccountAdmin, Task<T>> work)
    {
        var admin = Admin();
        await using var _ = admin.Db;
        return await work(admin);
    }

    private async Task<IApiKeyResolver> ResolverAsync(AsyncServiceScope scope) =>
        await Task.FromResult<IApiKeyResolver>(new PostgresApiKeyResolver(scope.ServiceProvider.GetRequiredService<SynectDbContext>()));

    private async Task<long> AppCountAsync(string sql, Guid? tenant = null)
    {
        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, tx))
        {
            set.Parameters.AddWithValue("t", tenant?.ToString() ?? "");
            await set.ExecuteNonQueryAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, tx);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Application_role_sees_and_changes_nothing_in_the_account_tables()
    {
        Require();
        var (account, orgs) = await AccountAsync("RLS", "RLS org 1", "RLS org 2");
        var key = await WithAdminAsync(a => a.CreateAccountKeyAsync(account.Id, [ApiKey.ScopeRead], "rls key", [orgs[0].Tin!]));
        await WithAdminAsync(async a =>
        {
            await a.Db.Database.ExecuteSqlRawAsync(
                "SELECT synect_write_account_tool_audit(@p0, 'list_organizations', '{{}}'::jsonb, now(), 1, 0, 'Ok', NULL, NULL, NULL)",
                ApiKeyFormat.Hash(key.Key.Key));
            return true;
        });

        // The rows exist (the owner sees them) ...
        Assert.True(await fixture.ScalarAsync<long>("SELECT count(*) FROM accounts") >= 1);
        Assert.True(await fixture.ScalarAsync<long>("SELECT count(*) FROM account_tool_audit_log") >= 1);

        // ... but the application role, with no tenant context or in the context of one of the account's organizations,
        // reads nothing directly (row-level security without a policy; the test role even has table grants).
        foreach (Guid? context in new Guid?[] { null, orgs[0].TenantId })
        {
            foreach (var table in new[] { "accounts", "account_tenants", "api_key_tenants", "account_tool_audit_log" })
                Assert.Equal(0, await AppCountAsync($"SELECT count(*) FROM {table}", context));
            // Account keys have no tenant: invisible even in an organization's context.
            Assert.Equal(0, await AppCountAsync($"SELECT count(*) FROM api_keys WHERE account_id IS NOT NULL", context));
        }

        await using var connection = new NpgsqlConnection(fixture.AppConnectionString);
        await connection.OpenAsync();
        foreach (var sql in new[]
                 {
                     "INSERT INTO accounts (id, name, status, created_at) VALUES (gen_random_uuid(), 'hijack', 'Active', now())",
                     $"INSERT INTO account_tenants (account_id, tenant_id, added_at) VALUES ('{account.Id}', '{orgs[1].TenantId}', now())",
                     $"INSERT INTO api_key_tenants (key_id, tenant_id) VALUES ('{key.Key.Id}', '{orgs[1].TenantId}')",
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("42501", ex.SqlState); // row-level security violation
        }
        await using (var update = new NpgsqlCommand($"UPDATE account_tenants SET disabled_at = now() WHERE account_id = '{account.Id}'", connection))
            Assert.Equal(0, await update.ExecuteNonQueryAsync());
        await using (var delete = new NpgsqlCommand($"DELETE FROM api_key_tenants WHERE key_id = '{key.Key.Id}'", connection))
            Assert.Equal(0, await delete.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Key_functions_return_only_what_the_key_may_see()
    {
        Require();
        var (a, aOrgs) = await AccountAsync("Account A", "A one", "A two", "A three");
        var (b, bOrgs) = await AccountAsync("Account B", "B one");
        var keyA = await WithAdminAsync(x => x.CreateAccountKeyAsync(a.Id, [ApiKey.ScopeRead, ApiKey.ScopeReview], "A all"));
        var keyA2 = await WithAdminAsync(x => x.CreateAccountKeyAsync(a.Id, [ApiKey.ScopeRead], "A limited", [aOrgs[1].Tin!]));
        var keyB = await WithAdminAsync(x => x.CreateAccountKeyAsync(b.Id, [ApiKey.ScopeRead], "B all"));
        var tenantKey = await fixture.Host.InTenantAsync(aOrgs[2].TenantId,
            sp => sp.GetRequiredService<ApiKeyService>().CreateAsync("A three own", [ApiKey.ScopeRead], CancellationToken.None));

        await using var scope = fixture.Host.Scope(null);
        var resolver = await ResolverAsync(scope);
        async Task<List<Guid>> Orgs(CreatedApiKey k) =>
            (await resolver.OrganizationsAsync(ApiKeyFormat.Hash(k.Key), CancellationToken.None)).Select(o => o.TenantId).ToList();

        var identity = await resolver.ResolveAsync(ApiKeyFormat.Hash(keyA.Key.Key), CancellationToken.None);
        Assert.NotNull(identity);
        Assert.Equal((keyA.Key.Id, a.Id, (Guid?)null), (identity!.ApiKeyId, identity.AccountId!.Value, identity.TenantId));
        Assert.Equal(["read", "review"], identity.Scopes);

        Assert.Equal(aOrgs.Select(o => o.TenantId).Order(), (await Orgs(keyA.Key)).Order());
        Assert.Equal([aOrgs[1].TenantId], await Orgs(keyA2.Key));
        Assert.Equal([bOrgs[0].TenantId], await Orgs(keyB.Key));
        Assert.DoesNotContain(bOrgs[0].TenantId, await Orgs(keyA.Key));
        var named = await resolver.OrganizationsAsync(ApiKeyFormat.Hash(keyA2.Key.Key), CancellationToken.None);
        Assert.Equal(("A two", aOrgs[1].Tin), (named[0].Name, named[0].Tin));

        var own = await resolver.ResolveAsync(ApiKeyFormat.Hash(tenantKey.Key), CancellationToken.None);
        Assert.Equal((aOrgs[2].TenantId, (Guid?)null), (own!.TenantId!.Value, own.AccountId));
        Assert.Equal([aOrgs[2].TenantId], await Orgs(tenantKey));

        // A disabled organization disappears from account keys, and its own key stops working.
        await WithAdminAsync(x => x.DisableOrganizationAsync(a.Id, aOrgs[2].Tin!));
        Assert.DoesNotContain(aOrgs[2].TenantId, await Orgs(keyA.Key));
        Assert.Null(await resolver.ResolveAsync(ApiKeyFormat.Hash(tenantKey.Key), CancellationToken.None));
        Assert.Empty(await Orgs(tenantKey));

        // Restricted to a disabled organization: the key works but sees nothing.
        await WithAdminAsync(x => x.DisableOrganizationAsync(a.Id, aOrgs[1].Tin!));
        Assert.Empty(await Orgs(keyA2.Key));
        await WithAdminAsync(async x => { await x.EnableOrganizationAsync(a.Id, aOrgs[1].Tin!); return true; });

        // Revoked key and suspended account.
        Assert.True(await WithAdminAsync(x => x.RevokeAccountKeyAsync(a.Id, keyA2.Key.KeyPrefix)));
        Assert.Null(await resolver.ResolveAsync(ApiKeyFormat.Hash(keyA2.Key.Key), CancellationToken.None));
        Assert.Empty(await Orgs(keyA2.Key));
        await WithAdminAsync(async x => { await x.SetAccountStatusAsync(b.Id, AccountStatus.Suspended); return true; });
        Assert.Null(await resolver.ResolveAsync(ApiKeyFormat.Hash(keyB.Key.Key), CancellationToken.None));
        Assert.Empty(await Orgs(keyB.Key));

        // Garbage never matches.
        Assert.Empty(await resolver.OrganizationsAsync(new byte[32], CancellationToken.None));
        Assert.Empty(await resolver.OrganizationsAsync([1, 2, 3], CancellationToken.None));
    }

    [Fact]
    public async Task Tin_is_unique_within_an_account_only()
    {
        Require();
        var (a, aOrgs) = await AccountAsync("TIN A", "TIN org");
        var (b, _) = await AccountAsync("TIN B");
        var tin = aOrgs[0].Tin!;

        // Another account may serve an organization with the same TIN (a different tenant).
        var (other, created) = await WithAdminAsync(x => x.AddOrganizationAsync(b.Id, "TIN org copy", tin, createTenant: true));
        Assert.True(created);
        Assert.NotEqual(aOrgs[0].TenantId, other.TenantId);

        // The same account may not: the trigger refuses a second organization with that TIN (here: set on a tenant
        // already in the account).
        var (second, _) = await WithAdminAsync(x => x.AddOrganizationAsync(a.Id, "Another name", null, createTenant: true));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecAsync(fixture.AdminDbConnectionString,
            $"UPDATE tenants SET tin = '{tin}' WHERE id = '{second.TenantId}'"));
        Assert.Equal("23505", ex.SqlState);
        Assert.Contains("already used", ex.MessageText);

        // Adding the same TIN again is idempotent (the existing organization is returned).
        var again = await WithAdminAsync(x => x.AddOrganizationAsync(a.Id, "TIN org", tin, createTenant: true));
        Assert.False(again.Created);
        Assert.Equal(aOrgs[0].TenantId, again.Organization.TenantId);
    }

    [Fact]
    public async Task Import_of_organizations_is_idempotent_and_reports_errors_per_row()
    {
        Require();
        var (account, _) = await AccountAsync("Import");
        var t1 = NextTin();
        var t2 = NextTin();
        var path = Path.Combine(Path.GetTempPath(), $"synect-import-{Guid.NewGuid():N}.csv");
        await File.WriteAllLinesAsync(path,
        [
            "name;tin",
            $"\"ООО \"\"Ўзбек Савдо\"\"\";{t1}",
            $"Hamkor Invest MChJ;{t2}",
            "No TIN company;",
            "Bad TIN;12345",
            ";555555555",
            $"Duplicate in file;{t1}",
        ]);
        try
        {
            var first = await WithAdminAsync(x => x.ImportOrganizationsAsync(account.Id, path));
            Assert.Equal([ImportRowOutcome.Created, ImportRowOutcome.Created, ImportRowOutcome.Created, ImportRowOutcome.Error,
                ImportRowOutcome.Error, ImportRowOutcome.Error], first.Select(r => r.Outcome).ToList());
            Assert.Equal("ООО \"Ўзбек Савдо\"", first[0].Name);
            Assert.Contains("9 or 14 digits", first[3].Message);
            Assert.Contains("empty", first[4].Message);
            Assert.Contains("Repeats", first[5].Message);

            var second = await WithAdminAsync(x => x.ImportOrganizationsAsync(account.Id, path));
            Assert.Equal(3, second.Count(r => r.Outcome == ImportRowOutcome.AlreadyPresent));
            Assert.Equal(0, second.Count(r => r.Outcome == ImportRowOutcome.Created));
            Assert.Equal(3, (await WithAdminAsync(x => x.OrganizationsAsync(account.Id))).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Delete_tenant_removes_everything_of_that_tenant_only()
    {
        Require();
        var (account, orgs) = await AccountAsync("Delete", "Delete me " + Guid.NewGuid().ToString("N")[..6], "Keep me");
        var victim = orgs[0];
        var keep = orgs[1];
        foreach (var tenant in new[] { victim.TenantId, keep.TenantId })
        {
            var bank = await fixture.Host.CreateConnectionAsync(tenant, AdapterKind.Bank);
            await fixture.Host.SyncBankAsync(tenant, bank);
            // Didox documents leave rows in the append-only status history, which the deletion must still remove.
            var edo = await fixture.Host.CreateConnectionAsync(tenant, AdapterKind.Edo);
            await fixture.Host.SyncEdoAsync(tenant, edo);
        }
        Assert.True(await fixture.ScalarAsync<long>($"SELECT count(*) FROM edo_document_status_logs WHERE tenant_id = '{victim.TenantId}'") > 0);
        var keepLogs = await fixture.ScalarAsync<long>($"SELECT count(*) FROM edo_document_status_logs WHERE tenant_id = '{keep.TenantId}'");
        var key = await fixture.Host.InTenantAsync(victim.TenantId,
            sp => sp.GetRequiredService<ApiKeyService>().CreateAsync("victim key", [ApiKey.ScopeRead], CancellationToken.None));
        var keepRows = await fixture.ScalarAsync<long>($"SELECT count(*) FROM raw_objects WHERE tenant_id = '{keep.TenantId}'");
        Assert.True(await fixture.ScalarAsync<long>($"SELECT count(*) FROM raw_objects WHERE tenant_id = '{victim.TenantId}'") > 0);

        // Refused while the tenant has an active key.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => WithAdminAsync(x => x.DeleteTenantAsync(victim.Name, force: false)));
        Assert.Contains("active API keys", refused.Message);
        Assert.Equal(1, await fixture.ScalarAsync<long>($"SELECT count(*) FROM tenants WHERE id = '{victim.TenantId}'"));

        var deleted = await WithAdminAsync(x => x.DeleteTenantAsync(victim.Name, force: true));
        Assert.Equal(victim.TenantId, deleted.TenantId);
        Assert.Contains(deleted.Rows, r => r.Table == "bank_transactions" && r.Rows > 0);
        Assert.Contains(deleted.Rows, r => r.Table == "api_keys" && r.Rows == 1);
        Assert.Contains(deleted.Rows, r => r.Table == "account_tenants" && r.Rows == 1);
        Assert.Contains(deleted.Rows, r => r.Table == "edo_document_status_logs" && r.Rows > 0);
        // The history stays append-only for everything else: the trigger is enabled again after the deletion.
        Assert.Equal("O", await fixture.ScalarAsync<string>(
            "SELECT tgenabled::text FROM pg_trigger WHERE tgname = 'trg_edo_document_status_logs_append_only'"));
        var stillAppendOnly = await Assert.ThrowsAsync<PostgresException>(() => Postgres.PostgresFixture.ExecAsync(
            fixture.AdminDbConnectionString, $"DELETE FROM edo_document_status_logs WHERE tenant_id = '{keep.TenantId}'"));
        Assert.Contains("append-only", stillAppendOnly.MessageText);
        Assert.Equal(keepLogs, await fixture.ScalarAsync<long>($"SELECT count(*) FROM edo_document_status_logs WHERE tenant_id = '{keep.TenantId}'"));

        // Nothing of the tenant is left in any table with a tenant_id ...
        var leftovers = await fixture.ScalarAsync<string>("""
            SELECT coalesce(string_agg(c.relname, ','), '') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id'
            WHERE n.nspname = 'public' AND c.relkind = 'r'
            """);
        foreach (var table in leftovers.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            await using var connection = new NpgsqlConnection(fixture.AdminDbConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\" WHERE tenant_id = '{victim.TenantId}'", connection);
            Assert.True((long)(await command.ExecuteScalarAsync())! == 0, table);
        }
        Assert.Equal(0, await fixture.ScalarAsync<long>($"SELECT count(*) FROM tenants WHERE id = '{victim.TenantId}'"));
        // ... and the other organization is untouched.
        Assert.Equal(keepRows, await fixture.ScalarAsync<long>($"SELECT count(*) FROM raw_objects WHERE tenant_id = '{keep.TenantId}'"));
        await using var scope = fixture.Host.Scope(null);
        Assert.Null(await (await ResolverAsync(scope)).ResolveAsync(ApiKeyFormat.Hash(key.Key), CancellationToken.None));
    }

    [Fact]
    public async Task Multi_organization_work_never_mixes_tenants_even_when_one_fails()
    {
        Require();
        var (_, orgs) = await AccountAsync("FanOut", "F1", "F2", "F3", "F4", "F5");
        foreach (var org in orgs)
        {
            var bank = await fixture.Host.CreateConnectionAsync(org.TenantId, AdapterKind.Bank);
            await fixture.Host.SyncBankAsync(org.TenantId, bank);
        }
        var failing = orgs[2].TenantId;
        var fanOut = fixture.Host.Services.GetRequiredService<OrganizationFanOut>();
        var tenants = orgs.Select(o => o.TenantId).ToList();

        // Each work item reads WITHOUT a tenant filter of its own: whatever it sees comes from its scope's context only.
        var results = await fanOut.RunAsync(tenants, async (sp, tenant, ct) =>
        {
            var db = sp.GetRequiredService<SynectDbContext>();
            return await db.InTransactionAsync(async t =>
            {
                var setting = await db.Database.SqlQuery<string>($"SELECT current_setting('app.tenant_id', true) AS \"Value\"").SingleAsync(t);
                var seen = await db.Database.SqlQuery<Guid>($"SELECT DISTINCT tenant_id AS \"Value\" FROM bank_transactions").ToListAsync(t);
                var count = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM raw_objects").SingleAsync(t);
                if (tenant == failing) throw new InvalidOperationException("boom in the middle");
                return (Setting: setting, Seen: seen, Count: count);
            }, ct);
        }, CancellationToken.None);

        Assert.Equal(tenants, results.Select(r => r.TenantId).ToList());
        foreach (var r in results)
        {
            if (r.TenantId == failing)
            {
                Assert.False(r.Succeeded);
                Assert.Equal("boom in the middle", r.Error!.Message);
                Assert.Equal(default, r.Value);
                continue;
            }
            Assert.True(r.Succeeded, r.Error?.ToString());
            Assert.Equal(r.TenantId.ToString(), r.Value.Setting);
            Assert.Equal([r.TenantId], r.Value.Seen);
            var own = await fixture.ScalarAsync<long>($"SELECT count(*) FROM raw_objects WHERE tenant_id = '{r.TenantId}'");
            Assert.Equal(own, r.Value.Count);
        }

        // After the failure the pooled connections carry no tenant: a new scope without a tenant sees nothing.
        Assert.Equal(0, await fixture.Host.CountAsync(null, db => db.BankTransactions));
        Assert.Equal(0, await AppCountAsync("SELECT count(*) FROM bank_transactions"));

        // A tenant may not appear twice in one run.
        await Assert.ThrowsAsync<ArgumentException>(() => fanOut.RunAsync([tenants[0], tenants[0]], (_, _, _) => Task.FromResult(1), CancellationToken.None));
    }

    [Fact]
    public async Task Account_audit_function_takes_the_account_from_the_key_and_refuses_foreign_organizations()
    {
        Require();
        var (a, aOrgs) = await AccountAsync("Audit A", "Audit org");
        var (_, bOrgs) = await AccountAsync("Audit B", "Audit B org");
        var key = await WithAdminAsync(x => x.CreateAccountKeyAsync(a.Id, [ApiKey.ScopeRead], "audit"));
        var writer = new PostgresAccountToolAuditWriter(fixture.Host.Services.GetRequiredService<IServiceScopeFactory>(),
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresAccountToolAuditWriter>());
        // PostgresAccountToolAuditWriter resolves SynectDbContext from the scope: the test host runs it as the application role.
        await writer.WriteAsync(ApiKeyFormat.Hash(key.Key.Key), new AccountToolAuditEntry("list_organizations", "{\"organization\":\"all\"}",
            fixture.Host.Time.GetUtcNow(), TimeSpan.FromMilliseconds(12), 1, ToolCallStatus.Ok, null, null, [aOrgs[0].TenantId]), CancellationToken.None);
        Assert.Equal(1, await fixture.ScalarAsync<long>(
            $"SELECT count(*) FROM account_tool_audit_log WHERE account_id = '{a.Id}' AND api_key_id = '{key.Key.Id}' AND tenant_ids = ARRAY['{aOrgs[0].TenantId}']::uuid[]"));

        // An organization of another account in the list is refused (the writer logs and swallows the error).
        await writer.WriteAsync(ApiKeyFormat.Hash(key.Key.Key), new AccountToolAuditEntry("list_organizations", "{}",
            fixture.Host.Time.GetUtcNow(), TimeSpan.Zero, 0, ToolCallStatus.Ok, null, null, [bOrgs[0].TenantId]), CancellationToken.None);
        Assert.Equal(1, await fixture.ScalarAsync<long>($"SELECT count(*) FROM account_tool_audit_log WHERE account_id = '{a.Id}'"));
    }
}
