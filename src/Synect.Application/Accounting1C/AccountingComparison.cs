using Microsoft.Extensions.Options;
using Synect.Application.Analytics;
using Synect.Application.Reconciliation;

namespace Synect.Application.Accounting1C;

/// <summary>A bank payment as one side of a comparison.</summary>
public sealed record ComparedBankPayment(
    Guid Id, DateOnly Date, string Direction, long Amount, string Currency, string? CounterpartyInn, string? CounterpartyName,
    string? Purpose, string? DocumentNumber, string? AccountNumber);

/// <summary>A 1C bank document as one side of a comparison.</summary>
public sealed record ComparedAccountingPayment(
    string Ref, string Number, DateOnly Date, string Direction, long Amount, string Currency, string? CounterpartyTin, bool Posted,
    string? Purpose, string? BankAccountNumber);

/// <summary>A Didox document as one side of a comparison.</summary>
public sealed record ComparedDidoxDocument(
    Guid Id, string ExternalId, string? Number, DateOnly Date, string Owner, string DocType, string Status, string? CounterpartyInn,
    string? CounterpartyName, long Total, string Currency);

/// <summary>A 1C sales or purchase document as one side of a comparison.</summary>
public sealed record ComparedAccountingDocument(
    string Ref, string Number, string? EsfNumber, DateOnly Date, string DocKind, long Total, string Currency, string? CounterpartyTin, bool Posted);

/// <param name="MatchedBy">How the two sides were paired (null for one-sided rows).</param>
/// <param name="AmountDifference">1C amount minus the other side's amount, for "amount differs" rows.</param>
public sealed record ComparisonRow<TSource, TAccounting>(
    AccountingDifferenceKind Kind, DateOnly Date, TSource? Source, TAccounting? Accounting, string? MatchedBy, long? AmountDifference)
    where TSource : class where TAccounting : class;

/// <summary>Number of rows of one kind and their amounts per currency (of the side that exists; 1C side for "amount differs").</summary>
public sealed record ComparisonCount(AccountingDifferenceKind Kind, int Count, IReadOnlyList<MoneyAmount> Totals);

/// <param name="Matched">Pairs found on both sides without a difference, in the period.</param>
public sealed record ComparisonReport<TSource, TAccounting>(
    int Matched, IReadOnlyList<ComparisonCount> Counts, Page<ComparisonRow<TSource, TAccounting>> Rows)
    where TSource : class where TAccounting : class;

/// <summary>Balance of one bank account by the bank and by 1C.</summary>
/// <param name="Status">equal, differs, no_1c_balance (the account has no balance in 1C) or no_bank_data (1C account unknown to the bank data).</param>
public sealed record BankBalanceComparisonRow(
    string? AccountNumber, string? Currency, DateOnly? AccountingDate, long? AccountingBalance, DateOnly? BankDate, long? BankBalance,
    long? Difference, string Status);

/// <summary>One line of the settlement comparison with a counterparty: the same turnover by Synect's sources and by 1C.</summary>
/// <param name="Line">sales_documents, purchase_documents, receipts or payments.</param>
/// <param name="SourceName">didox or bank.</param>
public sealed record CounterpartyComparisonLine(string Line, string SourceName, string Currency, long Source, int SourceCount, long Accounting, int AccountingCount)
{
    public long Difference => Accounting - Source;
}

public sealed record CounterpartyComparison(
    string Inn, DatePeriod Period, IReadOnlyList<CounterpartyComparisonLine> Lines, DateOnly? BalanceDate, IReadOnlyList<AccountingSettlementRow> AccountingBalances);

