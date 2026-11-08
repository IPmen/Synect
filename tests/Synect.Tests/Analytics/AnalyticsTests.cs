using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Analytics;
using Synect.Application.Analytics.Banking;
using Synect.Application.Analytics.Counterparties;
using Synect.Domain.Edo;
using static Synect.Tests.Analytics.AnalyticsFixture;

namespace Synect.Tests.Analytics;

/// <summary>Correctness of every analytics method on the deterministic data set (PostgreSQL).</summary>
public class AnalyticsTests(AnalyticsFixture data) : IClassFixture<AnalyticsFixture>
{
    private static readonly DatePeriod October = new(new(2026, 10, 1), new(2026, 10, 31));
    private static readonly DatePeriod September = new(new(2026, 9, 1), new(2026, 9, 30));
    private static readonly DatePeriod November = new(new(2026, 11, 1), new(2026, 11, 30));

    private void RequirePostgres() => Assert.SkipUnless(data.Available, Postgres.PostgresFixture.SkipReason);

    private Task<AnalyticsResult<T>> A<TService, T>(Func<TService, Task<AnalyticsResult<T>>> call, Guid? tenant = null)
        where TService : notnull =>
        data.Host.InTenantAsync(tenant ?? data.TenantA, async sp =>
        {
            var result = await call(sp.GetRequiredService<TService>());
            AssertFreshness(result.Freshness);
            return result;
        });

    private static void AssertFreshness(DataFreshness freshness)
    {
        Assert.NotNull(freshness);
        Assert.Equal(2, freshness.Sources.Count); // bank + EDO connection of the tenant
        Assert.All(freshness.Sources, s => Assert.NotEqual(Guid.Empty, s.ConnectionId));
    }

    private static readonly CancellationToken None = CancellationToken.None;

    // ---------- Block A ----------

    [Fact]
    public async Task A1_current_balances_per_account_and_currency_totals()
    {
        RequirePostgres();
        var r = (await A<BalancesAnalytics, BalancesSnapshot>(s => s.GetCurrentAsync(None))).Data;

        var uzs = r.Accounts.Single(a => a.AccountId == data.UzsAccount);
        Assert.Equal(1_160_000, uzs.Balance);
        Assert.Equal(new DateOnly(2026, 10, 3), uzs.Date);
        Assert.False(uzs.IsDayClosed);
        Assert.Equal(6_500, r.Accounts.Single(a => a.AccountId == data.UsdAccount).Balance);
        Assert.Equal([new MoneyAmount("USD", 6_500), new MoneyAmount("UZS", 1_160_700)], r.TotalsByCurrency);
    }

    [Fact]
    public async Task A2_balance_on_a_date_uses_the_last_row_on_or_before_it()
    {
        RequirePostgres();
        var r = (await A<BalancesAnalytics, BalancesSnapshot>(s => s.GetOnDateAsync(Oct1, None))).Data;
        Assert.Equal(1_070_000, r.Accounts.Single(a => a.AccountId == data.UzsAccount).Balance);
        Assert.Equal(6_000, r.Accounts.Single(a => a.AccountId == data.UsdAccount).Balance);
        var idle = r.Accounts.Single(a => a.AccountId == data.IdleAccount);
        Assert.Equal(700, idle.Balance);
        Assert.Equal(new DateOnly(2025, 1, 15), idle.Date);
    }

    [Fact]
    public async Task A4_history_carries_the_last_balance_forward()
    {
        RequirePostgres();
        var period = new DatePeriod(Oct1, new(2026, 10, 5));
        var page = (await A<BalancesAnalytics, Page<DailyBalancePoint>>(s => s.GetHistoryAsync(period, data.UzsAccount, null, None))).Data;

        Assert.Equal(5, page.Items.Count);
        Assert.Null(page.NextCursor);
        Assert.Equal([1_070_000L, 1_100_000L, 1_160_000L, 1_160_000L, 1_160_000L], page.Items.Select(p => p.Balance!.Value));
        Assert.False(page.Items[2].IsCarriedForward);
        Assert.True(page.Items[4].IsCarriedForward);
        Assert.Equal(new DateOnly(2026, 10, 3), page.Items[4].SourceDate);
    }

