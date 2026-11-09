using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Analytics;
using Synect.Application.Analytics.Banking;
using Synect.Application.Analytics.Counterparties;
using Synect.Application.Queue;
using Synect.Domain.Edo;

namespace Synect.DevTools;

/// <param name="Category">list (target p95 0.3 s), aggregate (1 s) or heavy (3 s).</param>
public sealed record QueryCase(string Name, string Category, string Question, Func<IServiceProvider, CliArgs, DateOnly, CancellationToken, Task<object>> Run);

/// <summary>Every analytics method from part 3.1 under a CLI name, with sensible default arguments.</summary>
public sealed class QueryCatalog(TenantScopeRunner tenants, TimeProvider time)
{
    public const string DefaultInn = "300000007";

    private static readonly TimeSpan Tashkent = TimeSpan.FromHours(5);

    public IReadOnlyList<QueryCase> All { get; } =
    [
        new("a1-balances-current", "aggregate", "A1", async (sp, a, today, ct) =>
            await sp.GetRequiredService<BalancesAnalytics>().GetCurrentAsync(ct)),
        new("a2-balance-on-date", "aggregate", "A2", async (sp, a, today, ct) =>
            await sp.GetRequiredService<BalancesAnalytics>().GetOnDateAsync(a.Date("date", today.AddDays(-30)), ct)),
        new("a4-balance-history", "aggregate", "A4", async (sp, a, today, ct) =>
            await sp.GetRequiredService<BalancesAnalytics>().GetHistoryAsync(a.Period(today, 365), null, a.Page(), ct)),
        new("a11-balance-stats", "aggregate", "A11", async (sp, a, today, ct) =>
            await sp.GetRequiredService<BalancesAnalytics>().GetStatsAsync(a.Period(today, 365), ct)),
        new("a3-cash-flow", "aggregate", "A3", async (sp, a, today, ct) =>
            await sp.GetRequiredService<FlowsAnalytics>().GetCashFlowAsync(a.Period(today, 365), a.Enum("granularity", Granularity.Month), null, a.Page(), ct)),
        new("a3-cash-flow-daily-3y", "heavy", "A3", async (sp, a, today, ct) =>
            await sp.GetRequiredService<FlowsAnalytics>().GetCashFlowAsync(a.Period(today, 1096), Granularity.Day, null, new PageRequest(1000), ct)),
        new("a5-largest-operations", "list", "A5", async (sp, a, today, ct) =>
            await sp.GetRequiredService<FlowsAnalytics>().GetLargestAsync(a.Period(today, 30), null, a.Get("currency"), a.Page(), ct)),
        new("a5-largest-operations-year", "aggregate", "A5", async (sp, a, today, ct) =>
            await sp.GetRequiredService<FlowsAnalytics>().GetLargestAsync(a.Period(today, 365), null, null, a.Page(), ct)),
        new("a6-day-operations", "list", "A6", async (sp, a, today, ct) =>
            await sp.GetRequiredService<FlowsAnalytics>().GetDayAsync(a.Date("date", today.AddDays(-1)), null, a.Page(), ct)),
        new("a9-idle-accounts", "list", "A9", async (sp, a, today, ct) =>
            await sp.GetRequiredService<FlowsAnalytics>().GetIdleAccountsAsync(a.Int("days", 30), a.Page(), ct)),
        new("a7-balance-integrity", "heavy", "A7/A10", async (sp, a, today, ct) =>
            await sp.GetRequiredService<IntegrityAnalytics>().CheckBalancesAsync(a.Period(today, 1096), null, a.Page(), ct)),
        new("s1-find-counterparties", "list", "Search by name", async (sp, a, today, ct) =>
            await sp.GetRequiredService<CounterpartySearch>().FindAsync(a.Get("q") ?? "partner 01", new PageRequest(a.Int("limit", 10)), null, ct)),
        new("s1-find-counterparties-typo", "list", "Search with a typo", async (sp, a, today, ct) =>
            await sp.GetRequiredService<CounterpartySearch>().FindAsync(a.Get("q2") ?? "partnr", new PageRequest(10), null, ct)),
        new("s1-find-counterparties-inn", "list", "Search by INN", async (sp, a, today, ct) =>
            await sp.GetRequiredService<CounterpartySearch>().FindAsync(a.Get("inn") ?? DefaultInn, new PageRequest(10), null, ct)),
        new("b1-counterparty-payments", "list", "B1", async (sp, a, today, ct) =>
            await sp.GetRequiredService<CounterpartyAnalytics>().GetPaymentsAsync(a.Get("inn") ?? DefaultInn, a.Period(today, 365), a.Page(), ct)),
        new("b2-top-counterparties", "aggregate", "B2", async (sp, a, today, ct) =>
            await sp.GetRequiredService<CounterpartyAnalytics>().GetTopAsync(a.Period(today, 365), a.Enum("direction", FlowDirection.Incoming), a.Int("top", 10), ct)),
        new("b3-counterparty-summary", "aggregate", "B3/B4", async (sp, a, today, ct) =>
            await sp.GetRequiredService<CounterpartyAnalytics>().GetSummaryAsync(a.Get("inn") ?? DefaultInn, a.Period(today, 365), a.Enum("basis", DateBasis.DocumentDate), ct)),
        new("b5-turnover-compare", "aggregate", "B5", async (sp, a, today, ct) =>
        {
            var b = a.Period(today, 365);
            return await sp.GetRequiredService<CounterpartyAnalytics>().CompareAsync(b.Previous(), b, a.Get("inn"), a.Page(), ct);
        }),
        new("b6-new-counterparties", "heavy", "B6", async (sp, a, today, ct) =>
            await sp.GetRequiredService<CounterpartyAnalytics>().GetChangesAsync(a.Period(today, 90), CounterpartyChangeKind.New, a.Page(), ct)),
        new("b6-lost-counterparties", "heavy", "B6", async (sp, a, today, ct) =>
            await sp.GetRequiredService<CounterpartyAnalytics>().GetChangesAsync(a.Period(today, 90), CounterpartyChangeKind.Lost, a.Page(), ct)),
        new("b8-payments-without-documents", "heavy", "B8", async (sp, a, today, ct) =>
            await sp.GetRequiredService<CounterpartyAnalytics>().GetPaymentsWithoutDocumentsAsync(a.Period(today, 365), a.Page(), ct)),
        new("b9-concentration", "aggregate", "B9", async (sp, a, today, ct) =>
            await sp.GetRequiredService<CounterpartyAnalytics>().GetConcentrationAsync(a.Period(today, 365), a.Enum("direction", FlowDirection.Incoming), ct)),
        new("v1-awaiting-our-signature", "list", "V1", async (sp, a, today, ct) =>
            await sp.GetRequiredService<DocumentsAnalytics>().GetAwaitingOurSignatureAsync(a.Page(), ct)),
        new("v2-awaiting-partner-signature", "list", "V2", async (sp, a, today, ct) =>
            await sp.GetRequiredService<DocumentsAnalytics>().GetAwaitingPartnerSignatureAsync(a.Page(), ct)),
        new("v3-document-counts", "aggregate", "V3", async (sp, a, today, ct) =>
            await sp.GetRequiredService<DocumentsAnalytics>().GetCountsAsync(a.Period(today, 365), a.Enum("basis", DateBasis.DocumentDate), ct)),
        new("v4-rejected-documents", "list", "V4", async (sp, a, today, ct) =>
            await sp.GetRequiredService<DocumentsAnalytics>().GetRejectedAsync(a.Period(today, 30), a.Enum("basis", DateBasis.DocumentDate), a.Page(), ct)),
        new("v5-search-by-number", "list", "V5", async (sp, a, today, ct) =>
            await sp.GetRequiredService<DocumentsAnalytics>().SearchAsync(new DocumentSearch(Number: a.Get("number") ?? "0042"), a.Page(), ct)),
        new("v5-search-by-inn", "list", "V5", async (sp, a, today, ct) =>
            await sp.GetRequiredService<DocumentsAnalytics>().SearchAsync(new DocumentSearch(CounterpartyInn: a.Get("inn") ?? DefaultInn,
                ContractNumber: a.Get("contract"), DocType: a.Get("type"), Status: a.Get("status")), a.Page(), ct)),
        new("v6-largest-documents", "list", "V6", async (sp, a, today, ct) =>
            await sp.GetRequiredService<DocumentsAnalytics>().GetLargestAsync(a.Period(today, 30), a.Enum("basis", DateBasis.DocumentDate),
                a.Get("owner") is { } o ? Enum.Parse<EdoDocumentOwner>(o, true) : null, a.Page(), ct)),
        new("v7-vat-summary", "aggregate", "V7", async (sp, a, today, ct) =>
            await sp.GetRequiredService<DocumentsAnalytics>().GetVatSummaryAsync(a.Period(today, 365), a.Enum("basis", DateBasis.DocumentDate), ct)),
    ];

    public DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().ToOffset(Tashkent).DateTime);

    public QueryCase Find(string name) =>
        All.SingleOrDefault(q => q.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Unknown query '{name}'. Use list-queries.");

    public Task<object> RunAsync(string name, Guid tenant, CliArgs args, CancellationToken ct)
    {
        var query = Find(name);
        return tenants.InTenantAsync(tenant, (sp, token) => query.Run(sp, args, Today, token), ct);
    }
}
