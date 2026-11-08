using Synect.Application.Analytics.Banking;

namespace Synect.Application.Analytics.Counterparties;

/// <summary>Use as the INN argument to address payments without an INN (they are always grouped separately).</summary>
public static class CounterpartyKeys
{
    public const string WithoutInn = "";
}

public sealed record DirectionTotal(string Currency, long Incoming, long Outgoing, int IncomingCount, int OutgoingCount);

/// <summary>B1: totals per currency and the operations themselves (paged, newest first).</summary>
public sealed record CounterpartyPayments(string Inn, IReadOnlyList<DirectionTotal> Totals, Page<BankOperation> Operations);

public sealed record RankedCounterparty(int Rank, string Inn, string? Name, long Amount, int Count);

/// <summary>B2: per currency, the top N counterparties and, separately, payments without an INN.</summary>
public sealed record TopCounterparties(string Currency, long Total, IReadOnlyList<RankedCounterparty> Top, long WithoutInnAmount, int WithoutInnCount);

public sealed record EdoOwnerTotals(int Count, long TotalSum, long TotalVatSum);

/// <summary>
/// B3/B4: documents (counted statuses) and payments with one counterparty, and the difference in the EDO currency:
/// Receivable = outgoing documents - incoming payments; Payable = incoming documents - outgoing payments.
/// </summary>
public sealed record CounterpartySummary(
    string Inn, string? Name, string EdoCurrency,
    EdoOwnerTotals IncomingDocuments, EdoOwnerTotals OutgoingDocuments,
    IReadOnlyList<DirectionTotal> Payments, long Receivable, long Payable);

public sealed record TurnoverComparison(
    string Inn, string? Name, string Currency,
    long IncomingA, long OutgoingA, long IncomingB, long OutgoingB)
{
    public long IncomingDelta => IncomingB - IncomingA;
    public long OutgoingDelta => OutgoingB - OutgoingA;
}

public sealed record CounterpartyActivity(string Inn, string? Name, DateOnly FirstMonth, DateOnly LastMonth);

/// <summary>
/// B6. New: active in the period and never before it. Lost: active in the previous period of the same length but
/// not in this one. Accuracy depends on how much history is loaded: anything before <see cref="HistoryStartsAt"/> is unknown.
/// </summary>
public sealed record CounterpartyChanges(DateOnly? HistoryStartsAt, bool LimitedByHistory, Page<CounterpartyActivity> Counterparties);

public sealed record UndocumentedPayments(string Inn, string? Name, string Currency, long Amount, int Count, DateOnly FirstDate, DateOnly LastDate);

public sealed record Concentration(
    string Currency, long Total, int Counterparties, string? TopInn, string? TopName,
    decimal TopShare, decimal Top3Share, decimal Top5Share, long WithoutInnAmount);

public enum CounterpartyChangeKind
{
    New,
    Lost,
}