    [Fact]
    public async Task A11_average_and_minimum_balance_per_account()
    {
        RequirePostgres();
        var period = new DatePeriod(Oct1, new(2026, 10, 3));
        var stats = (await A<BalancesAnalytics, IReadOnlyList<AccountBalanceStats>>(s => s.GetStatsAsync(period, None))).Data;

        var uzs = stats.Single(s => s.AccountId == data.UzsAccount);
        Assert.Equal(3, uzs.Days);
        Assert.Equal(1_110_000, uzs.Average);
        Assert.Equal(1_070_000, uzs.Minimum);
        Assert.Equal(Oct1, uzs.MinimumDate);
        Assert.Equal(1_160_000, uzs.Maximum);
        var usd = stats.Single(s => s.AccountId == data.UsdAccount);
        Assert.Equal("USD", usd.Currency);
        Assert.Equal(6_500, usd.Maximum); // 10-03 carried forward from 10-02
    }

    [Fact]
    public async Task A3_cash_flow_excludes_errors_and_keeps_currencies_apart()
    {
        RequirePostgres();
        var month = (await A<FlowsAnalytics, Page<CashFlowBucket>>(s => s.GetCashFlowAsync(October, Granularity.Month, null, null, None))).Data;

        var uzs = month.Items.Single(b => b.Currency == "UZS");
        Assert.Equal(new DateOnly(2026, 10, 1), uzs.BucketStart);
        Assert.Equal(225_000, uzs.Incoming);
        Assert.Equal(60_000, uzs.Outgoing); // the 999 999 operation with a bank error is excluded
        Assert.Equal(165_000, uzs.Net);
        Assert.Equal(7, uzs.Count);
        var usd = month.Items.Single(b => b.Currency == "USD");
        Assert.Equal(1_000, usd.Incoming);

        var week = (await A<FlowsAnalytics, Page<CashFlowBucket>>(s =>
            s.GetCashFlowAsync(new DatePeriod(Oct1, new(2026, 10, 3)), Granularity.Week, data.UzsAccount, null, None))).Data;
        Assert.Equal(new DateOnly(2026, 9, 28), Assert.Single(week.Items).BucketStart); // ISO week starts on Monday

        var day = (await A<FlowsAnalytics, Page<CashFlowBucket>>(s =>
            s.GetCashFlowAsync(new DatePeriod(Oct1, new(2026, 10, 3)), Granularity.Day, data.UzsAccount, null, None))).Data;
        Assert.Equal([70_000L, 30_000L, 60_000L], day.Items.Select(b => b.Net));
    }

    [Fact]
    public async Task A5_largest_operations_ordered_per_currency_without_errors()
    {
        RequirePostgres();
        var page = (await A<FlowsAnalytics, Page<BankOperation>>(s => s.GetLargestAsync(October, null, null, null, None))).Data;

        Assert.DoesNotContain(page.Items, o => o.Amount == 999_999);
        Assert.Equal("USD", page.Items[0].Currency); // currency first, then amount descending
        var uzs = page.Items.Where(o => o.Currency == "UZS").Select(o => o.Amount).ToList();
        Assert.Equal(uzs.OrderByDescending(a => a), uzs);
        Assert.Equal(100_000, uzs[0]);

        var outgoing = (await A<FlowsAnalytics, Page<BankOperation>>(s => s.GetLargestAsync(October, FlowDirection.Outgoing, "UZS", null, None))).Data;
        Assert.Equal([30_000L, 20_000L, 10_000L], outgoing.Items.Select(o => o.Amount));
    }

    [Fact]
    public async Task A6_operations_of_a_day_with_status()
    {
        RequirePostgres();
        var page = (await A<FlowsAnalytics, Page<BankOperation>>(s => s.GetDayAsync(new(2026, 10, 2), data.UzsAccount, null, None))).Data;

        Assert.Equal(["T3", "T4"], page.Items.Select(o => o.OperationId).Order());
        Assert.All(page.Items, o => Assert.True(o.IsDayClosed));
        Assert.Null(page.Items.Single(o => o.OperationId == "T4").CounterpartyInn);

        var open = (await A<FlowsAnalytics, Page<BankOperation>>(s => s.GetDayAsync(new(2026, 10, 3), null, null, None))).Data;
        Assert.All(open.Items, o => Assert.False(o.IsDayClosed));
    }

    [Fact]
    public async Task A9_accounts_without_movement_for_n_days()
    {
        RequirePostgres();
        var page = (await A<FlowsAnalytics, Page<IdleAccount>>(s => s.GetIdleAccountsAsync(30, null, None))).Data;

        Assert.DoesNotContain(page.Items, a => a.AccountId == data.UzsAccount); // last movement 2026-11-01
        var usd = page.Items.Single(a => a.AccountId == data.UsdAccount);
        Assert.Equal(Oct1, usd.LastMovementDate);
        Assert.Equal(37, usd.DaysIdle);
        var idle = page.Items.Single(a => a.AccountId == data.IdleAccount);
        Assert.Null(idle.LastMovementDate);
    }

