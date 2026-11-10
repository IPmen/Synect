using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Domain.Reconciliation;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.Reconciliation;

/// <summary>Loads the golden set through the fake adapters and the real sync services and returns the host and tenant.</summary>
internal static class GoldenSetLoader
{
    public static async Task<(Guid Tenant, Guid Bank, Guid Edo)> LoadAsync(TestHost host, string tenantName = ReconciliationGoldenSet.TenantName)
    {
        var tenant = await host.CreateTenantAsync(tenantName);
        var bank = await host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        var edo = await host.CreateConnectionAsync(tenant, AdapterKind.Edo);
        ReconciliationGoldenSet.Apply(host.FakeBank.For(bank), host.FakeEdo.For(edo));
        var b = await host.SyncBankAsync(tenant, bank, ReconciliationGoldenSet.BankFrom, ReconciliationGoldenSet.BankTo);
        Assert.Equal(SyncRunResult.Succeeded, b.Result);
        var e = await host.SyncEdoAsync(tenant, edo);
        Assert.Equal(SyncRunResult.Succeeded, e.Result);
        return (tenant, bank, edo);
    }

    public static Task<GoldenEvaluation> EvaluateAsync(TestHost host, Guid tenant, Guid bank) =>
        host.InTenantAsync(tenant, sp => ReconciliationGoldenSet.EvaluateAsync(sp.GetRequiredService<ISynectDbContext>(), host.FakeBank.For(bank), CancellationToken.None));
}

/// <summary>The golden set: every expected match is found and nothing else is matched automatically (SQLite).</summary>
public class GoldenSetTests
{
    [Fact]
    public async Task Golden_set_has_full_precision_and_recall()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var (tenant, bank, _) = await GoldenSetLoader.LoadAsync(host);
        var eval = await GoldenSetLoader.EvaluateAsync(host, tenant, bank);

        Assert.True(eval.Errors.Count == 0, string.Join("\n", eval.Errors.Concat(["-- produced:"]).Concat(eval.ProducedMatches)));
        Assert.Equal(ReconciliationGoldenSet.ExpectedMatches.Count, eval.Correct);
        Assert.Equal(1.0, eval.Precision);
        Assert.Equal(1.0, eval.Recall);
    }

    [Fact]
    public async Task Golden_set_findings_cover_duplicates_hints_and_missing_references()
    {
        await using var host = await TestHost.CreateSqliteAsync();
        var (tenant, bank, _) = await GoldenSetLoader.LoadAsync(host);
        var scenario = host.FakeBank.For(bank);
        var findings = await host.QueryAsync(tenant, db => db.ReconciliationFindings.AsNoTracking().ToListAsync());
        var payments = await host.QueryAsync(tenant, db => db.BankTransactions.AsNoTracking().ToDictionaryAsync(t => t.OperationId, t => t.Id));
        var docs = await host.QueryAsync(tenant, db => db.EdoDocuments.AsNoTracking().ToDictionaryAsync(d => d.ExternalId, d => d.Id));
        Guid P(string key) => payments[scenario.OperationIdOf(key)];

        // The second payment of the same invoice is a possible duplicate of the first; instalments are not.
        var duplicate = Assert.Single(findings, f => f.Kind == FindingKind.PossibleDuplicate && f.SubjectId == P("p10b"));
        Assert.Equal(P("p10a"), duplicate.RelatedId);
        Assert.Equal(90, duplicate.Confidence);
        Assert.DoesNotContain(findings, f => f.Kind == FindingKind.PossibleDuplicate && f.SubjectId == P("p6b"));

        // The ambiguous payment keeps a hint to one of the two equal documents.
        var hint = Assert.Single(findings, f => f.Kind == FindingKind.NearestCandidate && f.SubjectId == P("p17"));
        Assert.Contains(hint.RelatedId!.Value, new[] { docs["d17a"], docs["d17b"] });
        Assert.True(hint.Confidence < 70);

        // Purposes without a document or contract number.
        Assert.Contains(findings, f => f.Kind == FindingKind.NoReference && f.SubjectId == P("p4"));
        Assert.Contains(findings, f => f.Kind == FindingKind.NoReference && f.SubjectId == P("p17"));
        Assert.DoesNotContain(findings, f => f.Kind == FindingKind.NoReference && f.SubjectId == P("p1"));
    }
}
