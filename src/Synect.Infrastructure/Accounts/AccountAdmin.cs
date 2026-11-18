using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Synect.Application.Access;
using Synect.Domain.Access;
using Synect.Domain.Accounts;
using Synect.Domain.Tenants;
using Synect.Infrastructure.Persistence;

namespace Synect.Infrastructure.Accounts;

public sealed record AccountRow(Guid Id, string Name, AccountStatus Status, DateTimeOffset CreatedAt, int Organizations, int DisabledOrganizations, int ActiveKeys);

public sealed record AccountOrganization(Guid TenantId, string Name, string? DisplayName, string? Tin, DateTimeOffset AddedAt, DateTimeOffset? DisabledAt);

public sealed record CreatedAccountKey(CreatedApiKey Key, Guid AccountId, IReadOnlyList<AccountOrganization> Restriction);

public enum ImportRowOutcome
{
    Created,
    AlreadyPresent,
    Error,
}

public sealed record ImportRowResult(int Line, string? Name, string? Tin, ImportRowOutcome Outcome, string? Message);

public sealed record DeletedTenant(Guid TenantId, string Name, IReadOnlyList<(string Table, int Rows)> Rows)
{
    public int Total => Rows.Sum(r => r.Rows);
}

/// <summary>
/// Administration of accounts and their organizations (stage 6.5). Runs as the schema OWNER (DevTools, tests): the account
/// tables are not readable by the application role. Nothing here touches organization data except
/// <see cref="DeleteTenantAsync"/>, which deletes one tenant with everything it owns in one transaction.
/// </summary>
public sealed class AccountAdmin(SynectDbContext owner, string ownerConnectionString, TimeProvider time)
{
    public static AccountAdmin Create(string ownerConnectionString, TimeProvider? time = null)
    {
        var options = new DbContextOptionsBuilder<SynectDbContext>()
            .UseSynectNpgsql(ownerConnectionString)
            .AddInterceptors(new TenantSessionInterceptor())
            .Options;
        return new AccountAdmin(new SynectDbContext(options, new Application.Abstractions.TenantContext()), ownerConnectionString, time ?? TimeProvider.System);
    }

    public SynectDbContext Db => owner;