    [Fact]
    public async Task A7_A10_balance_integrity_reports_each_kind_of_discrepancy()
    {
        RequirePostgres();
        var period = new DatePeriod(new(2025, 1, 1), October.To);
        var items = (await A<IntegrityAnalytics, Page<BalanceDiscrepancy>>(s => s.CheckBalancesAsync(period, null, null, None))).Data.Items;

        Assert.DoesNotContain(items, d => d.AccountId == data.UzsAccount); // consistent (the error operation is excluded)
        var idle = items.Where(d => d.AccountId == data.IdleAccount).ToList();
        var equation = idle.Single(d => d.Kind == BalanceDiscrepancyKind.DayEquation);
        Assert.Equal((600L, 500L), (equation.Expected, equation.Actual));
        var carry = idle.Single(d => d.Kind == BalanceDiscrepancyKind.Carryover);
        Assert.Equal((600L, 700L), (carry.Expected, carry.Actual));
        Assert.Equal(new DateOnly(2025, 1, 15), carry.NextDate);
        Assert.Equal(5, idle.Single(d => d.Kind == BalanceDiscrepancyKind.MissingDays).Actual);
        var totals = items.Single(d => d.AccountId == data.UsdAccount);
        Assert.Equal(BalanceDiscrepancyKind.TotalsMismatch, totals.Kind);
        Assert.Equal(new DateOnly(2026, 10, 2), totals.Date);
        Assert.Equal((500L, 0L), (totals.Expected, totals.Actual));
        Assert.True(totals.IsDayClosed);
    }

    // ---------- Block B ----------

    [Fact]
    public async Task B1_payments_with_a_counterparty_per_currency()
    {
        RequirePostgres();
        var r = (await A<CounterpartyAnalytics, CounterpartyPayments>(s => s.GetPaymentsAsync(Alpha, October, null, None))).Data;

        Assert.Equal(new DirectionTotal("USD", 1_000, 0, 1, 0), r.Totals.Single(t => t.Currency == "USD"));
        Assert.Equal(new DirectionTotal("UZS", 155_000, 0, 3, 0), r.Totals.Single(t => t.Currency == "UZS"));
        Assert.Equal(4, r.Operations.Items.Count);
        Assert.Equal(new DateOnly(2026, 10, 31), r.Operations.Items[0].PostingDate); // newest first

        var noInn = (await A<CounterpartyAnalytics, CounterpartyPayments>(s =>
            s.GetPaymentsAsync(CounterpartyKeys.WithoutInn, October, null, None))).Data;
        Assert.Equal(new DirectionTotal("UZS", 0, 20_000, 0, 1), Assert.Single(noInn.Totals));
    }

    [Fact]
    public async Task B2_top_counterparties_from_monthly_totals_with_payments_without_inn_apart()
    {
        RequirePostgres();
        var incoming = (await A<CounterpartyAnalytics, IReadOnlyList<TopCounterparties>>(s => s.GetTopAsync(October, FlowDirection.Incoming, 5, None))).Data;
        var uzs = incoming.Single(t => t.Currency == "UZS");
        Assert.Equal([(Alpha, 155_000L), (Gamma, 70_000L)], uzs.Top.Select(t => (t.Inn, t.Amount)));
        Assert.Equal("Alpha LLC", uzs.Top[0].Name);
        Assert.Equal((Alpha, 1_000L), (incoming.Single(t => t.Currency == "USD").Top[0].Inn, incoming.Single(t => t.Currency == "USD").Top[0].Amount));

        var outgoing = (await A<CounterpartyAnalytics, IReadOnlyList<TopCounterparties>>(s => s.GetTopAsync(October, FlowDirection.Outgoing, 5, None))).Data
            .Single(t => t.Currency == "UZS");
        Assert.Equal([(Beta, 30_000L), (Delta, 10_000L)], outgoing.Top.Select(t => (t.Inn, t.Amount)));
        Assert.Equal(20_000, outgoing.WithoutInnAmount);
        Assert.Equal(1, outgoing.WithoutInnCount);
        Assert.Equal(60_000, outgoing.Total);
    }

