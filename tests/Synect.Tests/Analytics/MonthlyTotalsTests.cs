using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Analytics;
using Synect.Application.Analytics.Banking;
using Synect.Application.Analytics.Totals;
using Synect.Domain.Analytics;
using Synect.Domain.Sync;
using Synect.Tests.Infrastructure;
using static Synect.Tests.Infrastructure.TestData;

namespace Synect.Tests.Analytics;

/// <summary>analytics_counterparty_monthly is refreshed by every successful load, only for what the load touched.</summary>
public class MonthlyTotalsTests : IAsyncLifetime
{
    private static readonly DateOnly Sep28 = new(2026, 9, 28);
    private static readonly DateOnly Oct3 = new(2026, 10, 3);

    private TestHost _host = null!;
    private Guid _tenant;
    private Guid _bank;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.CreateSqliteAsync();
        _tenant = await _host.CreateTenantAsync("T");
        _bank = await _host.CreateConnectionAsync(_tenant, AdapterKind.Bank);
        _host.FakeBank.For(_bank).CounterpartyPoolSize = 5;
        _host.FakeBank.For(_bank).NoInnShare = 0.2;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private Task<List<CounterpartyMonthly>> RowsAsync() =>
        _host.QueryAsync(_tenant, db => db.CounterpartyMonthly.AsNoTracking()
            .OrderBy(r => r.Month).ThenBy(r => r.CounterpartyInn).ThenBy(r => r.Currency).ToListAsync());

    [Fact]
    public async Task Totals_match_the_source_operations_after_a_load()
    {
        Assert.Equal(SyncRunResult.Succeeded, (await _host.SyncBankAsync(_tenant, _bank, Sep28, Oct3)).Result);

        var rows = await RowsAsync();
        Assert.NotEmpty(rows);
        Assert.Equal([new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)], rows.Select(r => r.Month).Distinct());
        Assert.Contains(rows, r => r.CounterpartyInn == ""); // payments without INN are their own group

        var ops = await _host.QueryAsync(_tenant, db => db.BankTransactions.AsNoTracking().ToListAsync());
        foreach (var row in rows)
        {
            var expected = ops.Where(o => !o.HasError && (o.CounterpartyInn ?? "") == row.CounterpartyInn
                && o.PostingDate.Year == row.Month.Year && o.PostingDate.Month == row.Month.Month && o.Currency == row.Currency).ToList();
            Assert.Equal(expected.Where(o => o.SignedAmount > 0).Sum(o => o.Amount), row.BankIncoming);
            Assert.Equal(expected.Where(o => o.SignedAmount < 0).Sum(o => o.Amount), row.BankOutgoing);
            Assert.Equal(expected.Count, row.BankCount);
        }
    }

    [Fact]
    public async Task Refresh_is_idempotent_and_touches_only_changed_months()
    {
        Assert.Equal(SyncRunResult.Succeeded, (await _host.SyncBankAsync(_tenant, _bank, Sep28, Oct3)).Result);
        var before = await RowsAsync();

        // Same data again: nothing changes.
        _host.Time.Advance(TimeSpan.FromHours(1));
        await _host.SyncBankAsync(_tenant, _bank, Sep28, Oct3);
        var again = await RowsAsync();
        Assert.Equal(before.Select(r => (r.Id, r.UpdatedAt, r.BankIncoming, r.BankOutgoing)),
                     again.Select(r => (r.Id, r.UpdatedAt, r.BankIncoming, r.BankOutgoing)));

        // A late operation appears on 2 October: only that month and INN are recomputed.
        _host.Time.Advance(TimeSpan.FromHours(1));
        _host.FakeBank.For(_bank).ExtraDays.Add(new DateOnly(2026, 10, 2));
        await _host.SyncBankAsync(_tenant, _bank, Sep28, Oct3);
        var after = await RowsAsync();

        var late = after.Single(r => r.CounterpartyInn == "309999999");
        Assert.Equal((new DateOnly(2026, 10, 1), 1_234_00L, 1), (late.Month, late.BankIncoming, late.BankCount));
        Assert.Equal(_host.Time.Now, late.UpdatedAt);
        var untouched = after.Where(r => r.CounterpartyInn != "309999999").ToList();
        Assert.Equal(before.Select(r => (r.Id, r.UpdatedAt)), untouched.Select(r => (r.Id, r.UpdatedAt)));

        // A full rebuild agrees with the incremental result.
        var changed = await _host.InTenantAsync(_tenant, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            return await db.InTransactionAsync(ct => sp.GetRequiredService<CounterpartyMonthlyRefresher>().RebuildAllAsync(ct), CancellationToken.None);
        });
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Edo_documents_feed_the_totals_with_counted_statuses_only()
    {
        var edo = await _host.CreateConnectionAsync(_tenant, AdapterKind.Edo);
        await _host.SyncEdoAsync(_tenant, edo);

        var rows = await RowsAsync();
        var docs = await _host.QueryAsync(_tenant, db => db.EdoDocuments.AsNoTracking().ToListAsync());
        var counted = new AnalyticsOptions().Edo.CountedStatuses;
        var expectedOutgoing = docs.Where(d => d.Owner == Domain.Edo.EdoDocumentOwner.Outgoing && counted.Contains(d.Status)).Sum(d => d.TotalSum);
        Assert.Equal(expectedOutgoing, rows.Sum(r => r.EdoOutgoingTotal));
        Assert.All(rows, r => Assert.Equal("UZS", r.Currency));

        // A status change to "rejected" removes the document from the totals of its month only.
        var doc = docs.First(d => counted.Contains(d.Status) && d.Owner == Domain.Edo.EdoDocumentOwner.Outgoing);
        _host.Time.Advance(TimeSpan.FromDays(1));
        _host.FakeEdo.SetStatus(edo, doc.ExternalId, 4, _host.Time.Now);
        await _host.SyncEdoAsync(_tenant, edo);
        Assert.Equal(expectedOutgoing - doc.TotalSum, (await RowsAsync()).Sum(r => r.EdoOutgoingTotal));
    }
}