/// <summary>
/// Block B: counterparties. B2, B5, B6 and B9 read the precomputed analytics_counterparty_monthly (whole calendar
/// months touched by the period); B1, B3, B4 and B8 read the source tables for one INN or a narrow selection.
/// </summary>
public sealed class CounterpartyAnalytics(AnalyticsContext ctx)
{
    /// <summary>B1. Payments to and from a counterparty in the period.</summary>
    public Task<AnalyticsResult<CounterpartyPayments>> GetPaymentsAsync(
        string inn, DatePeriod period, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var totals = await DirectionTotalsAsync(inn, period, token);
            var limit = ctx.Limit(page);
            var sql = new Sql()
                .Raw("select t.id, t.bank_account_id as account_id, a.account_number, t.operation_id, t.posting_date, t.value_date,")
                .Raw(" t.direction, t.amount, t.currency, t.counterparty_inn, t.counterparty_name, t.counterparty_account,")
                .Raw(" t.purpose, t.document_number, t.is_final, t.source_state, false as is_day_closed")
                .Raw(" from bank_transactions t join bank_accounts a on a.tenant_id = t.tenant_id and a.id = t.bank_account_id")
                .Raw(" where t.tenant_id = ").P(ctx.TenantId).Raw(" and t.has_error = false")
                .Raw(" and t.posting_date between ").P(period.From).Raw(" and ").P(period.To);
            InnFilter(sql, "t.counterparty_inn", inn);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (t.posting_date, t.id) < (").P(DateOnly.Parse(c[0]!)).Raw(", ").P(Guid.Parse(c[1]!)).Raw(")");
            }
            sql.Raw(" order by t.posting_date desc, t.id desc limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<FlowsAnalytics.OperationRow>(sql, token);
            var operations = AnalyticsContext.ToPage(rows, limit, FlowsAnalytics.Map, r => Cursor.Encode(r.PostingDate, r.Id));
            return new CounterpartyPayments(inn, totals, operations);
        }, ct);

    /// <summary>B2. Top N counterparties by incoming or outgoing payments, per currency.</summary>
    public Task<AnalyticsResult<IReadOnlyList<TopCounterparties>>> GetTopAsync(
        DatePeriod period, FlowDirection direction, int top, CancellationToken ct) =>
        ctx.RunAsync<IReadOnlyList<TopCounterparties>>(async token =>
        {
            top = Math.Clamp(top, 1, ctx.Options.MaxPageSize);
            var rows = await MonthlyTotalsAsync(period, token);
            var names = await NamesAsync(token);
            return rows.GroupBy(r => r.Currency).OrderBy(g => g.Key).Select(g =>
            {
                var amounts = g.Select(r => (r.Inn, Amount: direction == FlowDirection.Incoming ? r.BankIncoming : r.BankOutgoing,
                        Count: r.BankCount))
                    .Where(r => r.Amount > 0).ToList();
                var withoutInn = amounts.Where(r => r.Inn == CounterpartyKeys.WithoutInn).ToList();
                var ranked = amounts.Where(r => r.Inn != CounterpartyKeys.WithoutInn)
                    .OrderByDescending(r => r.Amount).ThenBy(r => r.Inn, StringComparer.Ordinal).Take(top)
                    .Select((r, i) => new RankedCounterparty(i + 1, r.Inn, names.GetValueOrDefault(r.Inn), r.Amount, r.Count))
                    .ToList();
                return new TopCounterparties(g.Key, amounts.Sum(r => r.Amount), ranked,
                    withoutInn.Sum(r => r.Amount), withoutInn.Sum(r => r.Count));
            }).ToList();
        }, ct);

    /// <summary>B3, B4. Documents and payments with one counterparty and the open difference.</summary>
    public Task<AnalyticsResult<CounterpartySummary>> GetSummaryAsync(
        string inn, DatePeriod period, DateBasis basis, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var sql = new Sql()
                .Raw("select d.owner, count(*)::int as count, coalesce(sum(d.total_sum), 0)::bigint as total_sum,")
                .Raw(" coalesce(sum(d.total_vat_sum), 0)::bigint as total_vat_sum from edo_documents d where d.tenant_id = ").P(ctx.TenantId)
                .Raw(" and d.status = any(").P(ctx.Options.Edo.CountedStatuses).Raw(")");
            InnFilter(sql, "d.counterparty_inn", inn);
            DocumentsAnalytics.PeriodFilter(sql, ctx, "d", period, basis);
            sql.Raw(" group by d.owner");
            var docs = (await ctx.QueryAsync<OwnerRow>(sql, token)).ToDictionary(r => r.Owner);
            var payments = await DirectionTotalsAsync(inn, period, token);
            var names = await NamesAsync(token);

            EdoOwnerTotals Totals(string owner) => docs.TryGetValue(owner, out var r)
                ? new EdoOwnerTotals(r.Count, r.TotalSum, r.TotalVatSum) : new EdoOwnerTotals(0, 0, 0);
            var incomingDocs = Totals("Incoming");
            var outgoingDocs = Totals("Outgoing");
            var edoCurrency = ctx.Options.Edo.Currency;
            var paid = payments.SingleOrDefault(p => p.Currency == edoCurrency);
            return new CounterpartySummary(inn, names.GetValueOrDefault(inn), edoCurrency, incomingDocs, outgoingDocs, payments,
                Receivable: outgoingDocs.TotalSum - (paid?.Incoming ?? 0),
                Payable: incomingDocs.TotalSum - (paid?.Outgoing ?? 0));
        }, ct);

    /// <summary>B5. Turnover per counterparty and currency in two periods (all counterparties, or one INN).</summary>
    public Task<AnalyticsResult<Page<TurnoverComparison>>> CompareAsync(
        DatePeriod periodA, DatePeriod periodB, string? inn, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var (a1, a2) = periodA.Months;
            var (b1, b2) = periodB.Months;
            var sql = new Sql()
                .Raw("select m.counterparty_inn as inn, m.currency,")
                .Raw(" coalesce(sum(m.bank_incoming) filter (where m.month between ").P(a1).Raw(" and ").P(a2).Raw("), 0)::bigint as incoming_a,")
                .Raw(" coalesce(sum(m.bank_outgoing) filter (where m.month between ").P(a1).Raw(" and ").P(a2).Raw("), 0)::bigint as outgoing_a,")
                .Raw(" coalesce(sum(m.bank_incoming) filter (where m.month between ").P(b1).Raw(" and ").P(b2).Raw("), 0)::bigint as incoming_b,")
                .Raw(" coalesce(sum(m.bank_outgoing) filter (where m.month between ").P(b1).Raw(" and ").P(b2).Raw("), 0)::bigint as outgoing_b")
                .Raw(" from analytics_counterparty_monthly m where m.tenant_id = ").P(ctx.TenantId)
                .Raw(" and ((m.month between ").P(a1).Raw(" and ").P(a2).Raw(") or (m.month between ").P(b1).Raw(" and ").P(b2).Raw("))");
            if (inn is not null) sql.Raw(" and m.counterparty_inn = ").P(inn);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (m.counterparty_inn, m.currency) > (").P(c[0]!).Raw(", ").P(c[1]!).Raw(")");
            }
            sql.Raw(" group by m.counterparty_inn, m.currency")
                .Raw(" having sum(m.bank_incoming) + sum(m.bank_outgoing) > 0")
                .Raw(" order by m.counterparty_inn, m.currency limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<CompareRow>(sql, token);
            var names = await NamesAsync(token);
            return AnalyticsContext.ToPage(rows, limit,
                r => new TurnoverComparison(r.Inn, names.GetValueOrDefault(r.Inn), r.Currency, r.IncomingA, r.OutgoingA, r.IncomingB, r.OutgoingB),
                r => Cursor.Encode(r.Inn, r.Currency));
        }, ct);

    /// <summary>B6. New or lost counterparties of the period (any bank or EDO activity, by month).</summary>
    public Task<AnalyticsResult<CounterpartyChanges>> GetChangesAsync(
        DatePeriod period, CounterpartyChangeKind kind, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var (m1, m2) = period.Months;
            var (p1, _) = period.Previous().Months;
            var t = ctx.TenantId;
            var sql = new Sql()
                .Raw("with active as (select counterparty_inn as inn, min(month) as first_month, max(month) as last_month,")
                .Raw(" bool_or(month between ").P(m1).Raw(" and ").P(m2).Raw(") as in_period,")
                .Raw(" bool_or(month < ").P(m1).Raw(") as before_period,")
                .Raw(" bool_or(month >= ").P(p1).Raw(" and month < ").P(m1).Raw(") as in_previous")
                .Raw(" from analytics_counterparty_monthly where tenant_id = ").P(t)
                .Raw(" and counterparty_inn <> '' and (bank_count > 0 or edo_incoming_count > 0 or edo_outgoing_count > 0)")
                .Raw(" and month <= ").P(m2).Raw(" group by counterparty_inn)")
                .Raw(" select inn, first_month, last_month from active where ")
                .Raw(kind == CounterpartyChangeKind.New ? "in_period and not before_period" : "in_previous and not in_period");
            if (page?.Cursor is { } cursor) sql.Raw(" and inn > ").P(Cursor.Decode(cursor, 1)[0]!);
            sql.Raw(" order by inn limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<ActivityRow>(sql, token);
            var names = await NamesAsync(token);

            var historyStart = (await ctx.QueryAsync<DateRow>(new Sql()
                .Raw("select min(month) as value from analytics_counterparty_monthly where tenant_id = ").P(t), token)).Single().Value;
            var limited = historyStart is null || historyStart >= (kind == CounterpartyChangeKind.New ? m1 : p1);
            var pageResult = AnalyticsContext.ToPage(rows, limit,
                r => new CounterpartyActivity(r.Inn, names.GetValueOrDefault(r.Inn), r.FirstMonth, r.LastMonth),
                r => Cursor.Encode(r.Inn));
            return new CounterpartyChanges(historyStart, limited, pageResult);
        }, ct);

    /// <summary>B8. Outgoing payments to INNs that have no Didox document (counted statuses) at all.</summary>
    public Task<AnalyticsResult<Page<UndocumentedPayments>>> GetPaymentsWithoutDocumentsAsync(
        DatePeriod period, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var t = ctx.TenantId;
            var sql = new Sql()
                .Raw("select t.counterparty_inn as inn, max(t.counterparty_name) as name, t.currency,")
                .Raw(" sum(t.amount)::bigint as amount, count(*)::int as count, min(t.posting_date) as first_date, max(t.posting_date) as last_date")
                .Raw(" from bank_transactions t where t.tenant_id = ").P(t)
                .Raw(" and t.has_error = false and t.direction = 'Outgoing' and coalesce(t.counterparty_inn, '') <> ''")
                .Raw(" and t.posting_date between ").P(period.From).Raw(" and ").P(period.To)
                .Raw(" and not exists (select 1 from edo_documents d where d.tenant_id = ").P(t)
                .Raw(" and d.counterparty_inn = t.counterparty_inn and d.status = any(").P(ctx.Options.Edo.CountedStatuses).Raw("))");
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (t.counterparty_inn, t.currency) > (").P(c[0]!).Raw(", ").P(c[1]!).Raw(")");
            }
            sql.Raw(" group by t.counterparty_inn, t.currency order by t.counterparty_inn, t.currency limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<UndocumentedRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit,
                r => new UndocumentedPayments(r.Inn, r.Name, r.Currency, r.Amount, r.Count, r.FirstDate, r.LastDate),
                r => Cursor.Encode(r.Inn, r.Currency));
        }, ct);

    /// <summary>B9. Share of the largest counterparty and of the top 3 and top 5, per currency.</summary>
    public Task<AnalyticsResult<IReadOnlyList<Concentration>>> GetConcentrationAsync(
        DatePeriod period, FlowDirection direction, CancellationToken ct) =>
        ctx.RunAsync<IReadOnlyList<Concentration>>(async token =>
        {
            var rows = await MonthlyTotalsAsync(period, token);
            var names = await NamesAsync(token);
            return rows.GroupBy(r => r.Currency).OrderBy(g => g.Key).Select(g =>
            {
                var amounts = g.Select(r => (r.Inn, Amount: direction == FlowDirection.Incoming ? r.BankIncoming : r.BankOutgoing))
                    .Where(r => r.Amount > 0).ToList();
                var withoutInn = amounts.Where(r => r.Inn == CounterpartyKeys.WithoutInn).Sum(r => r.Amount);
                var ranked = amounts.Where(r => r.Inn != CounterpartyKeys.WithoutInn)
                    .OrderByDescending(r => r.Amount).ThenBy(r => r.Inn, StringComparer.Ordinal).ToList();
                var total = amounts.Sum(r => r.Amount);
                decimal Share(int n) => total == 0 ? 0 : Math.Round(ranked.Take(n).Sum(r => r.Amount) / (decimal)total, 4);
                var topInn = ranked.FirstOrDefault().Inn;
                return new Concentration(g.Key, total, ranked.Count, topInn, topInn is null ? null : names.GetValueOrDefault(topInn),
                    Share(1), Share(3), Share(5), withoutInn);
            }).ToList();
        }, ct);

    private async Task<IReadOnlyList<DirectionTotal>> DirectionTotalsAsync(string inn, DatePeriod period, CancellationToken ct)
    {
        var sql = new Sql()
            .Raw("select t.currency,")
            .Raw(" coalesce(sum(t.amount) filter (where t.direction = 'Incoming'), 0)::bigint as incoming,")
            .Raw(" coalesce(sum(t.amount) filter (where t.direction = 'Outgoing'), 0)::bigint as outgoing,")
            .Raw(" (count(*) filter (where t.direction = 'Incoming'))::int as incoming_count,")
            .Raw(" (count(*) filter (where t.direction = 'Outgoing'))::int as outgoing_count")
            .Raw(" from bank_transactions t where t.tenant_id = ").P(ctx.TenantId)
            .Raw(" and t.has_error = false and t.posting_date between ").P(period.From).Raw(" and ").P(period.To);
        InnFilter(sql, "t.counterparty_inn", inn);
        sql.Raw(" group by t.currency order by t.currency");
        return (await ctx.QueryAsync<DirectionRow>(sql, ct))
            .Select(r => new DirectionTotal(r.Currency, r.Incoming, r.Outgoing, r.IncomingCount, r.OutgoingCount)).ToList();
    }

    private Task<List<MonthlyRow>> MonthlyTotalsAsync(DatePeriod period, CancellationToken ct)
    {
        var (m1, m2) = period.Months;
        return ctx.QueryAsync<MonthlyRow>(new Sql()
            .Raw("select counterparty_inn as inn, currency, sum(bank_incoming)::bigint as bank_incoming,")
            .Raw(" sum(bank_outgoing)::bigint as bank_outgoing, sum(bank_count)::int as bank_count")
            .Raw(" from analytics_counterparty_monthly where tenant_id = ").P(ctx.TenantId)
            .Raw(" and month between ").P(m1).Raw(" and ").P(m2)
            .Raw(" group by counterparty_inn, currency"), ct);
    }

    private async Task<Dictionary<string, string?>> NamesAsync(CancellationToken ct) =>
        (await ctx.QueryAsync<NameRow>(new Sql()
            .Raw("select inn, name from counterparties where tenant_id = ").P(ctx.TenantId), ct))
        .ToDictionary(r => r.Inn, r => r.Name);

    internal static void InnFilter(Sql sql, string column, string inn)
    {
        if (inn == CounterpartyKeys.WithoutInn) sql.Raw($" and coalesce({column}, '') = ''");
        else sql.Raw($" and {column} = ").P(inn);
    }

    private sealed class DirectionRow
    {
        public string Currency { get; set; } = "";
        public long Incoming { get; set; }
        public long Outgoing { get; set; }
        public int IncomingCount { get; set; }
        public int OutgoingCount { get; set; }
    }

    private sealed class MonthlyRow
    {
        public string Inn { get; set; } = "";
        public string Currency { get; set; } = "";
        public long BankIncoming { get; set; }
        public long BankOutgoing { get; set; }
        public int BankCount { get; set; }
    }

    private sealed class NameRow
    {
        public string Inn { get; set; } = "";
        public string? Name { get; set; }
    }

    private sealed class OwnerRow
    {
        public string Owner { get; set; } = "";
        public int Count { get; set; }
        public long TotalSum { get; set; }
        public long TotalVatSum { get; set; }
    }

    private sealed class CompareRow
    {
        public string Inn { get; set; } = "";
        public string Currency { get; set; } = "";
        public long IncomingA { get; set; }
        public long OutgoingA { get; set; }
        public long IncomingB { get; set; }
        public long OutgoingB { get; set; }
    }

    private sealed class ActivityRow
    {
        public string Inn { get; set; } = "";
        public DateOnly FirstMonth { get; set; }
        public DateOnly LastMonth { get; set; }
    }

    private sealed class DateRow
    {
        public DateOnly? Value { get; set; }
    }

    private sealed class UndocumentedRow
    {
        public string Inn { get; set; } = "";
        public string? Name { get; set; }
        public string Currency { get; set; } = "";
        public long Amount { get; set; }
        public int Count { get; set; }
        public DateOnly FirstDate { get; set; }
        public DateOnly LastDate { get; set; }
    }
}