    [Fact]
    public async Task B3_B4_counterparty_summary_with_documents_payments_and_difference()
    {
        RequirePostgres();
        var r = (await A<CounterpartyAnalytics, CounterpartySummary>(s => s.GetSummaryAsync(Alpha, October, DateBasis.DocumentDate, None))).Data;

        Assert.Equal(new EdoOwnerTotals(2, 211_000, 22_606), r.OutgoingDocuments); // E3 + E6 (E2 is in September)
        Assert.Equal(new EdoOwnerTotals(0, 0, 0), r.IncomingDocuments);
        Assert.Equal(155_000, r.Payments.Single(p => p.Currency == "UZS").Incoming);
        Assert.Equal(56_000, r.Receivable);
        Assert.Equal(0, r.Payable);
        Assert.Equal("Alpha LLC", r.Name);
    }

    [Fact]
    public async Task B5_turnover_in_two_periods()
    {
        RequirePostgres();
        var rows = (await A<CounterpartyAnalytics, Page<TurnoverComparison>>(s => s.CompareAsync(September, October, null, null, None))).Data.Items;

        var alpha = rows.Single(r => r.Inn == Alpha && r.Currency == "UZS");
        Assert.Equal((0L, 155_000L, 155_000L), (alpha.IncomingA, alpha.IncomingB, alpha.IncomingDelta));
        var epsilon = rows.Single(r => r.Inn == Epsilon);
        Assert.Equal((40_000L, 0L), (epsilon.IncomingA, epsilon.IncomingB));
        Assert.Contains(rows, r => r.Inn == Alpha && r.Currency == "USD");
    }

    [Fact]
    public async Task B6_new_and_lost_counterparties_flag_history_depth()
    {
        RequirePostgres();
        var added = (await A<CounterpartyAnalytics, CounterpartyChanges>(s => s.GetChangesAsync(October, CounterpartyChangeKind.New, null, None))).Data;
        Assert.Equal([Beta, Gamma, Delta], added.Counterparties.Items.Select(c => c.Inn));
        Assert.Equal(new DateOnly(2026, 9, 1), added.HistoryStartsAt);
        Assert.False(added.LimitedByHistory);

        var lost = (await A<CounterpartyAnalytics, CounterpartyChanges>(s => s.GetChangesAsync(October, CounterpartyChangeKind.Lost, null, None))).Data;
        Assert.Equal([Epsilon], lost.Counterparties.Items.Select(c => c.Inn));
        Assert.True(lost.LimitedByHistory); // the previous period starts in August, before the loaded history
    }

    [Fact]
    public async Task B8_payments_to_counterparties_without_documents()
    {
        RequirePostgres();
        var items = (await A<CounterpartyAnalytics, Page<UndocumentedPayments>>(s => s.GetPaymentsWithoutDocumentsAsync(October, null, None))).Data.Items;
        var delta = Assert.Single(items); // Beta has a counted document; the fee has no INN
        Assert.Equal((Delta, "UZS", 10_000L, 1), (delta.Inn, delta.Currency, delta.Amount, delta.Count));
    }

    [Fact]
    public async Task B9_concentration_per_currency()
    {
        RequirePostgres();
        var rows = (await A<CounterpartyAnalytics, IReadOnlyList<Concentration>>(s => s.GetConcentrationAsync(October, FlowDirection.Incoming, None))).Data;
        var uzs = rows.Single(r => r.Currency == "UZS");
        Assert.Equal(225_000, uzs.Total);
        Assert.Equal(Alpha, uzs.TopInn);
        Assert.Equal(0.6889m, uzs.TopShare);
        Assert.Equal(1m, uzs.Top3Share);
        Assert.Equal(1m, rows.Single(r => r.Currency == "USD").TopShare);
    }

    // ---------- Block V ----------

    [Fact]
    public async Task V1_V2_documents_waiting_for_signatures_with_age()
    {
        RequirePostgres();
        var ours = (await A<DocumentsAnalytics, Page<EdoDocumentItem>>(s => s.GetAwaitingOurSignatureAsync(null, None))).Data.Items;
        var e1 = Assert.Single(ours);
        Assert.Equal(("E1", 36), (e1.ExternalId, e1.AgeDays!.Value));

        var partner = (await A<DocumentsAnalytics, Page<EdoDocumentItem>>(s => s.GetAwaitingPartnerSignatureAsync(null, None))).Data.Items;
        var e2 = Assert.Single(partner);
        Assert.Equal(("E2", 48), (e2.ExternalId, e2.AgeDays!.Value));
        Assert.Equal("awaiting partner signature", e2.StatusName);
    }

