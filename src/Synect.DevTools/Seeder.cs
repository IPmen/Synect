using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Analytics.Totals;
using Synect.Application.Queue;
using Synect.Application.Secrets;
using Synect.Application.Sync;
using Synect.Domain.Sync;
using Synect.Domain.Tenants;
using Synect.Infrastructure.Adapters;
using Synect.Infrastructure.Adapters.Fake;

namespace Synect.DevTools;

/// <summary>
/// Fills the local database through the real sync services and the fake adapters (no direct inserts),
/// so the data goes through mapping, raw storage, idempotent upserts and the monthly totals refresh.
/// </summary>
public sealed class Seeder(IServiceProvider services, TenantScopeRunner tenants, FakeBankDataSource bank, FakeEdoDataSource edo, TimeProvider time)
{
    private static readonly TimeSpan Tashkent = TimeSpan.FromHours(5);
    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().ToOffset(Tashkent).DateTime);

    public async Task SeedDemoAsync(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            var tenant = await EnsureTenantAsync($"Demo client {i}", (310_000_000 + i).ToString());
            var bankId = await EnsureConnectionAsync(tenant, AdapterKind.Bank, "Kapital Bank", $"Kapital Bank (demo {i})");
            var edoId = await EnsureConnectionAsync(tenant, AdapterKind.Edo, "Didox", $"Didox (demo {i})");

            var b = bank.For(bankId);
            b.Seed = 100 + i;
            b.AccountCount = 2;
            b.AccountCurrencies = ["000", "840"];
            b.CounterpartyPoolSize = 40;
            b.NoInnShare = 0.05;
            b.MaxOperationsPerDay = 8;
            var e = edo.For(edoId);
            e.Seed = 200 + i;
            e.DocumentCount = 400;
            e.SpreadDays = 365;
            e.CounterpartyPoolSize = 40;
            e.PageSize = 100;
            e.BaseTime = time.GetUtcNow().AddDays(-365);

            var sw = Stopwatch.StartNew();
            var bankOutcome = await SyncBankAsync(tenant, bankId, Today.AddDays(-119), Today);
            var edoOutcome = await SyncEdoAsync(tenant, edoId);
            Console.WriteLine($"Demo client {i} ({tenant}): bank {bankOutcome.Result} {bankOutcome.RecordCount} records, " +
                              $"EDO {edoOutcome.Result} {edoOutcome.RecordCount} records, {sw.Elapsed.TotalSeconds:F1}s");
        }
    }

    /// <summary>
    /// A small data set for one organization of seed-account: a fake bank (2 accounts, 60 days) and a fake Didox
    /// connection (120 documents), loaded through the real sync services (which also run the reconciliation and the
    /// attention rules after each load). Returns the connection ids.
    /// </summary>
    public async Task<(Guid Bank, Guid Edo)> SeedSmallOrganizationAsync(Guid tenant, int index)
    {
        var bankId = await EnsureConnectionAsync(tenant, AdapterKind.Bank, "Kapital Bank", $"Kapital Bank (seed {index})");
        var edoId = await EnsureConnectionAsync(tenant, AdapterKind.Edo, "Didox", $"Didox (seed {index})");
        var b = bank.For(bankId);
        b.Seed = 5000 + index;
        b.AccountCount = 2;
        b.AccountCurrencies = ["000", "840"];
        b.CounterpartyPoolSize = 15;
        b.NoInnShare = 0.05;
        b.MaxOperationsPerDay = 4;
        var e = edo.For(edoId);
        e.Seed = 6000 + index;
        e.DocumentCount = 120;
        e.SpreadDays = 120;
        e.CounterpartyPoolSize = 15;
        e.PageSize = 100;
        e.BaseTime = time.GetUtcNow().AddDays(-120);
        await SyncBankAsync(tenant, bankId, Today.AddDays(-59), Today);
        await SyncEdoAsync(tenant, edoId);
        return (bankId, edoId);
    }

    public async Task SeedLoadAsync(int operations, int documents)
    {
        const int days = 1096; // three years
        var tenant = await EnsureTenantAsync("Load test", "319999999");
        var bankId = await EnsureConnectionAsync(tenant, AdapterKind.Bank, "Kapital Bank (load)");
        var edoId = await EnsureConnectionAsync(tenant, AdapterKind.Edo, "Didox (load)");

        var b = bank.For(bankId);
        b.Seed = 900;
        b.AccountCount = 3;
        b.AccountCurrencies = ["000", "000", "840"];
        var perAccountDay = operations / (days * b.AccountCount);
        b.MinOperationsPerDay = Math.Max(1, perAccountDay - 6);
        b.MaxOperationsPerDay = perAccountDay + 6;
        b.CounterpartyPoolSize = 2000;
        b.NoInnShare = 0.03;
        var e = edo.For(edoId);
        e.Seed = 901;
        e.DocumentCount = documents;
        e.SpreadDays = days;
        e.CounterpartyPoolSize = 2000;
        e.PageSize = 1000;
        e.BaseTime = time.GetUtcNow().AddDays(-days);

        var sw = Stopwatch.StartNew();
        var from = Today.AddDays(-days + 1);
        // Month by month: progress output and bounded transactions; every step is idempotent.
        for (var start = from; start <= Today; start = start.AddMonths(1))
        {
            var end = start.AddMonths(1).AddDays(-1);
            if (end > Today) end = Today;
            var outcome = await SyncBankAsync(tenant, bankId, start, end);
            Console.WriteLine($"  bank {start:yyyy-MM}: {outcome.Result}, {outcome.RecordCount} records, {sw.Elapsed.TotalSeconds:F0}s");
            if (outcome.Result != SyncRunResult.Succeeded) throw new InvalidOperationException(outcome.Error);
        }
        var edoOutcome = await SyncEdoAsync(tenant, edoId);
        Console.WriteLine($"  EDO: {edoOutcome.Result}, {edoOutcome.RecordCount} records, {sw.Elapsed.TotalSeconds:F0}s");

        var counts = await tenants.InTenantAsync(tenant, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            return await db.InTransactionAsync(async t => (
                Ops: await db.BankTransactions.CountAsync(t),
                Docs: await db.EdoDocuments.CountAsync(t),
                Totals: await db.CounterpartyMonthly.CountAsync(t)), ct);
        }, CancellationToken.None);
        Console.WriteLine($"Load test ({tenant}): {counts.Ops} bank operations, {counts.Docs} documents, " +
                          $"{counts.Totals} monthly total rows, {sw.Elapsed.TotalMinutes:F1} min");
    }

    /// <summary>The golden set tenant with one fake bank and one fake Didox connection (data is loaded by reconcile-eval).</summary>
    public async Task<(Guid Tenant, Guid Bank, Guid Edo)> EnsureGoldenTenantAsync()
    {
        var tenant = await EnsureTenantAsync(ReconciliationGoldenSet.TenantName, "319000001");
        var bankId = await EnsureConnectionAsync(tenant, AdapterKind.Bank, "Kapital Bank (golden set)");
        var edoId = await EnsureConnectionAsync(tenant, AdapterKind.Edo, "Didox (golden set)");
        return (tenant, bankId, edoId);
    }

    /// <summary>The attention golden set tenant: bank, Didox, a broken bank connection and a stale Didox connection.</summary>
    public async Task<(Guid Tenant, AttentionGoldenSet.Connections Connections)> EnsureAttentionTenantAsync()
    {
        var tenant = await EnsureTenantAsync(AttentionGoldenSet.TenantName, "319000002");
        var connections = new AttentionGoldenSet.Connections(
            await EnsureConnectionAsync(tenant, AdapterKind.Bank, "Kapital Bank (attention set)"),
            await EnsureConnectionAsync(tenant, AdapterKind.Edo, "Didox (attention set)"),
            await EnsureConnectionAsync(tenant, AdapterKind.Bank, "Kapital Bank (attention set, broken)"),
            await EnsureConnectionAsync(tenant, AdapterKind.Edo, "Didox (attention set, stale)"));
        return (tenant, connections);
    }

    public Task<int> RebuildTotalsAsync(Guid tenant) => tenants.InTenantAsync(tenant, (sp, ct) =>
    {
        var db = sp.GetRequiredService<ISynectDbContext>();
        return db.InTransactionAsync(t => sp.GetRequiredService<CounterpartyMonthlyRefresher>().RebuildAllAsync(t), ct);
    }, CancellationToken.None);

    public Task<int> RebuildAliasesAsync(Guid tenant) => tenants.InTenantAsync(tenant, (sp, ct) =>
        sp.GetRequiredService<CounterpartyAliasRebuilder>().RebuildAsync(ct), CancellationToken.None);

    private Task<SyncOutcome> SyncBankAsync(Guid tenant, Guid connection, DateOnly from, DateOnly to) =>
        tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<BankSyncService>().SyncAsync(connection, from, to, ct), CancellationToken.None);

    private Task<SyncOutcome> SyncEdoAsync(Guid tenant, Guid connection) =>
        tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<EdoSyncService>().SyncAsync(connection, ct), CancellationToken.None);

    private async Task<Guid> EnsureTenantAsync(string name, string inn)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
        var existing = await db.Tenants.Where(t => t.Name == name).Select(t => (Guid?)t.Id).SingleOrDefaultAsync();
        if (existing is { } id) return id;
        var tenant = new Tenant { Name = name, Tin = inn, CreatedAt = time.GetUtcNow() };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    /// <summary>Finds the connection by its name or by the name earlier seeds gave it (oldName), else creates it.</summary>
    private Task<Guid> EnsureConnectionAsync(Guid tenant, AdapterKind kind, string name, string? oldName = null) =>
        tenants.InTenantAsync(tenant, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            var existing = await db.InTransactionAsync(t =>
                db.Connections.Where(c => c.Name == name || (oldName != null && c.Name == oldName)).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(t), ct);
            if (existing is { } id) return id;

            var connection = new Connection
            {
                Name = name,
                SourceCode = kind == AdapterKind.Bank ? FakeBankAdapter.Code : FakeEdoAdapter.Code,
                AdapterKind = kind,
                CreatedAt = time.GetUtcNow(),
            };
            await db.InTransactionAsync(async t =>
            {
                db.Connections.Add(connection);
                await db.SaveChangesAsync(t);
            }, ct);
            // Random demo credentials, encrypted by the secret store; never printed.
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            await sp.GetRequiredService<ISecretStore>().SetAsync(connection.Id, ConnectionSecret.CredentialsKind,
                LoginPasswordCredentials.Create("demo-" + connection.Id.ToString("N")[..8], password), ct);
            return connection.Id;
        }, CancellationToken.None);
}
