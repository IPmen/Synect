namespace Synect.Application.Analytics.Banking;

public sealed record AccountBalance(
    Guid AccountId, string AccountNumber, string? AccountName, string Currency,
    DateOnly? Date, long Balance, bool IsDayClosed);

/// <summary>Balances per account and totals per currency (never mixed across currencies).</summary>
public sealed record BalancesSnapshot(DateOnly AsOf, IReadOnlyList<AccountBalance> Accounts, IReadOnlyList<MoneyAmount> TotalsByCurrency);

/// <param name="SourceDate">Date of the daily balance row used; earlier than Date when the balance was carried forward.</param>
public sealed record DailyBalancePoint(
    Guid AccountId, string Currency, DateOnly Date, long? Balance, bool? IsDayClosed, DateOnly? SourceDate)
{
    public bool IsCarriedForward => SourceDate is { } s && s != Date;
}

public sealed record AccountBalanceStats(
    Guid AccountId, string AccountNumber, string Currency, int Days,
    long Average, long Minimum, DateOnly MinimumDate, long Maximum);

/// <summary>Block A: money on accounts (A1, A2, A4, A11). Reads bank_daily_balances only.</summary>
public sealed class BalancesAnalytics(AnalyticsContext ctx)
{
    /// <summary>A1. Latest known closing balance of every account.</summary>
    public Task<AnalyticsResult<BalancesSnapshot>> GetCurrentAsync(CancellationToken ct) =>
        ctx.RunAsync(token => SnapshotAsync(ctx.Today, token), ct);

    /// <summary>A2. Closing balance at the end of <paramref name="date"/> (last row on or before it).</summary>
    public Task<AnalyticsResult<BalancesSnapshot>> GetOnDateAsync(DateOnly date, CancellationToken ct) =>
        ctx.RunAsync(token => SnapshotAsync(date, token), ct);

    /// <summary>
    /// A4. Closing balance per account and day of the period. Days without a row carry the previous
    /// closing balance forward. Paged by (account, date).
    /// </summary>
    public Task<AnalyticsResult<Page<DailyBalancePoint>>> GetHistoryAsync(
        DatePeriod period, Guid? accountId, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var sql = DaysCte(period, accountId)
                .Raw(" select d.account_id, d.currency, d.date, b.closing_balance as balance, b.is_day_closed, b.date as source_date")
                .Raw(" from days d left join lateral (select x.date, x.closing_balance, x.is_day_closed from bank_daily_balances x")
                .Raw(" where x.tenant_id = ").P(ctx.TenantId)
                .Raw(" and x.bank_account_id = d.account_id and x.date <= d.date order by x.date desc limit 1) b on true");
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" where (d.account_id, d.date) > (").P(Guid.Parse(c[0]!)).Raw(", ").P(DateOnly.Parse(c[1]!)).Raw(")");
            }
            sql.Raw(" order by d.account_id, d.date limit ").P(limit + 1);

            var rows = await ctx.QueryAsync<HistoryRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit,
                r => new DailyBalancePoint(r.AccountId, r.Currency, r.Date, r.Balance, r.IsDayClosed, r.SourceDate),
                r => Cursor.Encode(r.AccountId, r.Date));
        }, ct);

    /// <summary>A11. Average, minimum (with its date) and maximum daily closing balance per account over the period.</summary>
    public Task<AnalyticsResult<IReadOnlyList<AccountBalanceStats>>> GetStatsAsync(
        DatePeriod period, CancellationToken ct) =>
        ctx.RunAsync<IReadOnlyList<AccountBalanceStats>>(async token =>
        {
            var sql = DaysCte(period, null)
                .Raw(", series as (select d.account_id, d.account_number, d.currency, d.date, b.closing_balance as balance")
                .Raw(" from days d left join lateral (select x.closing_balance from bank_daily_balances x")
                .Raw(" where x.tenant_id = ").P(ctx.TenantId)
                .Raw(" and x.bank_account_id = d.account_id and x.date <= d.date order by x.date desc limit 1) b on true)")
                .Raw(" select account_id, account_number, currency, count(balance)::int as days,")
                .Raw(" round(avg(balance))::bigint as average, min(balance) as minimum, max(balance) as maximum,")
                .Raw(" (array_agg(date order by balance, date) filter (where balance is not null))[1] as minimum_date")
                .Raw(" from series group by account_id, account_number, currency having count(balance) > 0")
                .Raw(" order by currency, account_number");
            var rows = await ctx.QueryAsync<StatsRow>(sql, token);
            return rows.Select(r => new AccountBalanceStats(r.AccountId, r.AccountNumber, r.Currency, r.Days,
                r.Average, r.Minimum, r.MinimumDate, r.Maximum)).ToList();
        }, ct);

    private async Task<BalancesSnapshot> SnapshotAsync(DateOnly asOf, CancellationToken ct)
    {
        var sql = new Sql()
            .Raw("select a.id as account_id, a.account_number, a.name as account_name, a.currency, b.date,")
            .Raw(" coalesce(b.closing_balance, 0) as balance, coalesce(b.is_day_closed, false) as is_day_closed")
            .Raw(" from bank_accounts a left join lateral (select d.date, d.closing_balance, d.is_day_closed")
            .Raw(" from bank_daily_balances d where d.tenant_id = a.tenant_id and d.bank_account_id = a.id and d.date <= ").P(asOf)
            .Raw(" order by d.date desc limit 1) b on true")
            .Raw(" where a.tenant_id = ").P(ctx.TenantId)
            .Raw(" order by a.currency, a.account_number");
        var rows = await ctx.QueryAsync<SnapshotRow>(sql, ct);
        var accounts = rows.Select(r => new AccountBalance(r.AccountId, r.AccountNumber, r.AccountName, r.Currency,
            r.Date, r.Balance, r.IsDayClosed)).ToList();
        var totals = accounts.GroupBy(a => a.Currency).OrderBy(g => g.Key)
            .Select(g => new MoneyAmount(g.Key, g.Sum(a => a.Balance))).ToList();
        return new BalancesSnapshot(asOf, accounts, totals);
    }

    private Sql DaysCte(DatePeriod period, Guid? accountId)
    {
        var sql = new Sql()
            .Raw("with acc as (select id, account_number, currency from bank_accounts where tenant_id = ").P(ctx.TenantId);
        if (accountId is { } id) sql.Raw(" and id = ").P(id);
        return sql.Raw("), days as (select acc.id as account_id, acc.account_number, acc.currency, g::date as date")
            .Raw(" from acc cross join generate_series(").P(period.From).Raw("::date, ").P(period.To)
            .Raw("::date, interval '1 day') g)");
    }

    private sealed class SnapshotRow
    {
        public Guid AccountId { get; set; }
        public string AccountNumber { get; set; } = "";
        public string? AccountName { get; set; }
        public string Currency { get; set; } = "";
        public DateOnly? Date { get; set; }
        public long Balance { get; set; }
        public bool IsDayClosed { get; set; }
    }

    private sealed class HistoryRow
    {
        public Guid AccountId { get; set; }
        public string Currency { get; set; } = "";
        public DateOnly Date { get; set; }
        public long? Balance { get; set; }
        public bool? IsDayClosed { get; set; }
        public DateOnly? SourceDate { get; set; }
    }

    private sealed class StatsRow
    {
        public Guid AccountId { get; set; }
        public string AccountNumber { get; set; } = "";
        public string Currency { get; set; } = "";
        public int Days { get; set; }
        public long Average { get; set; }
        public long Minimum { get; set; }
        public long Maximum { get; set; }
        public DateOnly MinimumDate { get; set; }
    }
}