/// <summary>Analytics only reads the database: no dependency on adapters, sync, queue, secrets or external systems.</summary>
public class AnalyticsIsolationFromSourcesTests
{
    private static readonly string[] ForbiddenNamespaces =
    [
        "Synect.Application.Adapters", "Synect.Application.Sync", "Synect.Application.Queue",
        "Synect.Application.Resilience", "Synect.Application.Secrets", "Synect.Infrastructure",
    ];

    [Fact]
    public void Analytics_types_do_not_reference_adapters_or_external_systems()
    {
        var assembly = typeof(AnalyticsContext).Assembly;
        var analyticsTypes = assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Synect.Application.Analytics", StringComparison.Ordinal) == true).ToList();
        Assert.Contains(typeof(BalancesAnalytics), analyticsTypes);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var violations = new List<string>();
        foreach (var type in analyticsTypes)
        {
            var referenced = new List<Type>();
            referenced.AddRange(type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));
            referenced.AddRange(type.GetFields(all).Select(f => f.FieldType));
            referenced.AddRange(type.GetProperties(all).Select(p => p.PropertyType));
            foreach (var method in type.GetMethods(all))
            {
                referenced.Add(method.ReturnType);
                referenced.AddRange(method.GetParameters().Select(p => p.ParameterType));
                referenced.AddRange(method.GetMethodBody()?.LocalVariables.Select(v => v.LocalType) ?? []);
            }
            foreach (var t in referenced.SelectMany(Expand))
            {
                if (ForbiddenNamespaces.Any(ns => t.Namespace?.StartsWith(ns, StringComparison.Ordinal) == true))
                    violations.Add($"{type.FullName} -> {t.FullName}");
            }
        }
        Assert.Empty(violations);
    }

    private static IEnumerable<Type> Expand(Type t)
    {
        yield return t;
        if (t.HasElementType) foreach (var e in Expand(t.GetElementType()!)) yield return e;
        if (t.IsGenericType) foreach (var g in t.GetGenericArguments().SelectMany(Expand)) yield return g;
    }
}
