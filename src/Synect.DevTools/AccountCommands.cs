using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Domain.Access;
using Synect.Domain.Accounts;
using Synect.Domain.Sync;
using Synect.Infrastructure.Accounts;

namespace Synect.DevTools;

/// <summary>
/// Accounts and organizations (stage 6.5) and delete-tenant. Account administration runs as the schema owner
/// (ConnectionStrings:SynectOwner from user-secrets; the application role cannot read the account tables); onboarding
/// marks and seeding go through the application services in the organization's own tenant context.
/// </summary>
public sealed class AccountCommands(IServiceProvider services, IConfiguration configuration, TenantScopeRunner tenants, Seeder seeder,
    TimeProvider time, ILogger<AccountCommands> logger)
{
    private AccountAdmin Admin()
    {
        var owner = configuration.GetConnectionString("SynectOwner")
            ?? throw new InvalidOperationException("ConnectionStrings:SynectOwner is not set (dotnet user-secrets set \"ConnectionStrings:SynectOwner\" \"...\" --project src/Synect.DevTools).");
        return AccountAdmin.Create(owner, time);
    }

    public async Task<int> CreateAccountAsync(CliArgs o)
    {
        await using var admin = new Disposable(Admin());
        var account = await admin.Value.CreateAccountAsync(Need(o, "name"));
        Console.WriteLine($"Account created: {account.Name} ({account.Id})");
        return 0;
    }

    public async Task<int> AddOrganizationAsync(CliArgs o)
    {
        await using var admin = new Disposable(Admin());
        var account = await admin.Value.FindAccountAsync(Need(o, "account"));
        var (org, created) = await admin.Value.AddOrganizationAsync(account.Id, Need(o, "name"), o.Get("tin"), o.Get("create-tenant") == "true", o.Get("display-name"));
        Console.WriteLine($"{(created ? "Added" : "Already in the account")}: {org.Name} TIN {org.Tin ?? "-"} (tenant {org.TenantId}) -> account {account.Name}");
        return 0;
    }

    public async Task<int> ListAccountsAsync(CliArgs o)
    {
        await using var admin = new Disposable(Admin());
        if (o.Get("account") is { } name)
        {
            var account = await admin.Value.FindAccountAsync(name);
            Console.WriteLine($"{account.Name} ({account.Id}) {account.Status}");
            foreach (var org in await admin.Value.OrganizationsAsync(account.Id))
                Console.WriteLine($"  {org.Tin ?? "-",-14} {org.Name}{(org.DisplayName is { } d ? $" (as \"{d}\")" : "")}  tenant {org.TenantId}" +
                                  $"{(org.DisabledAt is { } at ? $"  DISABLED {at:u}" : "")}");
            Console.WriteLine("  keys:");
            foreach (var k in await admin.Value.AccountKeysAsync(account.Id))
                Console.WriteLine($"    {k.Id}  {k.KeyPrefix}…  [{string.Join(",", k.Scopes)}]  {k.Name}{(k.RevokedAt is { } r ? $"  REVOKED {r:u}" : "")}");
            return 0;
        }
        var rows = await admin.Value.ListAccountsAsync();
        foreach (var a in rows)
            Console.WriteLine($"{a.Id}  {a.Name,-30} {a.Status,-9} organizations {a.Organizations} (disabled {a.DisabledOrganizations}), active keys {a.ActiveKeys}");
        if (rows.Count == 0) Console.WriteLine("No accounts.");
        return 0;
    }

    /// <summary>list-tenants: every tenant (name, id, TIN, account), to pick names for delete-tenant.</summary>
    public async Task<int> ListTenantsAsync()
    {
        await using var admin = new Disposable(Admin());
        var db = admin.Value.Db;
        var rows = await (from t in db.Tenants.AsNoTracking()
                          join at in db.AccountTenants on t.Id equals at.TenantId into links
                          from at in links.DefaultIfEmpty()
                          join a in db.Accounts on at.AccountId equals a.Id into accounts
                          from a in accounts.DefaultIfEmpty()
                          orderby t.Name
                          select new { t.Id, t.Name, t.Tin, Account = a == null ? null : a.Name }).ToListAsync();
        foreach (var r in rows) Console.WriteLine($"{r.Id}  {r.Tin ?? "-",-14} {r.Name}{(r.Account is { } acc ? $"  [account {acc}]" : "")}");
        Console.WriteLine($"{rows.Count} tenants.");
        return 0;
    }

    public async Task<int> DisableOrganizationAsync(CliArgs o)
    {
        await using var admin = new Disposable(Admin());
        var account = await admin.Value.FindAccountAsync(Need(o, "account"));
        var org = await admin.Value.DisableOrganizationAsync(account.Id, Need(o, "organization"));
        logger.LogWarning("Organization {TenantId} of account {AccountId} disabled", org.TenantId, account.Id);
        Console.WriteLine($"Disabled: {org.Name} TIN {org.Tin ?? "-"} (its data is kept).");
        return 0;
    }

    public async Task<int> CreateAccountKeyAsync(CliArgs o)
    {
        await using var admin = new Disposable(Admin());
        var account = await admin.Value.FindAccountAsync(Need(o, "account"));
        var scopes = (o.Get("scopes") ?? ApiKey.ScopeRead).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var organizations = o.Get("organizations")?.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var created = await admin.Value.CreateAccountKeyAsync(account.Id, scopes, o.Get("name") ?? $"account key {DateTime.UtcNow:yyyy-MM-dd HH:mm}", organizations);
        Console.WriteLine($"API key created for account {account.Name} ({account.Id})");
        Console.WriteLine($"  id:            {created.Key.Id}");
        Console.WriteLine($"  name:          {created.Key.Name}");
        Console.WriteLine($"  prefix:        {created.Key.KeyPrefix}");
        Console.WriteLine($"  scopes:        {string.Join(", ", created.Key.Scopes)}");
        Console.WriteLine($"  organizations: {(created.Restriction.Count == 0 ? "all enabled organizations of the account" : string.Join("; ", created.Restriction.Select(r => $"{r.Name} ({r.Tin ?? "-"})")))}");
        Console.WriteLine();
        Console.WriteLine("Key (shown only now; Synect stores only its SHA-256 hash):");
        Console.WriteLine(created.Key.Key);
        return 0;
    }

    public async Task<int> RevokeAccountKeyAsync(CliArgs o, string? positionalKey)
    {
        await using var admin = new Disposable(Admin());
        var account = await admin.Value.FindAccountAsync(Need(o, "account"));
        var key = o.Get("key") ?? positionalKey ?? throw new ArgumentException("--key <key id|key prefix> is required");
        var revoked = await admin.Value.RevokeAccountKeyAsync(account.Id, key);
        Console.WriteLine(revoked ? $"Key {key} of account {account.Name} revoked." : $"No single key '{key}' found for account {account.Name}.");
        return revoked ? 0 : 1;
    }

    public async Task<int> ImportOrganizationsAsync(CliArgs o)
    {
        await using var admin = new Disposable(Admin());
        var account = await admin.Value.FindAccountAsync(Need(o, "account"));
        var report = await admin.Value.ImportOrganizationsAsync(account.Id, Need(o, "file"));
        foreach (var r in report)
            Console.WriteLine($"line {r.Line,4}: {r.Outcome,-14} {r.Tin,-14} {r.Name}{(r.Message is { } m ? "  - " + m : "")}");
        Console.WriteLine($"Created {report.Count(r => r.Outcome == ImportRowOutcome.Created)}, already present {report.Count(r => r.Outcome == ImportRowOutcome.AlreadyPresent)}, " +
                          $"errors {report.Count(r => r.Outcome == ImportRowOutcome.Error)}.");
        return report.Any(r => r.Outcome == ImportRowOutcome.Error) ? 3 : 0;
    }

    /// <summary>set-onboarding --tenant --source (connection name, id or source code) --status --note: a manual mark only.</summary>
    public async Task<int> SetOnboardingAsync(Guid tenant, CliArgs o)
    {
        var source = Need(o, "source");
        var status = o.Enum("status", OnboardingStatus.NotStarted);
        var note = o.Get("note");
        var changed = await tenants.InTenantAsync(tenant, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            return await db.InTransactionAsync(async t =>
            {
                var all = await db.Connections.ToListAsync(t);
                var matches = all.Where(c => c.Id.ToString() == source || c.Name == source || c.SourceCode == source).ToList();
                if (matches.Count != 1)
                    throw new InvalidOperationException(matches.Count == 0 ? $"No connection '{source}'." : $"{matches.Count} connections match '{source}'; give the connection id.");
                matches[0].SetOnboarding(status, note);
                await db.SaveChangesAsync(t);
                return matches[0].Name;
            }, ct);
        }, CancellationToken.None);
        Console.WriteLine($"Onboarding of '{changed}': {status}{(string.IsNullOrWhiteSpace(note) ? "" : " - " + note.Trim())}");
        return 0;
    }

    /// <summary>
    /// delete-tenant --name X --confirm X [--id ...] [--force]: deletes the tenant and all its data in one transaction as the owner.
    /// The name must be repeated exactly; a tenant with active keys is kept unless --force.
    /// </summary>
    public async Task<int> DeleteTenantAsync(CliArgs o)
    {
        var name = Need(o, "name");
        if (o.Get("confirm") != name)
        {
            Console.Error.WriteLine("Refused: repeat the exact tenant name in --confirm.");
            return 2;
        }
        await using var admin = new Disposable(Admin());
        Guid? id = o.Get("id") is { } raw ? Guid.Parse(raw) : null;
        var deleted = await admin.Value.DeleteTenantAsync(name, o.Get("force") == "true", id);
        logger.LogWarning("Tenant {TenantId} '{TenantName}' deleted with {Rows} rows ({Tables})", deleted.TenantId, deleted.Name, deleted.Total,
            string.Join(", ", deleted.Rows.Select(r => $"{r.Table} {r.Rows}")));
        Console.WriteLine($"Deleted tenant '{deleted.Name}' ({deleted.TenantId}): {deleted.Total} rows");
        foreach (var (table, rows) in deleted.Rows) Console.WriteLine($"  {table,-34} {rows,8}");
        return 0;
    }

    /// <summary>
    /// seed-account --name X [--organizations 50]: an account with N organizations, each with a fake bank and a fake Didox
    /// connection loaded through the real sync services (reconciliation and attention rules run after the loads). Some
    /// organizations get a broken connection or an onboarding mark, so that the portfolio tools have something to sort.
    /// </summary>
    public async Task<int> SeedAccountAsync(CliArgs o)
    {
        var name = Need(o, "name");
        var count = o.Int("organizations", 50);
        await using var admin = new Disposable(Admin());
        Account account;
        try { account = await admin.Value.FindAccountAsync(name); }
        catch (InvalidOperationException) { account = await admin.Value.CreateAccountAsync(name); }

        var total = Stopwatch.StartNew();
        for (var i = 1; i <= count; i++)
        {
            var sw = Stopwatch.StartNew();
            var tin = (400_000_000 + Math.Abs(account.Id.GetHashCode() % 50_000) * 100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var orgName = $"{name} org {i:D2}";
            admin.Value.Db.ChangeTracker.Clear();
            var (org, created) = await admin.Value.AddOrganizationAsync(account.Id, orgName, tin, createTenant: true);
            if (!created)
            {
                Console.WriteLine($"{orgName}: already present, skipped");
                continue;
            }
            var (bank, edo) = await seeder.SeedSmallOrganizationAsync(org.TenantId, i);
            await tenants.InTenantAsync(org.TenantId, async (sp, ct) =>
            {
                var db = sp.GetRequiredService<ISynectDbContext>();
                await db.InTransactionAsync(async t =>
                {
                    var connections = await db.Connections.Where(c => c.Id == bank || c.Id == edo).ToListAsync(t);
                    foreach (var c in connections) c.SetOnboarding(OnboardingStatus.Connected, null);
                    if (i % 10 == 3)
                        connections.Single(c => c.Id == bank).MarkNeedsAttention("Seed: the bank rejected the credentials (demo).", time.GetUtcNow());
                    if (i % 10 == 7)
                        connections.Single(c => c.Id == edo).SetOnboarding(OnboardingStatus.WaitingForAccess, "Seed: заявление в банк подано");
                    await db.SaveChangesAsync(t);
                }, ct);
                await sp.GetRequiredService<Synect.Application.Attention.AttentionEvaluator>()
                    .EvaluateAsync(Synect.Application.Attention.AttentionEvaluator.AllCategories, ct);
                return true;
            }, CancellationToken.None);
            Console.WriteLine($"{orgName} TIN {tin}: loaded in {sw.Elapsed.TotalSeconds:F1}s");
        }
        Console.WriteLine($"Account {account.Name} ({account.Id}): {count} organizations in {total.Elapsed.TotalSeconds:F0}s. " +
                          "Create a key with create-account-key --account \"" + account.Name + "\".");
        return 0;
    }

    private static string Need(CliArgs o, string key) =>
        o.Get(key) is { Length: > 0 } v && v != "true" ? v : throw new ArgumentException($"--{key} is required");

    /// <summary>Disposes the owner DbContext of an <see cref="AccountAdmin"/>.</summary>
    private sealed class Disposable(AccountAdmin admin) : IAsyncDisposable
    {
        public AccountAdmin Value => admin;
        public ValueTask DisposeAsync() => admin.Db.DisposeAsync();
    }
}