    public async Task<Account> CreateAccountAsync(string name, CancellationToken ct = default)
    {
        name = Required(name, "Account name");
        if (await owner.Accounts.AnyAsync(a => a.Name == name, ct)) throw new InvalidOperationException($"Account '{name}' already exists.");
        var account = new Account { Name = name, CreatedAt = time.GetUtcNow() };
        owner.Accounts.Add(account);
        await owner.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Account> FindAccountAsync(string nameOrId, CancellationToken ct = default)
    {
        var key = nameOrId.Trim();
        var account = Guid.TryParse(key, out var id)
            ? await owner.Accounts.SingleOrDefaultAsync(a => a.Id == id, ct)
            : await owner.Accounts.SingleOrDefaultAsync(a => a.Name == key, ct);
        return account ?? throw new InvalidOperationException($"Account '{key}' not found.");
    }

    public async Task SetAccountStatusAsync(Guid accountId, AccountStatus status, CancellationToken ct = default)
    {
        var account = await owner.Accounts.SingleAsync(a => a.Id == accountId, ct);
        account.Status = status;
        await owner.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds an organization to the account. With <paramref name="createTenant"/> a new tenant is created (name, TIN);
    /// otherwise an existing tenant is used (by TIN if given, else by exact name). Idempotent: an organization already in
    /// this account is returned as is. A tenant of another account is refused (a tenant belongs to at most one account).
    /// </summary>
    public async Task<(AccountOrganization Organization, bool Created)> AddOrganizationAsync(
        Guid accountId, string name, string? tin, bool createTenant, string? displayName = null, CancellationToken ct = default)
    {
        name = Required(name, "Organization name");
        tin = Tin.Normalize(tin);
        await using var tx = await owner.Database.BeginTransactionAsync(ct);

        var mine = await OrganizationsAsync(accountId, ct);
        var present = mine.FirstOrDefault(o => tin is not null ? o.Tin == tin : o.Name == name);
        if (present is not null)
        {
            await tx.CommitAsync(ct);
            return (present, false);
        }

        Tenant? tenant = null;
        if (!createTenant)
        {
            var candidates = tin is not null
                ? await owner.Tenants.Where(t => t.Tin == tin).ToListAsync(ct)
                : await owner.Tenants.Where(t => t.Name == name).ToListAsync(ct);
            if (candidates.Count == 0)
                throw new InvalidOperationException($"No tenant '{name}'{(tin is null ? "" : $" with TIN {tin}")} exists; add --create-tenant to create it.");
            if (candidates.Count > 1)
                throw new InvalidOperationException($"{candidates.Count} tenants match '{(tin ?? name)}'; give the TIN or create a new tenant.");
            tenant = candidates[0];
            if (tin is not null && tenant.Tin is null) tenant.Tin = tin;
        }
        else
        {
            tenant = new Tenant { Name = name, Tin = tin, CreatedAt = time.GetUtcNow() };
            owner.Tenants.Add(tenant);
        }

        if (await owner.AccountTenants.AnyAsync(at => at.TenantId == tenant.Id && at.AccountId != accountId, ct))
            throw new InvalidOperationException($"Tenant '{tenant.Name}' already belongs to another account.");
        var link = new AccountTenant
        {
            AccountId = accountId,
            TenantId = tenant.Id,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            AddedAt = time.GetUtcNow(),
        };
        owner.AccountTenants.Add(link);
        await owner.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (new AccountOrganization(tenant.Id, tenant.Name, link.DisplayName, tenant.Tin, link.AddedAt, null), true);
    }

    public async Task<IReadOnlyList<AccountOrganization>> OrganizationsAsync(Guid accountId, CancellationToken ct = default) =>
        await (from at in owner.AccountTenants
               join t in owner.Tenants on at.TenantId equals t.Id
               where at.AccountId == accountId
               orderby t.Name
               select new AccountOrganization(t.Id, t.Name, at.DisplayName, t.Tin, at.AddedAt, at.DisabledAt)).ToListAsync(ct);

    /// <summary>An organization of the account by TIN, id or exact name (tenant or display name).</summary>
    public async Task<AccountOrganization> FindOrganizationAsync(Guid accountId, string key, CancellationToken ct = default)
    {
        key = key.Trim();
        var all = await OrganizationsAsync(accountId, ct);
        var found = all.Where(o => o.Tin == key || o.TenantId.ToString() == key || o.Name == key || o.DisplayName == key).ToList();
        return found.Count switch
        {
            1 => found[0],
            0 => throw new InvalidOperationException($"The account has no organization '{key}'."),
            _ => throw new InvalidOperationException($"{found.Count} organizations of the account match '{key}'; give the TIN."),
        };
    }

    public async Task<IReadOnlyList<AccountRow>> ListAccountsAsync(CancellationToken ct = default)
    {
        var accounts = await owner.Accounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);
        var links = await owner.AccountTenants.AsNoTracking().ToListAsync(ct);
        var keys = await owner.ApiKeys.IgnoreQueryFilters().AsNoTracking()
            .Where(k => k.AccountId != null && k.RevokedAt == null).Select(k => k.AccountId!.Value).ToListAsync(ct);
        return accounts.Select(a => new AccountRow(a.Id, a.Name, a.Status, a.CreatedAt,
            links.Count(l => l.AccountId == a.Id), links.Count(l => l.AccountId == a.Id && l.DisabledAt != null),
            keys.Count(k => k == a.Id))).ToList();
    }

    /// <summary>Disables (never deletes) an organization: account keys stop seeing it, its own keys stop working.</summary>
    public async Task<AccountOrganization> DisableOrganizationAsync(Guid accountId, string key, CancellationToken ct = default)
    {
        var org = await FindOrganizationAsync(accountId, key, ct);
        var link = await owner.AccountTenants.SingleAsync(at => at.AccountId == accountId && at.TenantId == org.TenantId, ct);
        link.DisabledAt ??= time.GetUtcNow();
        await owner.SaveChangesAsync(ct);
        return org with { DisabledAt = link.DisabledAt };
    }

    public async Task EnableOrganizationAsync(Guid accountId, string key, CancellationToken ct = default)
    {
        var org = await FindOrganizationAsync(accountId, key, ct);
        var link = await owner.AccountTenants.SingleAsync(at => at.AccountId == accountId && at.TenantId == org.TenantId, ct);
        link.DisabledAt = null;
        await owner.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates an account key (printed once by the caller, only its hash is stored). With <paramref name="organizations"/>
    /// the key sees only those organizations of the account (api_key_tenants); otherwise every enabled organization.
    /// </summary>
    public async Task<CreatedAccountKey> CreateAccountKeyAsync(Guid accountId, IReadOnlyCollection<string> scopes, string name,
        IReadOnlyCollection<string>? organizations = null, CancellationToken ct = default)
    {
        name = Required(name, "Key name");
        if (scopes.Count == 0) throw new ArgumentException("At least one scope is required.", nameof(scopes));
        var unknown = scopes.Except(ApiKey.KnownScopes).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"Unknown scopes: {string.Join(", ", unknown)}. Known: {string.Join(", ", ApiKey.KnownScopes)}.");
        var restriction = new List<AccountOrganization>();
        foreach (var key in organizations ?? [])
        {
            var org = await FindOrganizationAsync(accountId, key, ct);
            if (!restriction.Contains(org)) restriction.Add(org);
        }

        var plain = ApiKeyFormat.Generate();
        var entity = new ApiKey
        {
            AccountId = accountId,
            Name = name,
            KeyHash = ApiKeyFormat.Hash(plain),
            KeyPrefix = plain[..ApiKeyFormat.StoredPrefixLength],
            Scopes = scopes.Distinct().Order(StringComparer.Ordinal).ToArray(),
            CreatedAt = time.GetUtcNow(),
        };
        await using var tx = await owner.Database.BeginTransactionAsync(ct);
        owner.ApiKeys.Add(entity);
        foreach (var org in restriction) owner.ApiKeyTenants.Add(new ApiKeyTenant { KeyId = entity.Id, TenantId = org.TenantId });
        await owner.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new CreatedAccountKey(new CreatedApiKey(entity.Id, entity.Name, entity.KeyPrefix, entity.Scopes, plain), accountId, restriction);
    }

    /// <summary>Revokes a key of the account by id or prefix. Returns false when the account has no such single key.</summary>
    public async Task<bool> RevokeAccountKeyAsync(Guid accountId, string idOrPrefix, CancellationToken ct = default)
    {
        var keys = owner.ApiKeys.IgnoreQueryFilters().Where(k => k.AccountId == accountId);
        var matches = Guid.TryParse(idOrPrefix, out var id)
            ? await keys.Where(k => k.Id == id).ToListAsync(ct)
            : await keys.Where(k => k.KeyPrefix == idOrPrefix).ToListAsync(ct);
        if (matches.Count != 1) return false;
        matches[0].RevokedAt ??= time.GetUtcNow();
        await owner.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> AccountKeysAsync(Guid accountId, CancellationToken ct = default) =>
        (await owner.ApiKeys.IgnoreQueryFilters().AsNoTracking().Where(k => k.AccountId == accountId).OrderBy(k => k.CreatedAt).ToListAsync(ct))
        .Select(k => new ApiKeyInfo(k.Id, k.Name, k.KeyPrefix, k.Scopes, k.CreatedAt, k.LastUsedAt, k.RevokedAt)).ToList();

    /// <summary>
    /// Imports organizations from CSV (header with columns name and tin; comma or semicolon). Idempotent: an organization
    /// whose TIN (or, without a TIN, name) is already in the account is reported as already present. Every row is handled
    /// on its own; errors are collected in the report and never stop the import.
    /// </summary>
    public async Task<IReadOnlyList<ImportRowResult>> ImportOrganizationsAsync(Guid accountId, string path, CancellationToken ct = default)
    {
        var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8, ct);
        var report = new List<ImportRowResult>();
        if (lines.Length == 0) return [new ImportRowResult(0, null, null, ImportRowOutcome.Error, "The file is empty.")];
        var separator = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
        var header = SplitCsv(lines[0], separator).Select(h => h.Trim().ToLowerInvariant()).ToList();
        var nameAt = header.IndexOf("name");
        var tinAt = header.IndexOf("tin");
        if (nameAt < 0 || tinAt < 0)
            return [new ImportRowResult(1, null, null, ImportRowOutcome.Error, "The header must have the columns name and tin.")];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = SplitCsv(lines[i], separator);
            var name = nameAt < cells.Count ? cells[nameAt].Trim() : "";
            var tin = tinAt < cells.Count ? cells[tinAt].Trim() : "";
            try
            {
                if (name.Length == 0) throw new ArgumentException("The name is empty.");
                if (tin.Length > 0 && !Tin.IsValid(tin)) throw new ArgumentException($"TIN must be 9 or 14 digits, got '{tin}'.");
                if (!seen.Add(tin.Length > 0 ? "tin:" + tin : "name:" + name)) throw new ArgumentException("Repeats an earlier row of the file.");
                owner.ChangeTracker.Clear();
                var (_, created) = await AddOrganizationAsync(accountId, name, tin.Length > 0 ? tin : null, createTenant: true, ct: ct);
                report.Add(new ImportRowResult(i + 1, name, tin, created ? ImportRowOutcome.Created : ImportRowOutcome.AlreadyPresent, null));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DbUpdateException or PostgresException)
            {
                owner.ChangeTracker.Clear();
                var message = ex is DbUpdateException { InnerException: PostgresException pg } ? pg.MessageText : ex.Message;
                report.Add(new ImportRowResult(i + 1, name, tin, ImportRowOutcome.Error, message));
            }
        }
        return report;
    }