    [Fact]
    public async Task V3_counts_by_status_type_and_owner()
    {
        RequirePostgres();
        var counts = (await A<DocumentsAnalytics, IReadOnlyList<DocumentCount>>(s => s.GetCountsAsync(October, DateBasis.DocumentDate, None))).Data;
        Assert.Equal(4, counts.Count);
        var signed = counts.Single(c => c.Owner == EdoDocumentOwner.Outgoing && c.Status == "3");
        Assert.Equal((2, 211_000L, "002"), (signed.Count, signed.TotalSum, signed.DocType));
        Assert.Equal(1, counts.Single(c => c.Status == "0").Count);
    }

    [Fact]
    public async Task V4_rejected_documents_with_comment()
    {
        RequirePostgres();
        var e4 = Assert.Single((await A<DocumentsAnalytics, Page<EdoDocumentItem>>(s => s.GetRejectedAsync(October, DateBasis.DocumentDate, null, None))).Data.Items);
        Assert.Equal(("E4", "Wrong amount", Gamma), (e4.ExternalId, e4.StatusComment, e4.CounterpartyInn));
    }

    [Fact]
    public async Task V5_search_by_number_inn_contract_type_status()
    {
        RequirePostgres();
        async Task<string[]> Find(DocumentSearch f) =>
            (await A<DocumentsAnalytics, Page<EdoDocumentItem>>(s => s.SearchAsync(f, null, None))).Data.Items.Select(d => d.ExternalId).Order().ToArray();

        Assert.Equal(["E2", "E3", "E4", "E6"], await Find(new DocumentSearch(Number: "INV-00")));
        Assert.Equal(["E3"], await Find(new DocumentSearch(Number: "INV-002")));
        Assert.Equal(["E2", "E3", "E6"], await Find(new DocumentSearch(CounterpartyInn: Alpha)));
        Assert.Equal(["E2", "E3"], await Find(new DocumentSearch(ContractNumber: "C-1")));
        Assert.Equal(["E1", "E5"], await Find(new DocumentSearch(DocType: "005")));
        Assert.Equal(["E3", "E6"], await Find(new DocumentSearch(Status: "3", Owner: EdoDocumentOwner.Outgoing)));
        Assert.Empty(await Find(new DocumentSearch(Number: "INV%"))); // wildcards are literal
    }

    [Fact]
    public async Task V6_largest_documents_only_counted_statuses()
    {
        RequirePostgres();
        var items = (await A<DocumentsAnalytics, Page<EdoDocumentItem>>(s => s.GetLargestAsync(October, DateBasis.DocumentDate, null, null, None))).Data.Items;
        Assert.Equal(["E3", "E1", "E6"], items.Select(d => d.ExternalId)); // E4 rejected and E5 draft are excluded
    }

    [Fact]
    public async Task V7_vat_and_totals_per_owner()
    {
        RequirePostgres();
        var rows = (await A<DocumentsAnalytics, IReadOnlyList<VatSummary>>(s => s.GetVatSummaryAsync(October, DateBasis.DocumentDate, None))).Data;
        Assert.Equal(new VatSummary(EdoDocumentOwner.Incoming, "UZS", 1, 30_000, 3_214, 26_786), rows.Single(r => r.Owner == EdoDocumentOwner.Incoming));
        Assert.Equal(new VatSummary(EdoDocumentOwner.Outgoing, "UZS", 2, 211_000, 22_606, 188_394), rows.Single(r => r.Owner == EdoDocumentOwner.Outgoing));
    }

    [Fact]
    public async Task Periods_are_local_dates_in_utc_plus_5()
    {
        RequirePostgres();
        // E6 was created at 2026-10-31 20:00 UTC = 2026-11-01 01:00 in Tashkent.
        var byCreated = (await A<DocumentsAnalytics, IReadOnlyList<VatSummary>>(s => s.GetVatSummaryAsync(November, DateBasis.SourceCreated, None))).Data;
        Assert.Equal(1, byCreated.Single(r => r.Owner == EdoDocumentOwner.Outgoing).Count);
        var byDocumentDate = (await A<DocumentsAnalytics, IReadOnlyList<VatSummary>>(s => s.GetVatSummaryAsync(November, DateBasis.DocumentDate, None))).Data;
        Assert.Equal(0, byDocumentDate.Single(r => r.Owner == EdoDocumentOwner.Outgoing).Count);
        var octoberCreated = (await A<DocumentsAnalytics, IReadOnlyList<VatSummary>>(s => s.GetVatSummaryAsync(October, DateBasis.SourceCreated, None))).Data;
        Assert.Equal(1, octoberCreated.Single(r => r.Owner == EdoDocumentOwner.Outgoing).Count); // only E3
    }

