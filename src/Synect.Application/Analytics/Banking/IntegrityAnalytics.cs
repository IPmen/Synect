namespace Synect.Application.Analytics.Banking;

public enum BalanceDiscrepancyKind
{
    /// <summary>opening + credit - debit != closing for the day.</summary>
    DayEquation,

    /// <summary>Closing balance differs from the opening balance of the next stored day.</summary>
    Carryover,

    /// <summary>The next stored day is not the next calendar day (days are missing).</summary>
    MissingDays,

    /// <summary>The day totals differ from the sum of stored error-free operations of that day.</summary>
    TotalsMismatch,
}

public sealed record BalanceDiscrepancy(
    Guid AccountId, string AccountNumber, string Currency, DateOnly Date, BalanceDiscrepancyKind Kind,
    long Expected, long Actual, bool IsDayClosed, DateOnly? NextDate);

/// <summary>Block A: balance integrity (A7 partially, A10). Day closure comes from is_day_closed.</summary>
public sealed class IntegrityAnalytics(AnalyticsContext ctx)
{
    /// <summary>
    /// Checks every stored day of the period: day equation, carry-over to the next stored day, gaps between
    /// stored days, and day totals against stored operations. Returns the discrepancies only.
    /// </summary>
    public Task<AnalyticsResult<Page<BalanceDiscrepancy>>> CheckBalancesAsync(
        DatePeriod period, Guid? accountId, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var t = ctx.TenantId;
            var sql = new Sql()
                .Raw("with d as (select x.bank_account_id, x.date, x.opening_balance, x.closing_balance, x.total_debit, x.total_credit,")
                .Raw(" x.is_day_closed, lead(x.date) over w as next_date, lead(x.opening_balance) over w as next_opening")
                .Raw(" from bank_daily_balances x where x.tenant_id = ").P(t).Raw(" and x.date >= ").P(period.From);
            if (accountId is { } id) sql.Raw(" and x.bank_account_id = ").P(id);
            sql.Raw(" window w as (partition by x.bank_account_id order by x.date)),")
                .Raw(" ops as (select o.bank_account_id, o.posting_date,")
                .Raw(" sum(case when o.direction = 'Incoming' then o.amount else 0 end)::bigint as incoming,")
                .Raw(" sum(case when o.direction = 'Outgoing' then o.amount else 0 end)::bigint as outgoing")
                .Raw(" from bank_transactions o where o.tenant_id = ").P(t)
                .Raw(" and o.has_error = false and o.posting_date between ").P(period.From).Raw(" and ").P(period.To)
                .Raw(" group by 1, 2),")
                .Raw(" checks as (")
                .Raw(" select d.bank_account_id, d.date, 'DayEquation' as kind, d.closing_balance as expected,")
                .Raw(" d.opening_balance + d.total_credit - d.total_debit as actual, d.is_day_closed, d.next_date from d")
                .Raw(" where d.opening_balance + d.total_credit - d.total_debit <> d.closing_balance")
                .Raw(" union all select d.bank_account_id, d.date, 'Carryover', d.closing_balance, d.next_opening, d.is_day_closed, d.next_date from d")
                .Raw(" where d.next_date is not null and d.next_opening <> d.closing_balance")
                .Raw(" union all select d.bank_account_id, d.date, 'MissingDays', 1, (d.next_date - d.date)::bigint, d.is_day_closed, d.next_date from d")
                .Raw(" where d.next_date is not null and d.next_date - d.date > 1")
                .Raw(" union all select d.bank_account_id, d.date, 'TotalsMismatch', d.total_credit + d.total_debit,")
                .Raw(" coalesce(o.incoming, 0) + coalesce(o.outgoing, 0), d.is_day_closed, d.next_date")
                .Raw(" from d left join ops o on o.bank_account_id = d.bank_account_id and o.posting_date = d.date")
                .Raw(" where d.total_credit <> coalesce(o.incoming, 0) or d.total_debit <> coalesce(o.outgoing, 0))")
                .Raw(" select c.bank_account_id as account_id, a.account_number, a.currency, c.date, c.kind,")
                .Raw(" c.expected::bigint as expected, c.actual::bigint as actual, c.is_day_closed, c.next_date")
                .Raw(" from checks c join bank_accounts a on a.tenant_id = ").P(t).Raw(" and a.id = c.bank_account_id")
                .Raw(" where c.date <= ").P(period.To);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 3);
                sql.Raw(" and (c.bank_account_id, c.date, c.kind) > (").P(Guid.Parse(c[0]!)).Raw(", ")
                    .P(DateOnly.Parse(c[1]!)).Raw(", ").P(c[2]!).Raw(")");
            }
            sql.Raw(" order by c.bank_account_id, c.date, c.kind limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<Row>(sql, token);
            return AnalyticsContext.ToPage(rows, limit,
                r => new BalanceDiscrepancy(r.AccountId, r.AccountNumber, r.Currency, r.Date, Enum.Parse<BalanceDiscrepancyKind>(r.Kind),
                    r.Expected, r.Actual, r.IsDayClosed, r.NextDate),
                r => Cursor.Encode(r.AccountId, r.Date, r.Kind));
        }, ct);

    private sealed class Row
    {
        public Guid AccountId { get; set; }
        public string AccountNumber { get; set; } = "";
        public string Currency { get; set; } = "";
        public DateOnly Date { get; set; }
        public string Kind { get; set; } = "";
        public long Expected { get; set; }
        public long Actual { get; set; }
        public bool IsDayClosed { get; set; }
        public DateOnly? NextDate { get; set; }
    }
}
