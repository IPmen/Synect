using System.Globalization;
using Synect.Application.Analytics;

namespace Synect.Application.Accounting1C;

/// <summary>Which 1C documents a list shows by state.</summary>
public enum AccountingDocumentState
{
    /// <summary>Everything that is not marked deleted (posted and unposted).</summary>
    Active,
    Posted,
    Unposted,
    Deleted,
    All,
}

public sealed record AccountingDocumentFilter(
    DatePeriod? Period = null, string? Kind = null, string? CounterpartyTin = null,
    AccountingDocumentState State = AccountingDocumentState.Active, bool WithoutEsf = false, string? Number = null);

/// <summary>A sales or purchase document of 1C (accounting_documents).</summary>
public sealed record AccountingDocumentItem(
    string Ref, string DocKind, string? DocType, string Number, DateOnly Date, bool Posted, bool IsDeleted,
    string? CounterpartyTin, string? CounterpartyName, string? ContractNumber, string Currency, long AmountTotal, long? VatAmount,
    string? EsfNumber, DateOnly? EsfDate, string? EsfStatus, DateOnly? DueDate, string? Comment, DateTimeOffset ModifiedAt);

/// <summary>A payment order of 1C (accounting_payment_orders).</summary>
public sealed record AccountingPaymentOrderItem(
    string Ref, string Number, DateOnly Date, bool Posted, bool IsDeleted, string? CounterpartyTin, string? CounterpartyName,
    string? BankAccountNumber, string Currency, long Amount, string? Purpose, string? BankStatus, DateTimeOffset ModifiedAt);

/// <summary>Settlements with one counterparty in one currency by 1C: debit = they owe us, credit = we owe them.</summary>
public sealed record AccountingSettlementRow(
    string? CounterpartyRef, string? CounterpartyTin, string? CounterpartyName, string Currency, long Debit, long Credit, string GlAccounts)
{
    /// <summary>Positive: receivable (the counterparty owes us); negative: payable (we owe).</summary>
    public long Net => Debit - Credit;
}

public sealed record AccountingSettlementTotal(string Currency, long Receivable, long Payable, int Counterparties);

/// <param name="AsOfDate">Date of the 1C balances used (the latest export on or before the requested date); null when there is none.</param>
public sealed record AccountingSettlementReport(DateOnly? AsOfDate, IReadOnlyList<AccountingSettlementRow> Rows, IReadOnlyList<AccountingSettlementTotal> Totals);

public sealed record AccountingBankBalanceRow(
    string BankAccountRef, string? AccountNumber, string? BankName, string? Currency, DateOnly AsOfDate, long Balance,
    long? TurnoverDebit, long? TurnoverCredit);