    /// <summary>
    /// Deletes a tenant and everything it owns, in one transaction as the owner: every table with a tenant_id column
    /// (children before parents, from the foreign keys), then the tenants row. Refused while the tenant has active keys
    /// (its own, or account keys limited to it) unless <paramref name="force"/>.
    /// </summary>
    /// <param name="id">Needed only when several tenants have this name; the name must still match.</param>
    public async Task<DeletedTenant> DeleteTenantAsync(string name, bool force, Guid? id = null, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);

        var tenants = new List<(Guid Id, string Name)>();
        await using (var find = new NpgsqlCommand("SELECT id, name FROM tenants WHERE name = @name FOR UPDATE", connection, tx))
        {
            find.Parameters.AddWithValue("name", name);
            await using var reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) tenants.Add((reader.GetGuid(0), reader.GetString(1)));
        }
        if (id is { } chosen) tenants = tenants.Where(t => t.Id == chosen).ToList();
        if (tenants.Count != 1)
            throw new InvalidOperationException(tenants.Count == 0 ? $"Tenant '{name}'{(id is null ? "" : $" with id {id}")} not found."
                : $"{tenants.Count} tenants are named '{name}'; add --id <tenant id> (see list-tenants).");
        var tenant = tenants[0];

        await using (var setTenant = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", connection, tx))
        {
            setTenant.Parameters.AddWithValue("t", tenant.Id.ToString());
            await setTenant.ExecuteNonQueryAsync(ct);
        }

        await using (var keys = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM api_keys WHERE tenant_id = @t AND revoked_at IS NULL)
                 + (SELECT count(*) FROM api_key_tenants kt JOIN api_keys k ON k.id = kt.key_id WHERE kt.tenant_id = @t AND k.revoked_at IS NULL)
            """, connection, tx))
        {
            keys.Parameters.AddWithValue("t", tenant.Id);
            var active = (long)(await keys.ExecuteScalarAsync(ct))!;
            if (active > 0 && !force)
                throw new InvalidOperationException($"Tenant '{tenant.Name}' has {active} active API keys; revoke them first or add --force.");
        }

        var order = await DeletionOrderAsync(connection, tx, ct);
        var counts = new List<(string, int)>();
        // The EDO status history is append-only for everybody (trigger). Deleting a whole tenant is the one exception:
        // the owner switches the trigger off inside this transaction only and on again before the commit; on any error
        // the rollback restores it as well (DDL is transactional in PostgreSQL).
        await ExecAsync(connection, tx, $"ALTER TABLE edo_document_status_logs DISABLE TRIGGER {AppendOnlyTrigger}", ct);
        foreach (var table in order)
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM \"{table}\" WHERE tenant_id = @t", connection, tx);
            delete.Parameters.AddWithValue("t", tenant.Id);
            var rows = await delete.ExecuteNonQueryAsync(ct);
            if (rows > 0) counts.Add((table, rows));
        }
        await ExecAsync(connection, tx, $"ALTER TABLE edo_document_status_logs ENABLE TRIGGER {AppendOnlyTrigger}", ct);
        await using (var deleteTenant = new NpgsqlCommand("DELETE FROM tenants WHERE id = @t", connection, tx))
        {
            deleteTenant.Parameters.AddWithValue("t", tenant.Id);
            counts.Add(("tenants", await deleteTenant.ExecuteNonQueryAsync(ct)));
        }
        await tx.CommitAsync(ct);
        return new DeletedTenant(tenant.Id, tenant.Name, counts);
    }

    private const string AppendOnlyTrigger = "trg_edo_document_status_logs_append_only";

    private static async Task ExecAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Tables with a tenant_id column, every referencing table before the table it references.</summary>
    private static async Task<IReadOnlyList<string>> DeletionOrderAsync(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("""
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND c.relname <> 'tenants' ORDER BY c.relname
            """, connection, tx))
        await using (var reader = await list.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) tables.Add(reader.GetString(0));

        var edges = new List<(string Child, string Parent)>();
        await using (var fk = new NpgsqlCommand("""
            SELECT ch.relname, pa.relname FROM pg_constraint k
            JOIN pg_class ch ON ch.oid = k.conrelid JOIN pg_class pa ON pa.oid = k.confrelid
            JOIN pg_namespace n ON n.oid = ch.relnamespace
            WHERE k.contype = 'f' AND n.nspname = 'public' AND ch.oid <> pa.oid
            """, connection, tx))
        await using (var reader = await fk.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) edges.Add((reader.GetString(0), reader.GetString(1)));

        var set = tables.ToHashSet(StringComparer.Ordinal);
        var order = new List<string>();
        var remaining = new HashSet<string>(tables, StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            // A table can go when no remaining table references it.
            var ready = remaining.Where(t => !edges.Any(e => e.Parent == t && remaining.Contains(e.Child) && set.Contains(e.Child)))
                .Order(StringComparer.Ordinal).ToList();
            if (ready.Count == 0) throw new InvalidOperationException("Cyclic foreign keys between tenant tables: " + string.Join(", ", remaining));
            order.AddRange(ready);
            remaining.ExceptWith(ready);
        }
        return order;
    }

    private static string Required(string? value, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{what} is required.") : value.Trim();

    /// <summary>Splits one CSV line (double quotes, doubled quotes inside).</summary>
    public static List<string> SplitCsv(string line, char separator)
    {
        var result = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == separator) { result.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(c);
        }
        result.Add(cell.ToString());
        return result;
    }


}