    // ---------- Cross-cutting ----------

    [Fact]
    public async Task Pagination_has_no_gaps_and_no_duplicates()
    {
        RequirePostgres();
        var all = (await A<FlowsAnalytics, Page<BankOperation>>(s => s.GetLargestAsync(October, null, null, new PageRequest(1000), None))).Data.Items;
        var paged = new List<BankOperation>();
        string? cursor = null;
        do
        {
            var page = (await A<FlowsAnalytics, Page<BankOperation>>(s => s.GetLargestAsync(October, null, null, new PageRequest(2, cursor), None))).Data;
            Assert.True(page.Items.Count <= 2);
            paged.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(all.Select(o => o.Id), paged.Select(o => o.Id));

        var docsAll = (await A<DocumentsAnalytics, Page<EdoDocumentItem>>(s => s.SearchAsync(new DocumentSearch(), null, None))).Data.Items;
        var docsPaged = new List<EdoDocumentItem>();
        cursor = null;
        do
        {
            var page = (await A<DocumentsAnalytics, Page<EdoDocumentItem>>(s => s.SearchAsync(new DocumentSearch(), new PageRequest(1, cursor), None))).Data;
            docsPaged.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(6, docsPaged.Count);
        Assert.Equal(docsAll.Select(d => d.Id), docsPaged.Select(d => d.Id));

        var history = new List<DailyBalancePoint>();
        cursor = null;
        do
        {
            var page = (await A<BalancesAnalytics, Page<DailyBalancePoint>>(s => s.GetHistoryAsync(October, null, new PageRequest(7, cursor), None))).Data;
            history.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(3 * 31, history.Count);
        Assert.Equal(history.Count, history.Select(h => (h.AccountId, h.Date)).Distinct().Count());
    }

    [Fact]
    public async Task Page_size_is_capped_and_bad_cursors_are_rejected()
    {
        RequirePostgres();
        var page = (await A<BalancesAnalytics, Page<DailyBalancePoint>>(s =>
            s.GetHistoryAsync(new DatePeriod(new(2020, 1, 1), new(2026, 10, 31)), null, new PageRequest(50_000), None))).Data;
        Assert.Equal(1000, page.Items.Count);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            A<FlowsAnalytics, Page<BankOperation>>(s => s.GetLargestAsync(October, null, null, new PageRequest(2, "not-a-cursor"), None)));
    }

    [Fact]
    public async Task Tenant_B_sees_only_its_own_data_through_every_service()
    {
        RequirePostgres();
        var b = data.TenantB;
        var balances = (await A<BalancesAnalytics, BalancesSnapshot>(s => s.GetCurrentAsync(None), b)).Data;
        Assert.Equal([new MoneyAmount("UZS", 10_000)], balances.TotalsByCurrency);

        var payments = (await A<CounterpartyAnalytics, CounterpartyPayments>(s => s.GetPaymentsAsync(Alpha, October, null, None), b)).Data;
        Assert.Equal(9_999, Assert.Single(payments.Totals).Incoming);

        var top = (await A<CounterpartyAnalytics, IReadOnlyList<TopCounterparties>>(s => s.GetTopAsync(October, FlowDirection.Incoming, 5, None), b)).Data;
        Assert.Equal(9_999, Assert.Single(Assert.Single(top).Top).Amount);

        var docs = (await A<DocumentsAnalytics, Page<EdoDocumentItem>>(s => s.SearchAsync(new DocumentSearch(), null, None), b)).Data.Items;
        Assert.Equal(["BE1"], docs.Select(d => d.ExternalId));

        var vat = (await A<DocumentsAnalytics, IReadOnlyList<VatSummary>>(s => s.GetVatSummaryAsync(October, DateBasis.DocumentDate, None), b)).Data;
        Assert.Equal(77_000, vat.Single(v => v.Owner == EdoDocumentOwner.Outgoing).TotalSum);

        var issues = (await A<IntegrityAnalytics, Page<BalanceDiscrepancy>>(s =>
            s.CheckBalancesAsync(new DatePeriod(new(2025, 1, 1), October.To), null, null, None), b)).Data.Items;
        Assert.Empty(issues);
    }

    [Fact]
    public async Task Analytics_require_a_tenant_context()
    {
        RequirePostgres();
        await using var scope = data.Host.Scope(null);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<BalancesAnalytics>().GetCurrentAsync(None));
    }
}