/// <summary>
/// Read-only questions about the data received from 1C (accounting_* tables): documents, payment orders, settlement
/// balances, bank account balances. Reads only the Synect database in the current tenant context; deleted records are
/// left out unless asked for. Names, purposes and comments are third-party text (callers clean and wrap them).
/// </summary>
public sealed class AccountingQueries(AnalyticsContext ctx)
{
    /// <summary>1C documents (sales and purchases), newest first.</summary>
    public Task<AnalyticsResult<Page<AccountingDocumentItem>>> GetDocumentsAsync(AccountingDocumentFilter filter, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var sql = new Sql().Raw("""
                select d.ref, d.doc_kind, d.doc_type, d.number, d.date, d.posted, d.is_deleted,
                       coalesce(d.counterparty_tin, c.tin) as counterparty_tin, c.name as counterparty_name, d.contract_number, d.currency,
                       d.amount_total_minor as amount_total, d.vat_amount_minor as vat_amount, d.esf_number, d.esf_date, d.esf_status, d.due_date,
                       d.comment, d.modified_at
                from accounting_documents d
                left join accounting_counterparties c on c.tenant_id = d.tenant_id and c.ref = d.counterparty_ref
                where d.tenant_id =
                """).P(ctx.TenantId);
            StateFilter(sql, "d", filter.State);
            if (filter.Period is { } period) sql.Raw(" and d.date between ").P(period.From).Raw(" and ").P(period.To);
            if (filter.Kind is { } kind) sql.Raw(" and d.doc_kind = ").P(kind);
            if (filter.CounterpartyTin is { } tin) sql.Raw(" and coalesce(d.counterparty_tin, c.tin) = ").P(tin);
            if (filter.WithoutEsf) sql.Raw(" and d.esf_number is null and d.esf_id is null");
            if (filter.Number is { Length: > 0 } number) sql.Raw(" and (d.number = ").P(number).Raw(" or d.esf_number = ").P(number).Raw(")");
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (d.date, d.ref) < (").P(DateOnly.Parse(c[0]!, CultureInfo.InvariantCulture)).Raw(", ").P(c[1]!).Raw(")");
            }
            sql.Raw(" order by d.date desc, d.ref desc limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<DocumentRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit,
                r => new AccountingDocumentItem(r.Ref, r.DocKind, r.DocType, r.Number, r.Date, r.Posted, r.IsDeleted, r.CounterpartyTin, r.CounterpartyName,
                    r.ContractNumber, r.Currency, r.AmountTotal, r.VatAmount, r.EsfNumber, r.EsfDate, r.EsfStatus, r.DueDate, r.Comment, r.ModifiedAt),
                r => Cursor.Encode(r.Date, r.Ref));
        }, ct);

    /// <summary>1C payment orders, newest first.</summary>
    public Task<AnalyticsResult<Page<AccountingPaymentOrderItem>>> GetPaymentOrdersAsync(
        DatePeriod? period, string? counterpartyTin, AccountingDocumentState state, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var sql = new Sql().Raw("""
                select o.ref, o.number, o.date, o.posted, o.is_deleted, o.counterparty_tin,
                       (select c.name from accounting_counterparties c where c.tenant_id = o.tenant_id and c.tin = o.counterparty_tin and not c.is_deleted
                        order by c.ref limit 1) as counterparty_name,
                       a.number as bank_account_number, o.currency, o.amount_minor as amount, o.purpose, o.bank_status, o.modified_at
                from accounting_payment_orders o
                left join accounting_bank_accounts a on a.tenant_id = o.tenant_id and a.ref = o.bank_account_ref
                where o.tenant_id =
                """).P(ctx.TenantId);
            StateFilter(sql, "o", state);
            if (period is not null) sql.Raw(" and o.date between ").P(period.From).Raw(" and ").P(period.To);
            if (counterpartyTin is not null) sql.Raw(" and o.counterparty_tin = ").P(counterpartyTin);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (o.date, o.ref) < (").P(DateOnly.Parse(c[0]!, CultureInfo.InvariantCulture)).Raw(", ").P(c[1]!).Raw(")");
            }
            sql.Raw(" order by o.date desc, o.ref desc limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<PaymentOrderRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit,
                r => new AccountingPaymentOrderItem(r.Ref, r.Number, r.Date, r.Posted, r.IsDeleted, r.CounterpartyTin, r.CounterpartyName,
                    r.BankAccountNumber, r.Currency, r.Amount, r.Purpose, r.BankStatus, r.ModifiedAt),
                r => Cursor.Encode(r.Date, r.Ref));
        }, ct);

    /// <summary>
    /// Receivables and payables by 1C: the settlement balances of the latest export on or before <paramref name="asOf"/>
    /// (or the latest one), per counterparty and currency, largest first. Amounts of different currencies are never added.
    /// </summary>
    public Task<AnalyticsResult<AccountingSettlementReport>> GetSettlementBalancesAsync(DateOnly? asOf, string? counterpartyTin, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var date = await LatestDateAsync("accounting_settlement_balances", asOf, token);
            if (date is null) return new AccountingSettlementReport(null, [], []);
            var rows = await SettlementRowsAsync(date, counterpartyTin, token);
            var totals = rows.GroupBy(r => r.Currency, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new AccountingSettlementTotal(g.Key, g.Where(r => r.Net > 0).Sum(r => r.Net), -g.Where(r => r.Net < 0).Sum(r => r.Net), g.Count()))
                .ToList();
            return new AccountingSettlementReport(date, rows, totals);
        }, ct);

    /// <summary>Settlement rows of one export date (inside the caller's transaction); empty without a date.</summary>
    internal async Task<List<AccountingSettlementRow>> SettlementRowsAsync(DateOnly? date, string? counterpartyTin, CancellationToken ct)
    {
        if (date is null) return [];
        var sql = new Sql().Raw("""
            select b.counterparty_ref, coalesce(max(b.counterparty_tin), max(c.tin)) as counterparty_tin, max(c.name) as counterparty_name, b.currency,
                   coalesce(sum(b.balance_debit_minor), 0)::bigint as debit, coalesce(sum(b.balance_credit_minor), 0)::bigint as credit,
                   string_agg(distinct b.gl_account_code, ', ' order by b.gl_account_code) as gl_accounts
            from accounting_settlement_balances b
            left join accounting_counterparties c on c.tenant_id = b.tenant_id and c.ref = b.counterparty_ref
            where b.tenant_id =
            """).P(ctx.TenantId).Raw(" and not b.is_deleted and b.as_of_date = ").P(date.Value);
        if (counterpartyTin is not null) sql.Raw(" and coalesce(b.counterparty_tin, c.tin) = ").P(counterpartyTin);
        sql.Raw(" group by b.counterparty_ref, b.currency");
        return (await ctx.QueryAsync<SettlementRow>(sql, ct))
            .Select(r => new AccountingSettlementRow(r.CounterpartyRef, r.CounterpartyTin, r.CounterpartyName, r.Currency, r.Debit, r.Credit, r.GlAccounts))
            .Where(r => r.Debit != 0 || r.Credit != 0)
            .OrderByDescending(r => Math.Abs(r.Net)).ThenBy(r => r.CounterpartyTin, StringComparer.Ordinal).ThenBy(r => r.CounterpartyRef, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Balances of the organization's bank accounts by 1C on the latest export date on or before <paramref name="asOf"/>.</summary>
    public Task<AnalyticsResult<IReadOnlyList<AccountingBankBalanceRow>>> GetBankAccountBalancesAsync(DateOnly? asOf, CancellationToken ct) =>
        ctx.RunAsync<IReadOnlyList<AccountingBankBalanceRow>>(async token =>
        {
            var date = await LatestDateAsync("accounting_bank_account_balances", asOf, token);
            if (date is null) return [];
            var sql = new Sql().Raw("""
                select b.bank_account_ref, a.number as account_number, a.bank_name, a.currency, b.as_of_date, b.balance_minor as balance,
                       b.turnover_debit_minor as turnover_debit, b.turnover_credit_minor as turnover_credit
                from accounting_bank_account_balances b
                left join accounting_bank_accounts a on a.tenant_id = b.tenant_id and a.ref = b.bank_account_ref
                where b.tenant_id =
                """).P(ctx.TenantId).Raw(" and not b.is_deleted and b.as_of_date = ").P(date.Value).Raw(" order by a.number, b.bank_account_ref");
            return (await ctx.QueryAsync<BankBalanceRow>(sql, token))
                .Select(r => new AccountingBankBalanceRow(r.BankAccountRef, r.AccountNumber, r.BankName, r.Currency, r.AsOfDate, r.Balance, r.TurnoverDebit, r.TurnoverCredit))
                .ToList();
        }, ct);

    /// <summary>Latest as_of_date of a balance table on or before the given date (table name is a constant of this class).</summary>
    internal async Task<DateOnly?> LatestDateAsync(string table, DateOnly? asOf, CancellationToken ct)
    {
        var sql = new Sql().Raw("select max(as_of_date) as value from " + table + " where tenant_id = ").P(ctx.TenantId).Raw(" and not is_deleted");
        if (asOf is { } d) sql.Raw(" and as_of_date <= ").P(d);
        return (await ctx.QueryAsync<DateRow>(sql, ct)).Single().Value;
    }

    private static void StateFilter(Sql sql, string alias, AccountingDocumentState state)
    {
        switch (state)
        {
            case AccountingDocumentState.Active: sql.Raw($" and not {alias}.is_deleted"); break;
            case AccountingDocumentState.Posted: sql.Raw($" and not {alias}.is_deleted and {alias}.posted"); break;
            case AccountingDocumentState.Unposted: sql.Raw($" and not {alias}.is_deleted and not {alias}.posted"); break;
            case AccountingDocumentState.Deleted: sql.Raw($" and {alias}.is_deleted"); break;
        }
    }

    private sealed class DateRow
    {
        public DateOnly? Value { get; set; }
    }

    private sealed class DocumentRow
    {
        public string Ref { get; set; } = "";
        public string DocKind { get; set; } = "";
        public string? DocType { get; set; }
        public string Number { get; set; } = "";
        public DateOnly Date { get; set; }
        public bool Posted { get; set; }
        public bool IsDeleted { get; set; }
        public string? CounterpartyTin { get; set; }
        public string? CounterpartyName { get; set; }
        public string? ContractNumber { get; set; }
        public string Currency { get; set; } = "";
        public long AmountTotal { get; set; }
        public long? VatAmount { get; set; }
        public string? EsfNumber { get; set; }
        public DateOnly? EsfDate { get; set; }
        public string? EsfStatus { get; set; }
        public DateOnly? DueDate { get; set; }
        public string? Comment { get; set; }
        public DateTimeOffset ModifiedAt { get; set; }
    }

    private sealed class PaymentOrderRow
    {
        public string Ref { get; set; } = "";
        public string Number { get; set; } = "";
        public DateOnly Date { get; set; }
        public bool Posted { get; set; }
        public bool IsDeleted { get; set; }
        public string? CounterpartyTin { get; set; }
        public string? CounterpartyName { get; set; }
        public string? BankAccountNumber { get; set; }
        public string Currency { get; set; } = "";
        public long Amount { get; set; }
        public string? Purpose { get; set; }
        public string? BankStatus { get; set; }
        public DateTimeOffset ModifiedAt { get; set; }
    }

    private sealed class SettlementRow
    {
        public string? CounterpartyRef { get; set; }
        public string? CounterpartyTin { get; set; }
        public string? CounterpartyName { get; set; }
        public string Currency { get; set; } = "";
        public long Debit { get; set; }
        public long Credit { get; set; }
        public string GlAccounts { get; set; } = "";
    }

    private sealed class BankBalanceRow
    {
        public string BankAccountRef { get; set; } = "";
        public string? AccountNumber { get; set; }
        public string? BankName { get; set; }
        public string? Currency { get; set; }
        public DateOnly AsOfDate { get; set; }
        public long Balance { get; set; }
        public long? TurnoverDebit { get; set; }
        public long? TurnoverCredit { get; set; }
    }
}