/// <summary>
/// Comparison of 1C data with the bank statement and Didox (read only, computed on request, nothing is stored):
/// payments missing in 1C or in the bank, documents missing in 1C or in Didox, different amounts, unposted 1C documents,
/// bank balances against 1C, and turnovers with one counterparty. Pairing rules: <see cref="AccountingMatcher"/>.
/// Bank operations rejected by the bank are excluded; deleted 1C records are excluded; currencies are never mixed.
/// </summary>
public sealed class AccountingComparison(
    AnalyticsContext ctx, AccountingQueries queries, IOptions<AccountingOptions> options, IOptions<ReconciliationOptions> reconciliation)
{
    private AccountingOptions O => options.Value;

    /// <summary>Bank payments of the period against 1C bank documents.</summary>
    public Task<AnalyticsResult<ComparisonReport<ComparedBankPayment, ComparedAccountingPayment>>> CompareBankAsync(
        DatePeriod period, IReadOnlyCollection<AccountingDifferenceKind>? kinds, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var window = Math.Max(0, O.PaymentDateWindowDays);
            var from = period.From.AddDays(-window);
            var to = period.To.AddDays(window);
            var bank = await ctx.QueryAsync<BankRow>(new Sql().Raw("""
                select t.id, t.posting_date as date, t.direction, t.amount, t.currency, t.counterparty_inn, t.counterparty_name, t.purpose,
                       t.document_number, a.account_number
                from bank_transactions t join bank_accounts a on a.tenant_id = t.tenant_id and a.id = t.bank_account_id
                where t.tenant_id =
                """).P(ctx.TenantId).Raw(" and t.has_error = false and t.posting_date between ").P(from).Raw(" and ").P(to)
                .Raw(" order by t.posting_date, t.id limit ").P(O.MaxCompareRows + 1), token);
            var accounting = await ctx.QueryAsync<AccountingPaymentRow>(new Sql().Raw("""
                select d.ref, d.number, d.date, d.direction, d.amount_minor as amount, d.currency,
                       coalesce(d.counterparty_tin, c.tin) as counterparty_tin, d.posted, d.purpose,
                       coalesce(d.bank_account_number, a.number) as bank_account_number
                from accounting_bank_documents d
                left join accounting_counterparties c on c.tenant_id = d.tenant_id and c.ref = d.counterparty_ref
                left join accounting_bank_accounts a on a.tenant_id = d.tenant_id and a.ref = d.bank_account_ref
                where d.tenant_id =
                """).P(ctx.TenantId).Raw(" and not d.is_deleted and d.date between ").P(from).Raw(" and ").P(to)
                .Raw(" order by d.date, d.ref limit ").P(O.MaxCompareRows + 1), token);
            RequireWithinLimit(bank.Count, accounting.Count);

            var bankById = bank.ToDictionary(b => b.Id.ToString("N"), StringComparer.Ordinal);
            var accountingByRef = accounting.ToDictionary(a => a.Ref, StringComparer.Ordinal);
            var results = AccountingMatcher.MatchPayments(
                bank.Select(b => new MatchItem(b.Id.ToString("N"), b.Date, b.Amount, b.Currency, b.Direction == "Incoming", Blank(b.CounterpartyInn), true, [])).ToList(),
                accounting.Select(a => new MatchItem(a.Ref, a.Date, a.Amount, a.Currency, a.Direction == "Receipt", Blank(a.CounterpartyTin), a.Posted, [])).ToList(),
                window);

            return Report(results, period, kinds, page,
                s => { var b = bankById[s.Id]; return new ComparedBankPayment(b.Id, b.Date, b.Direction, b.Amount, b.Currency, Blank(b.CounterpartyInn), b.CounterpartyName, b.Purpose, b.DocumentNumber, b.AccountNumber); },
                a => { var d = accountingByRef[a.Id]; return new ComparedAccountingPayment(d.Ref, d.Number, d.Date, d.Direction, d.Amount, d.Currency, d.CounterpartyTin, d.Posted, d.Purpose, d.BankAccountNumber); });
        }, ct);

    /// <summary>
    /// Didox documents of the period (counted statuses, document types of the reconciliation: invoices and acts) against
    /// 1C sales and purchase documents.
    /// </summary>
    public Task<AnalyticsResult<ComparisonReport<ComparedDidoxDocument, ComparedAccountingDocument>>> CompareDocumentsAsync(
        DatePeriod period, IReadOnlyCollection<AccountingDifferenceKind>? kinds, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var window = Math.Max(0, O.DocumentDateWindowDays);
            var from = period.From.AddDays(-window);
            var to = period.To.AddDays(window);
            var (utcFrom, utcTo) = ctx.UtcRange(new DatePeriod(from, to));
            var didox = await ctx.QueryAsync<DidoxRow>(new Sql().Raw("""
                select d.id, d.external_id, d.number, d.document_date, d.source_created_at, d.owner, d.doc_type, d.status, d.counterparty_inn,
                       d.counterparty_name, case when d.total_delivery_sum_with_vat > 0 then d.total_delivery_sum_with_vat else d.total_sum end as total,
                       d.currency
                from edo_documents d
                where d.tenant_id =
                """).P(ctx.TenantId).Raw(" and d.status = any(").P(ctx.Options.Edo.CountedStatuses).Raw(") and d.doc_type = any(")
                .P(reconciliation.Value.DocumentTypes).Raw(") and ((d.document_date between ").P(from).Raw(" and ").P(to)
                .Raw(") or (d.document_date is null and d.source_created_at >= ").P(utcFrom).Raw(" and d.source_created_at < ").P(utcTo)
                .Raw(")) order by d.id limit ").P(O.MaxCompareRows + 1), token);
            var accounting = await ctx.QueryAsync<AccountingDocumentRow>(new Sql().Raw("""
                select d.ref, d.number, d.esf_number, d.esf_id, d.date, d.doc_kind, d.amount_total_minor as total, d.currency,
                       coalesce(d.counterparty_tin, c.tin) as counterparty_tin, d.posted
                from accounting_documents d
                left join accounting_counterparties c on c.tenant_id = d.tenant_id and c.ref = d.counterparty_ref
                where d.tenant_id =
                """).P(ctx.TenantId).Raw(" and not d.is_deleted and d.date between ").P(from).Raw(" and ").P(to)
                .Raw(" order by d.date, d.ref limit ").P(O.MaxCompareRows + 1), token);
            RequireWithinLimit(didox.Count, accounting.Count);

            DateOnly DateOf(DidoxRow d) => d.DocumentDate ?? (d.SourceCreatedAt is { } at ? ctx.LocalDate(at) : from);
            var didoxById = didox.ToDictionary(d => d.Id.ToString("N"), StringComparer.Ordinal);
            var accountingByRef = accounting.ToDictionary(a => a.Ref, StringComparer.Ordinal);
            var results = AccountingMatcher.MatchDocuments(
                didox.Select(d => new MatchItem(d.Id.ToString("N"), DateOf(d), d.Total, d.Currency, d.Owner == "Outgoing", Blank(d.CounterpartyInn), true,
                    Keys(d.Number), d.ExternalId)).ToList(),
                accounting.Select(a => new MatchItem(a.Ref, a.Date, a.Total, a.Currency, a.DocKind == "Sales", Blank(a.CounterpartyTin), a.Posted,
                    Keys(a.EsfNumber).Concat(Keys(a.Number)).Distinct(StringComparer.Ordinal).ToList(), Blank(a.EsfId))).ToList(),
                window);

            return Report(results, period, kinds, page,
                s => { var d = didoxById[s.Id]; return new ComparedDidoxDocument(d.Id, d.ExternalId, d.Number, DateOf(d), d.Owner, d.DocType, d.Status, Blank(d.CounterpartyInn), d.CounterpartyName, d.Total, d.Currency); },
                a => { var d = accountingByRef[a.Id]; return new ComparedAccountingDocument(d.Ref, d.Number, d.EsfNumber, d.Date, d.DocKind, d.Total, d.Currency, d.CounterpartyTin, d.Posted); });
        }, ct);

    /// <summary>
    /// Balance of every bank account by 1C (latest export on or before <paramref name="asOf"/>) against the bank's closing
    /// balance of the same date (the latest stored day on or before it). Accounts are joined by account number.
    /// </summary>
    public Task<AnalyticsResult<IReadOnlyList<BankBalanceComparisonRow>>> CompareBankBalancesAsync(DateOnly? asOf, CancellationToken ct) =>
        ctx.RunAsync<IReadOnlyList<BankBalanceComparisonRow>>(async token =>
        {
            var date = await queries.LatestDateAsync("accounting_bank_account_balances", asOf, token);
            var accounting = date is null ? [] : await ctx.QueryAsync<BalanceRow>(new Sql().Raw("""
                select a.number as account_number, a.currency, b.as_of_date as date, b.balance_minor as balance
                from accounting_bank_account_balances b
                left join accounting_bank_accounts a on a.tenant_id = b.tenant_id and a.ref = b.bank_account_ref
                where b.tenant_id =
                """).P(ctx.TenantId).Raw(" and not b.is_deleted and b.as_of_date = ").P(date.Value), token);
            var bankDate = date ?? asOf ?? ctx.Today;
            var bank = await ctx.QueryAsync<BalanceRow>(new Sql().Raw("""
                select a.account_number, a.currency, x.date, x.closing_balance as balance
                from bank_accounts a
                join lateral (select b.date, b.closing_balance from bank_daily_balances b
                              where b.tenant_id = a.tenant_id and b.bank_account_id = a.id and b.date <=
                """).P(bankDate).Raw(" order by b.date desc limit 1) x on true where a.tenant_id = ").P(ctx.TenantId), token);

            var bankByNumber = bank.Where(b => b.AccountNumber is not null).GroupBy(b => b.AccountNumber!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var rows = new List<BankBalanceComparisonRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in accounting.OrderBy(a => a.AccountNumber, StringComparer.Ordinal))
            {
                if (a.AccountNumber is not null && bankByNumber.TryGetValue(a.AccountNumber, out var b))
                {
                    seen.Add(a.AccountNumber);
                    rows.Add(new BankBalanceComparisonRow(a.AccountNumber, a.Currency ?? b.Currency, a.Date, a.Balance, b.Date, b.Balance, a.Balance - b.Balance,
                        a.Balance == b.Balance ? "equal" : "differs"));
                }
                else
                {
                    rows.Add(new BankBalanceComparisonRow(a.AccountNumber, a.Currency, a.Date, a.Balance, null, null, null, "no_bank_data"));
                }
            }
            foreach (var b in bankByNumber.Values.Where(b => !seen.Contains(b.AccountNumber!)).OrderBy(b => b.AccountNumber, StringComparer.Ordinal))
                rows.Add(new BankBalanceComparisonRow(b.AccountNumber, b.Currency, null, null, b.Date, b.Balance, null, "no_1c_balance"));
            return rows;
        }, ct);

    /// <summary>
    /// Turnovers with one counterparty in the period by Synect's sources (Didox documents, bank payments) and by 1C
    /// (posted documents and bank documents), per currency, and the 1C settlement balance on the period end.
    /// </summary>
    public Task<AnalyticsResult<CounterpartyComparison>> CompareCounterpartyAsync(string inn, DatePeriod period, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var t = ctx.TenantId;
            var (utcFrom, utcTo) = ctx.UtcRange(period);
            var rows = await ctx.QueryAsync<TurnoverRow>(new Sql()
                .Raw("select 'sales_documents' as line, 'source' as side, d.currency, coalesce(sum(case when d.total_delivery_sum_with_vat > 0 then d.total_delivery_sum_with_vat else d.total_sum end), 0)::bigint as amount, count(*)::int as count")
                .Raw(" from edo_documents d where d.tenant_id = ").P(t).Raw(" and d.owner = 'Outgoing'").Append(DidoxFilter(inn, period, utcFrom, utcTo)).Raw(" group by d.currency")
                .Raw(" union all select 'purchase_documents', 'source', d.currency, coalesce(sum(case when d.total_delivery_sum_with_vat > 0 then d.total_delivery_sum_with_vat else d.total_sum end), 0)::bigint, count(*)::int")
                .Raw(" from edo_documents d where d.tenant_id = ").P(t).Raw(" and d.owner = 'Incoming'").Append(DidoxFilter(inn, period, utcFrom, utcTo)).Raw(" group by d.currency")
                .Raw(" union all select case when b.direction = 'Incoming' then 'receipts' else 'payments' end, 'source', b.currency, coalesce(sum(b.amount), 0)::bigint, count(*)::int")
                .Raw(" from bank_transactions b where b.tenant_id = ").P(t).Raw(" and b.has_error = false and b.counterparty_inn = ").P(inn)
                .Raw(" and b.posting_date between ").P(period.From).Raw(" and ").P(period.To).Raw(" group by 1, b.currency")
                .Raw(" union all select case when d.doc_kind = 'Sales' then 'sales_documents' else 'purchase_documents' end, 'accounting', d.currency, coalesce(sum(d.amount_total_minor), 0)::bigint, count(*)::int")
                .Raw(" from accounting_documents d left join accounting_counterparties c on c.tenant_id = d.tenant_id and c.ref = d.counterparty_ref")
                .Raw(" where d.tenant_id = ").P(t).Raw(" and not d.is_deleted and d.posted and coalesce(d.counterparty_tin, c.tin) = ").P(inn)
                .Raw(" and d.date between ").P(period.From).Raw(" and ").P(period.To).Raw(" group by 1, d.currency")
                .Raw(" union all select case when d.direction = 'Receipt' then 'receipts' else 'payments' end, 'accounting', d.currency, coalesce(sum(d.amount_minor), 0)::bigint, count(*)::int")
                .Raw(" from accounting_bank_documents d left join accounting_counterparties c on c.tenant_id = d.tenant_id and c.ref = d.counterparty_ref")
                .Raw(" where d.tenant_id = ").P(t).Raw(" and not d.is_deleted and d.posted and coalesce(d.counterparty_tin, c.tin) = ").P(inn)
                .Raw(" and d.date between ").P(period.From).Raw(" and ").P(period.To).Raw(" group by 1, d.currency"), token);

            string[] order = ["sales_documents", "receipts", "purchase_documents", "payments"];
            var lines = rows.GroupBy(r => (r.Line, r.Currency))
                .Select(g =>
                {
                    var source = g.FirstOrDefault(r => r.Side == "source");
                    var accounting = g.FirstOrDefault(r => r.Side == "accounting");
                    return new CounterpartyComparisonLine(g.Key.Line, g.Key.Line.EndsWith("documents", StringComparison.Ordinal) ? "didox" : "bank", g.Key.Currency,
                        source?.Amount ?? 0, source?.Count ?? 0, accounting?.Amount ?? 0, accounting?.Count ?? 0);
                })
                .OrderBy(l => Array.IndexOf(order, l.Line)).ThenBy(l => l.Currency, StringComparer.Ordinal).ToList();

            var balanceDate = await queries.LatestDateAsync("accounting_settlement_balances", period.To, token);
            var balances = await queries.SettlementRowsAsync(balanceDate, inn, token);
            return new CounterpartyComparison(inn, period, lines, balanceDate, balances);
        }, ct);

    private Sql DidoxFilter(string inn, DatePeriod period, DateTimeOffset utcFrom, DateTimeOffset utcTo) =>
        new Sql().Raw(" and d.counterparty_inn = ").P(inn).Raw(" and d.status = any(").P(ctx.Options.Edo.CountedStatuses)
            .Raw(") and d.doc_type = any(").P(reconciliation.Value.DocumentTypes).Raw(") and ((d.document_date between ").P(period.From).Raw(" and ").P(period.To)
            .Raw(") or (d.document_date is null and d.source_created_at >= ").P(utcFrom).Raw(" and d.source_created_at < ").P(utcTo).Raw("))");

    private void RequireWithinLimit(int source, int accounting)
    {
        if (source > O.MaxCompareRows || accounting > O.MaxCompareRows)
            throw new ArgumentException($"The period has more than {O.MaxCompareRows} records on one side; narrow the period.");
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> Keys(string? number) => string.IsNullOrWhiteSpace(number) ? [] : ReferenceKey.KeysOf(number);

    /// <summary>Rows with a difference whose own date is inside the period, ordered by kind, date and id; paged by offset.</summary>
    private ComparisonReport<TSource, TAccounting> Report<TSource, TAccounting>(
        IReadOnlyList<MatchResult> results, DatePeriod period, IReadOnlyCollection<AccountingDifferenceKind>? kinds, PageRequest? page,
        Func<MatchItem, TSource> source, Func<MatchItem, TAccounting> accounting)
        where TSource : class where TAccounting : class
    {
        static DateOnly DateOf(MatchResult r) => r.Source?.Date ?? r.Accounting!.Date;
        var inPeriod = results.Where(r => DateOf(r) >= period.From && DateOf(r) <= period.To).ToList();
        var differences = inPeriod.Where(r => r.Difference is not null)
            .OrderBy(r => r.Difference).ThenBy(DateOf).ThenBy(r => r.Source?.Id ?? r.Accounting!.Id, StringComparer.Ordinal).ToList();
        var counts = differences.GroupBy(r => r.Difference!.Value).OrderBy(g => g.Key)
            .Select(g => new ComparisonCount(g.Key, g.Count(),
                g.GroupBy(r => (r.Accounting ?? r.Source!).Currency, StringComparer.Ordinal).OrderBy(c => c.Key, StringComparer.Ordinal)
                    .Select(c => new MoneyAmount(c.Key, c.Sum(r => (r.Accounting ?? r.Source!).Amount))).ToList()))
            .ToList();

        var selected = kinds is { Count: > 0 } ? differences.Where(r => kinds.Contains(r.Difference!.Value)).ToList() : differences;
        var limit = ctx.Limit(page);
        var offset = 0;
        if (page?.Cursor is { } cursor && (!int.TryParse(Cursor.Decode(cursor, 1)[0], out offset) || offset < 0))
            throw new ArgumentException("Invalid cursor.", nameof(page));
        var items = selected.Skip(offset).Take(limit)
            .Select(r => new ComparisonRow<TSource, TAccounting>(r.Difference!.Value, DateOf(r),
                r.Source is null ? null : source(r.Source), r.Accounting is null ? null : accounting(r.Accounting), r.MatchedBy,
                r.Difference == AccountingDifferenceKind.AmountDiffers ? r.Accounting!.Amount - r.Source!.Amount : null))
            .ToList();
        var next = offset + items.Count < selected.Count ? Cursor.Encode(offset + items.Count) : null;
        return new ComparisonReport<TSource, TAccounting>(inPeriod.Count(r => r.Difference is null), counts, new Page<ComparisonRow<TSource, TAccounting>>(items, next));
    }

    private sealed class BankRow
    {
        public Guid Id { get; set; }
        public DateOnly Date { get; set; }
        public string Direction { get; set; } = "";
        public long Amount { get; set; }
        public string Currency { get; set; } = "";
        public string? CounterpartyInn { get; set; }
        public string? CounterpartyName { get; set; }
        public string? Purpose { get; set; }
        public string? DocumentNumber { get; set; }
        public string? AccountNumber { get; set; }
    }

    private sealed class AccountingPaymentRow
    {
        public string Ref { get; set; } = "";
        public string Number { get; set; } = "";
        public DateOnly Date { get; set; }
        public string Direction { get; set; } = "";
        public long Amount { get; set; }
        public string Currency { get; set; } = "";
        public string? CounterpartyTin { get; set; }
        public bool Posted { get; set; }
        public string? Purpose { get; set; }
        public string? BankAccountNumber { get; set; }
    }

    private sealed class DidoxRow
    {
        public Guid Id { get; set; }
        public string ExternalId { get; set; } = "";
        public string? Number { get; set; }
        public DateOnly? DocumentDate { get; set; }
        public DateTimeOffset? SourceCreatedAt { get; set; }
        public string Owner { get; set; } = "";
        public string DocType { get; set; } = "";
        public string Status { get; set; } = "";
        public string? CounterpartyInn { get; set; }
        public string? CounterpartyName { get; set; }
        public long Total { get; set; }
        public string Currency { get; set; } = "";
    }

    private sealed class AccountingDocumentRow
    {
        public string Ref { get; set; } = "";
        public string Number { get; set; } = "";
        public string? EsfNumber { get; set; }
        public string? EsfId { get; set; }
        public DateOnly Date { get; set; }
        public string DocKind { get; set; } = "";
        public long Total { get; set; }
        public string Currency { get; set; } = "";
        public string? CounterpartyTin { get; set; }
        public bool Posted { get; set; }
    }

    private sealed class BalanceRow
    {
        public string? AccountNumber { get; set; }
        public string? Currency { get; set; }
        public DateOnly Date { get; set; }
        public long Balance { get; set; }
    }

    private sealed class TurnoverRow
    {
        public string Line { get; set; } = "";
        public string Side { get; set; } = "";
        public string Currency { get; set; } = "";
        public long Amount { get; set; }
        public int Count { get; set; }
    }
}
