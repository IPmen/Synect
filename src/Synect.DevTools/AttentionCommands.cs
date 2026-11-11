using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Attention;
using Synect.Application.Queue;
using Synect.Application.Sync;
using Synect.Domain.Attention;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Fake;

namespace Synect.DevTools;

/// <summary>A clock that DevTools can move (attention-eval only; every other command uses the system clock).</summary>
public sealed class DevClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
}

/// <summary>attention (evaluate now), attention-eval (golden set) and attention-bench (timing).</summary>
public sealed class AttentionCommands(IServiceProvider services, TenantScopeRunner tenants, Seeder seeder, FakeBankDataSource bank, FakeEdoDataSource edo)
{
    /// <summary>Evaluates every rule for the tenant now and prints the changes and the open items.</summary>
    public async Task EvaluateAsync(Guid tenant)
    {
        var r = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<AttentionEvaluator>().EvaluateAsync(AttentionEvaluator.AllCategories, ct),
            CancellationToken.None);
        Print(r);
        await PrintItemsAsync(tenant);
    }

    /// <summary>
    /// Loads the attention golden set with a fixed clock, evaluates every rule and compares the items with the expected
    /// ones. The attention items of the golden set tenant are removed first, so the output is the same on every run.
    /// </summary>
    public async Task<int> EvalAsync()
    {
        var clock = services.GetRequiredService<TimeProvider>() as DevClock
            ?? throw new InvalidOperationException("attention-eval needs the DevTools clock.");
        var (tenant, connections) = await seeder.EnsureAttentionTenantAsync();
        await tenants.InTenantAsync(tenant, (sp, ct) =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            return db.InTransactionAsync(async t =>
            {
                await db.AttentionItemEvents.ExecuteDeleteAsync(t);
                return await db.AttentionItems.ExecuteDeleteAsync(t);
            }, ct);
        }, CancellationToken.None);

        AttentionGoldenSet.Apply(bank, edo, connections);
        var outcomes = await AttentionGoldenSet.LoadAsync(connections, clock.Set,
            (id, from, to) => tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<BankSyncService>().SyncAsync(id, from, to, ct), CancellationToken.None),
            id => tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<EdoSyncService>().SyncAsync(id, ct), CancellationToken.None));
        var run = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<AttentionEvaluator>().EvaluateAsync(AttentionEvaluator.AllCategories, ct),
            CancellationToken.None);
        var items = await ItemsAsync(tenant);

        Console.WriteLine($"Attention golden set ({tenant}), evaluated at {AttentionGoldenSet.EvaluateAt:yyyy-MM-dd HH:mm zzz}");
        Console.WriteLine("Loads: " + string.Join(", ", outcomes.Select(o => o.Result)));
        Console.WriteLine($"Last full evaluation: {run.Created} created, {run.Reopened} reopened, {run.Resolved} resolved, {run.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine("Created items (status, severity, rule, cases, title):");
        foreach (var i in items)
            Console.WriteLine($"  {i.Status,-9} {i.Severity,-8} {i.RuleCode,-40} {Cases(i),3}  {i.Title}");

        var errors = new List<string>();
        foreach (var e in AttentionGoldenSet.Expected)
        {
            var hit = items.Where(i => i.RuleCode == e.RuleCode).ToList();
            var cases = hit.Sum(Cases);
            if (hit.Count == 0) errors.Add($"MISSING   {e.RuleCode} ({e.Case})");
            else if (hit.Any(i => i.Severity != e.Severity)) errors.Add($"SEVERITY  {e.RuleCode}: {hit[0].Severity} instead of {e.Severity}");
            else if (cases != e.Cases) errors.Add($"CASES     {e.RuleCode}: {cases} instead of {e.Cases}");
        }
        foreach (var i in items.Where(i => AttentionGoldenSet.Expected.All(e => e.RuleCode != i.RuleCode)))
            errors.Add($"UNEXPECTED {i.RuleCode}: {i.Title}");
        Console.WriteLine("Not created (expected):");
        foreach (var n in AttentionGoldenSet.ExpectedNotCreated)
            Console.WriteLine($"  {n.RuleCode,-40} {(items.Any(i => i.RuleCode == n.RuleCode) ? "CREATED (error)" : "not created")}: {n.Why}");
        foreach (var n in AttentionGoldenSet.NotListed) Console.WriteLine($"  below threshold, not in any item: {n}");
        Console.WriteLine(errors.Count == 0 ? $"Result: all {AttentionGoldenSet.Expected.Count} expected items found, nothing unexpected" : "Errors:");
        foreach (var e in errors) Console.WriteLine("  " + e);
        return errors.Count == 0 ? 0 : 2;
    }

    /// <summary>Times full evaluations and the evaluation after a bank load (Connection, Bank, Reconciliation).</summary>
    public async Task BenchAsync(Guid tenant, int runs)
    {
        var counts = await tenants.InTenantAsync(tenant, (sp, ct) =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            return db.InTransactionAsync(async t => (Ops: await db.BankTransactions.CountAsync(t), Docs: await db.EdoDocuments.CountAsync(t),
                Accounts: await db.BankAccounts.CountAsync(t)), ct);
        }, CancellationToken.None);
        Console.WriteLine($"Tenant {tenant}: {counts.Ops} bank operations, {counts.Docs} documents, {counts.Accounts} accounts");

        async Task<List<(double Ms, AttentionEvaluation Result)>> Time(IReadOnlyList<AttentionCategory> categories)
        {
            var list = new List<(double, AttentionEvaluation)>();
            for (var i = 0; i < Math.Max(1, runs); i++)
            {
                var sw = Stopwatch.StartNew();
                var r = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<AttentionEvaluator>().EvaluateAsync(categories, ct), CancellationToken.None);
                list.Add((sw.Elapsed.TotalMilliseconds, r));
            }
            return list;
        }

        var full = await Time(AttentionEvaluator.AllCategories);
        Report("full (every rule)", full);
        Report("after a bank load (connection, bank, reconciliation)", await Time([AttentionCategory.Connection, AttentionCategory.Bank, AttentionCategory.Reconciliation]));
        Report("after a Didox load (connection, documents, reconciliation)", await Time([AttentionCategory.Connection, AttentionCategory.Documents, AttentionCategory.Reconciliation]));
        Console.WriteLine("Findings per rule: " + string.Join(", ", full[^1].Result.Findings.Select(f => $"{f.Key}={f.Value}")));
        await PrintItemsAsync(tenant);

        static void Report(string name, List<(double Ms, AttentionEvaluation Result)> times)
        {
            var ms = times.Select(t => t.Ms).Order().ToList();
            Console.WriteLine($"{name,-58} min {ms[0],6:F0} ms, median {ms[ms.Count / 2],6:F0} ms, max {ms[^1],6:F0} ms over {ms.Count} runs; " +
                              $"open items {times[^1].Result.Open}, first run: {times[0].Result.Created} created");
        }
    }

    private async Task PrintItemsAsync(Guid tenant)
    {
        Console.WriteLine("Open items:");
        foreach (var i in await ItemsAsync(tenant))
            Console.WriteLine($"  {i.Status,-9} {i.Severity,-8} {i.RuleCode,-40} {Cases(i),5}  {i.Title}");
    }

    private Task<List<AttentionItem>> ItemsAsync(Guid tenant) => tenants.InTenantAsync(tenant, (sp, ct) =>
    {
        var db = sp.GetRequiredService<ISynectDbContext>();
        return db.InTransactionAsync(async t => (await db.AttentionItems.AsNoTracking().Where(i => i.Status != AttentionStatus.Resolved && i.Status != AttentionStatus.Archived).ToListAsync(t))
            .OrderBy(i => i.Severity).ThenBy(i => i.RuleCode, StringComparer.Ordinal).ThenBy(i => i.DedupKey, StringComparer.Ordinal).ToList(), ct);
    }, CancellationToken.None);

    /// <summary>Cases in an item: the count of a grouped item, 1 for a single case.</summary>
    private static int Cases(AttentionItem i)
    {
        using var doc = JsonDocument.Parse(i.Details);
        return i.EntityKind == "tenant" && doc.RootElement.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
    }

    private static void Print(AttentionEvaluation r) =>
        Console.WriteLine($"evaluation ({string.Join(", ", r.Categories)}): {r.Created} created, {r.Reopened} reopened, {r.Updated} updated, " +
                          $"{r.Resolved} resolved, {r.Open} open, {r.Elapsed.TotalMilliseconds:F0} ms");
}
