namespace Synect.Application.Analytics.Banking;

public sealed record CashFlowBucket(DateOnly BucketStart, string Currency, long Incoming, long Outgoing, long Net, int Count);

/// <summary>A bank operation as returned by analytics. Operations with bank errors are never included.</summary>
public sealed record BankOperation(
    Guid Id, Guid AccountId, string AccountNumber, string OperationId, DateOnly PostingDate, DateOnly? ValueDate,
    FlowDirection Direction, long Amount, string Currency,
    string? CounterpartyInn, string? CounterpartyName, string? CounterpartyAccount,
    string? Purpose, string? DocumentNumber, bool IsFinal, string? SourceState, bool IsDayClosed);

public sealed record IdleAccount(Guid AccountId, string AccountNumber, string? AccountName, string Currency, DateOnly? LastMovementDate, int? DaysIdle);

/// <summary>Block A: cash flows (A3, A5, A6, A9). Bank operations with HasError are always excluded.</summary>
public sealed class FlowsAnalytics(AnalyticsContext ctx)
{
    /// <summary>A3. Incoming and outgoing per day / ISO week (Monday) / month and currency.</summary>
    public Task<AnalyticsResult<Page<CashFlowBucket>>> GetCashFlowAsync(
        DatePeriod period, Granularity granularity, Guid? accountId, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var unit = granularity switch { Granularity.Day => "day", Granularity.Week => "week", _ => "month" };
            var sql = new Sql()
                .Raw("select * from (select date_trunc(").P(unit).Raw(", t.posting_date::timestamp)::date as bucket_start, t.currency,")
                .Raw(" sum(case when t.direction = 'Incoming' then t.amount else 0 end)::bigint as incoming,")
                .Raw(" sum(case when t.direction = 'Outgoing' then t.amount else 0 end)::bigint as outgoing,")
                .Raw(" count(*)::int as count")
                .Raw(" from bank_transactions t where t.tenant_id = ").P(ctx.TenantId)
                .Raw(" and t.has_error = false and t.posting_date between ").P(period.From).Raw(" and ").P(period.To);
            if (accountId is { } id) sql.Raw(" and t.bank_account_id = ").P(id);
            sql.Raw(" group by 1, 2) q");
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" where (q.bucket_start, q.currency) > (").P(DateOnly.Parse(c[0]!)).Raw(", ").P(c[1]!).Raw(")");
            }
            sql.Raw(" order by q.bucket_start, q.currency limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<FlowRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit,
                r => new CashFlowBucket(r.BucketStart, r.Currency, r.Incoming, r.Outgoing, r.Incoming - r.Outgoing, r.Count),
                r => Cursor.Encode(r.BucketStart, r.Currency));
        }, ct);

    /// <summary>A5. Largest operations of the period, ordered by currency, then amount (descending).</summary>
    public Task<AnalyticsResult<Page<BankOperation>>> GetLargestAsync(
        DatePeriod period, FlowDirection? direction, string? currency, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var sql = OperationSelect()
                .Raw(" and t.posting_date between ").P(period.From).Raw(" and ").P(period.To);
            if (direction is { } d) sql.Raw(" and t.direction = ").P(d.ToString());
            if (currency is not null) sql.Raw(" and t.currency = ").P(currency);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 3);
                var cur = c[0]!; var amount = long.Parse(c[1]!); var id = Guid.Parse(c[2]!);
                sql.Raw(" and (t.currency > ").P(cur).Raw(" or (t.currency = ").P(cur).Raw(" and (t.amount < ").P(amount)
                    .Raw(" or (t.amount = ").P(amount).Raw(" and t.id > ").P(id).Raw("))))");
            }
            sql.Raw(" order by t.currency, t.amount desc, t.id limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<OperationRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit, Map, r => Cursor.Encode(r.Currency, r.Amount, r.Id));
        }, ct);

    /// <summary>A6. Operations of one day with their status (final, bank state, day closed).</summary>
    public Task<AnalyticsResult<Page<BankOperation>>> GetDayAsync(
        DateOnly day, Guid? accountId, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var sql = OperationSelect().Raw(" and t.posting_date = ").P(day);
            if (accountId is { } id) sql.Raw(" and t.bank_account_id = ").P(id);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (t.bank_account_id, t.id) > (").P(Guid.Parse(c[0]!)).Raw(", ").P(Guid.Parse(c[1]!)).Raw(")");
            }
            sql.Raw(" order by t.bank_account_id, t.id limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<OperationRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit, Map, r => Cursor.Encode(r.AccountId, r.Id));
        }, ct);

    /// <summary>A9. Accounts without any (error-free) operation for more than <paramref name="days"/> days.</summary>
    public Task<AnalyticsResult<Page<IdleAccount>>> GetIdleAccountsAsync(int days, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            ArgumentOutOfRangeException.ThrowIfNegative(days);
            var limit = ctx.Limit(page);
            var today = ctx.Today;
            var threshold = today.AddDays(-days);
            var sql = new Sql()
                .Raw("select a.id as account_id, a.account_number, a.name as account_name, a.currency, m.last_date as last_movement_date")
                .Raw(" from bank_accounts a left join lateral (select max(t.posting_date) as last_date from bank_transactions t")
                .Raw(" where t.tenant_id = a.tenant_id and t.bank_account_id = a.id and t.has_error = false) m on true")
                .Raw(" where a.tenant_id = ").P(ctx.TenantId)
                .Raw(" and (m.last_date is null or m.last_date < ").P(threshold).Raw(")");
            if (page?.Cursor is { } cursor)
                sql.Raw(" and a.account_number > ").P(Cursor.Decode(cursor, 1)[0]!);
            sql.Raw(" order by a.account_number limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<IdleRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit,
                r => new IdleAccount(r.AccountId, r.AccountNumber, r.AccountName, r.Currency, r.LastMovementDate,
                    r.LastMovementDate is { } last ? today.DayNumber - last.DayNumber : null),
                r => Cursor.Encode(r.AccountNumber));
        }, ct);

    private Sql OperationSelect() => new Sql()
        .Raw("select t.id, t.bank_account_id as account_id, a.account_number, t.operation_id, t.posting_date, t.value_date,")
        .Raw(" t.direction, t.amount, t.currency, t.counterparty_inn, t.counterparty_name, t.counterparty_account,")
        .Raw(" t.purpose, t.document_number, t.is_final, t.source_state, coalesce(b.is_day_closed, false) as is_day_closed")
        .Raw(" from bank_transactions t join bank_accounts a on a.tenant_id = t.tenant_id and a.id = t.bank_account_id")
        .Raw(" left join bank_daily_balances b on b.tenant_id = t.tenant_id and b.bank_account_id = t.bank_account_id and b.date = t.posting_date")
        .Raw(" where t.tenant_id = ").P(ctx.TenantId).Raw(" and t.has_error = false");

    internal static BankOperation Map(OperationRow r) => new(
        r.Id, r.AccountId, r.AccountNumber, r.OperationId, r.PostingDate, r.ValueDate,
        Enum.Parse<FlowDirection>(r.Direction), r.Amount, r.Currency,
        r.CounterpartyInn, r.CounterpartyName, r.CounterpartyAccount, r.Purpose, r.DocumentNumber,
        r.IsFinal, r.SourceState, r.IsDayClosed);

    internal sealed class OperationRow
    {
        public Guid Id { get; set; }
        public Guid AccountId { get; set; }
        public string AccountNumber { get; set; } = "";
        public string OperationId { get; set; } = "";
        public DateOnly PostingDate { get; set; }
        public DateOnly? ValueDate { get; set; }
        public string Direction { get; set; } = "";
        public long Amount { get; set; }
        public string Currency { get; set; } = "";
        public string? CounterpartyInn { get; set; }
        public string? CounterpartyName { get; set; }
        public string? CounterpartyAccount { get; set; }
        public string? Purpose { get; set; }
        public string? DocumentNumber { get; set; }
        public bool IsFinal { get; set; }
        public string? SourceState { get; set; }
        public bool IsDayClosed { get; set; }
    }

    private sealed class FlowRow
    {
        public DateOnly BucketStart { get; set; }
        public string Currency { get; set; } = "";
        public long Incoming { get; set; }
        public long Outgoing { get; set; }
        public int Count { get; set; }
    }

    private sealed class IdleRow
    {
        public Guid AccountId { get; set; }
        public string AccountNumber { get; set; } = "";
        public string? AccountName { get; set; }
        public string Currency { get; set; } = "";
        public DateOnly? LastMovementDate { get; set; }
    }
}
