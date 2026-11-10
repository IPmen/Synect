using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Analytics.Totals;
using Synect.Application.Queue;
using Synect.Application.Reconciliation;
using Synect.Application.Sync;
using Synect.Domain.Common;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Fake;

namespace Synect.DevTools;

/// <summary>reconcile, rebuild-reconciliation, reconcile-eval, reconcile-bench and normalize-currency.</summary>
public sealed class ReconciliationCommands(IServiceProvider services, TenantScopeRunner tenants, Seeder seeder, FakeBankDataSource bank, FakeEdoDataSource edo)
{
    /// <summary>Full recalculation, or only the given INNs (comma separated; "-" = payments without an INN).</summary>
    public async Task ReconcileAsync(Guid tenant, string? inns)
    {
        var scope = inns is null ? ReconciliationScope.Full
            : ReconciliationScope.ForInns(inns.Split(',', StringSplitOptions.TrimEntries).Select(i => i == "-" ? "" : i));
        var r = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<ReconciliationService>().ReconcileAsync(scope, ct), CancellationToken.None);
        Print(r);
        await AttentionAsync(tenant);
    }

    /// <summary>The reconciliation attention rules after a recalculation outside a load.</summary>
    private Task AttentionAsync(Guid tenant) =>
        tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<Synect.Application.Attention.AttentionTrigger>().AfterReconciliationAsync(ct),
            CancellationToken.None);

    public async Task RebuildAsync(Guid tenant)
    {
        var r = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<ReconciliationService>().RebuildAsync(ct), CancellationToken.None);
        Print(r);
        await AttentionAsync(tenant);
    }

    /// <summary>Loads the golden set through the fake adapters, recalculates and prints precision, recall and the errors.</summary>
    public async Task<int> EvalAsync()
    {
        var (tenant, bankId, edoId) = await seeder.EnsureGoldenTenantAsync();
        var bankScenario = bank.For(bankId);
        ReconciliationGoldenSet.Apply(bankScenario, edo.For(edoId));
        var b = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<BankSyncService>()
            .SyncAsync(bankId, ReconciliationGoldenSet.BankFrom, ReconciliationGoldenSet.BankTo, ct), CancellationToken.None);
        var e = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<EdoSyncService>().SyncAsync(edoId, ct), CancellationToken.None);
        if (b.Result != SyncRunResult.Succeeded || e.Result != SyncRunResult.Succeeded)
            throw new InvalidOperationException($"Golden set load failed: bank {b.Result} {b.Error}, EDO {e.Result} {e.Error}");
        // Matches from an older rule set must not survive: Proposed matches are rebuilt (decisions, if any, stay).
        var run = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<ReconciliationService>().RebuildAsync(ct), CancellationToken.None);
        var eval = await tenants.InTenantAsync(tenant, (sp, ct) => ReconciliationGoldenSet.EvaluateAsync(sp.GetRequiredService<ISynectDbContext>(), bankScenario, ct),
            CancellationToken.None);

        Console.WriteLine($"Golden set ({tenant}), rules {run.RulesVersion}");
        Console.WriteLine($"  expected matches: {eval.Expected}, produced: {eval.Produced}, correct: {eval.Correct}");
        Console.WriteLine($"  precision: {eval.Precision:0.000}   recall: {eval.Recall:0.000}");
        Console.WriteLine($"  expected 'no automatic match': {ReconciliationGoldenSet.ExpectedNotMatched.Count} cases (" +
                          string.Join("; ", ReconciliationGoldenSet.ExpectedNotMatched.Select(n => n.Case)) + ")");
        Console.WriteLine("Produced matches:");
        foreach (var line in eval.ProducedMatches) Console.WriteLine("  " + line);
        Console.WriteLine(eval.Errors.Count == 0 ? "Errors: none" : "Errors:");
        foreach (var error in eval.Errors) Console.WriteLine("  " + error);
        return eval.Errors.Count == 0 ? 0 : 2;
    }

    /// <summary>Times a full recalculation and incremental recalculations of single INNs (the busiest ones and a typical one).</summary>
    public async Task BenchAsync(Guid tenant, int runs)
    {
        var counts = await tenants.InTenantAsync(tenant, (sp, ct) =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            return db.InTransactionAsync(async t => (
                Ops: await db.BankTransactions.CountAsync(t),
                Docs: await db.EdoDocuments.CountAsync(t),
                Busiest: await db.BankTransactions.Where(x => x.CounterpartyInn != null && x.CounterpartyInn != "")
                    .GroupBy(x => x.CounterpartyInn).Select(g => new { Inn = g.Key!, Count = g.Count() })
                    .OrderByDescending(g => g.Count).ThenBy(g => g.Inn).Take(3).ToListAsync(t)), ct);
        }, CancellationToken.None);
        Console.WriteLine($"Tenant {tenant}: {counts.Ops} bank operations, {counts.Docs} documents");

        var sw = Stopwatch.StartNew();
        var rebuild = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<ReconciliationService>().RebuildAsync(ct), CancellationToken.None);
        Console.WriteLine($"rebuild (drop Proposed + full):  {sw.Elapsed.TotalSeconds,7:F2} s  ({rebuild.Inns} INNs, {rebuild.Matches} matches, {rebuild.Findings} findings)");
        sw.Restart();
        var full = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<ReconciliationService>().ReconcileAsync(ReconciliationScope.Full, ct), CancellationToken.None);
        Console.WriteLine($"full (idempotent, nothing new): {sw.Elapsed.TotalSeconds,7:F2} s  ({full.Created} created, {full.Removed} removed, {full.Kept} kept)");

        foreach (var busy in counts.Busiest)
        {
            var times = new List<double>();
            ReconciliationRunResult? last = null;
            for (var i = 0; i < Math.Max(1, runs); i++)
            {
                sw.Restart();
                last = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<ReconciliationService>()
                    .ReconcileAsync(ReconciliationScope.ForInns([busy.Inn]), ct), CancellationToken.None);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"incremental INN {busy.Inn} ({last!.Payments} payments, {last.Documents} documents): " +
                              $"min {times[0]:F0} ms, median {times[times.Count / 2]:F0} ms, max {times[^1]:F0} ms over {times.Count} runs");
        }
    }

    /// <summary>Numeric ISO 4217 codes in already loaded data become letter codes; monthly totals and the reconciliation are refreshed.</summary>
    public async Task NormalizeCurrencyAsync(IReadOnlyList<Guid> tenantIds)
    {
        foreach (var tenant in tenantIds)
        {
            var (accounts, transactions, documents, totals) = await tenants.InTenantAsync(tenant, (sp, ct) =>
            {
                var db = sp.GetRequiredService<ISynectDbContext>();
                return db.InTransactionAsync(async t =>
                {
                    int a = 0, tx = 0, d = 0;
                    foreach (var code in IsoCurrency.KnownNumericCodes)
                    {
                        var alpha = IsoCurrency.Normalize(code);
                        a += await db.BankAccounts.Where(x => x.Currency == code).ExecuteUpdateAsync(s => s.SetProperty(x => x.Currency, alpha), t);
                        tx += await db.BankTransactions.Where(x => x.Currency == code).ExecuteUpdateAsync(s => s.SetProperty(x => x.Currency, alpha), t);
                        d += await db.EdoDocuments.Where(x => x.Currency == code).ExecuteUpdateAsync(s => s.SetProperty(x => x.Currency, alpha), t);
                    }
                    // Documents loaded before stage 5: currency and sellerAccount from the stored raw Didox JSON (rows are only
                    // rewritten when the source changes, so these new columns are filled here). Same rules as DidoxMapper:
                    // sellerAccount is confirmed by the Didox documentation; currency / currency_code are not confirmed by
                    // documentation, verify on a real response (optional, UZS when absent).
                    d += await db.Database.ExecuteSqlRawAsync("""
                        UPDATE edo_documents d SET
                            currency = CASE upper(coalesce(r.payload->>'currency', r.payload->>'currency_code', 'UZS'))
                                WHEN '000' THEN 'UZS' WHEN '860' THEN 'UZS' WHEN '840' THEN 'USD' WHEN '978' THEN 'EUR' WHEN '643' THEN 'RUB'
                                ELSE left(upper(coalesce(r.payload->>'currency', r.payload->>'currency_code', 'UZS')), 3) END,
                            seller_account = left(r.payload->>'sellerAccount', 34)
                        FROM raw_objects r
                        WHERE r.tenant_id = d.tenant_id AND r.connection_id = d.connection_id
                          AND r.object_type = 'didox.document' AND r.external_id = d.external_id
                          AND (d.seller_account IS DISTINCT FROM left(r.payload->>'sellerAccount', 34)
                               OR (r.payload ? 'currency' OR r.payload ? 'currency_code') AND d.currency = 'UZS'
                                  AND upper(coalesce(r.payload->>'currency', r.payload->>'currency_code')) NOT IN ('UZS', '000', '860'))
                        """, t);
                    var numeric = IsoCurrency.KnownNumericCodes.ToList();
                    var stale = await db.CounterpartyMonthly.Where(m => numeric.Contains(m.Currency)).ExecuteDeleteAsync(t);
                    var rebuilt = stale > 0 ? await sp.GetRequiredService<CounterpartyMonthlyRefresher>().RebuildAllAsync(t) : 0;
                    return (a, tx, d, rebuilt);
                }, ct);
            }, CancellationToken.None);
            Console.WriteLine($"{tenant}: {accounts} accounts, {transactions} operations, {documents} documents normalized or filled from raw JSON; {totals} monthly total rows rebuilt");
            if (transactions + documents > 0) await ReconcileAsync(tenant, null);
        }
    }

    private static void Print(ReconciliationRunResult r) =>
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"run {r.RunId} (rules {r.RulesVersion}): {r.Inns} INNs, {r.Payments} payments, {r.Documents} documents -> {r.Matches} proposed matches " +
            $"({r.Created} new, {r.Removed} removed, {r.Kept} kept), {r.Findings} findings, {r.Elapsed.TotalMilliseconds:F0} ms"));
}
