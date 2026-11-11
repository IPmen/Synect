using System.Text;
using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Application.Analytics;
using Synect.Application.Reconciliation;
using Synect.Domain.Attention;
using Synect.Domain.Sync;

namespace Synect.Application.Attention;

/// <summary>Rule codes (attention_items.rule_code).</summary>
public static class AttentionRuleCodes
{
    public const string ConnectionNeedsAttention = "connection.needs_attention";
    public const string ConnectionStaleData = "connection.stale_data";
    public const string ConnectionIncompleteLoad = "connection.incomplete_load";
    public const string BankBalanceMismatch = "bank.balance_mismatch";
    public const string BankFailedOperations = "bank.failed_operations";
    public const string BankIdleAccounts = "bank.idle_accounts";
    public const string PossibleDuplicatePayment = "reconciliation.possible_duplicate";
    public const string PaymentAccountMismatch = "reconciliation.account_mismatch";
    public const string LargeUnmatchedPayments = "reconciliation.large_unmatched_payments";
    public const string SignedUnpaidActs = "reconciliation.signed_unpaid_acts";
    public const string AwaitingOurSignature = "documents.awaiting_our_signature";
    public const string AwaitingPartnerSignature = "documents.awaiting_partner_signature";
    public const string RejectedRecently = "documents.rejected_recently";
}

/// <summary>
/// One case found by a rule. <see cref="Key"/> holds the significant parameters (ids, amount): a changed amount is a
/// different case. <see cref="Entry"/> is the case as listed in details (identifiers and amounts only, no third-party text).
/// </summary>
internal sealed record Finding(
    string Key, string EntityKind, string EntityRef, Dictionary<string, object?> Entry, IReadOnlyList<string>? Members = null);

/// <summary>What the rules read: the database of the current tenant (RLS), the evaluation time and the thresholds.</summary>
internal sealed record RuleContext(
    ISynectDbContext Db, Guid TenantId, DateTimeOffset Now, DateOnly Today, AttentionOptions Options,
    ReconciliationOptions Reconciliation, AnalyticsOptions Analytics)
{
    public Task<List<T>> QueryAsync<T>(Sql sql, CancellationToken ct) => Db.Database.SqlQuery<T>(sql.Build()).ToListAsync(ct);
    public DateOnly LookbackFrom => Today.AddDays(-Options.LookbackDays);
}

/// <param name="Grouped">Always one item for all cases (lists); otherwise one item per case up to Attention:GroupAbove.</param>
internal sealed record AttentionRule(
    string Code,
    AttentionCategory Category,
    AttentionSeverity Severity,
    bool Grouped,
    Func<RuleContext, CancellationToken, Task<List<Finding>>> Find,
    Func<RuleContext, Finding, (string Title, string Body)> Single,
    Func<RuleContext, IReadOnlyList<Finding>, (string Title, string Body)> Group,
    string? Note = null);

/// <summary>
/// The attention rules. They read only the Synect database (never a source). Every threshold is in Attention:* (see
/// <see cref="AttentionOptions"/>); dates are local (UTC+5) dates.
/// </summary>
internal static class AttentionRuleSet
{
    public const string BankAmountUnitNote =
        "The Kapital Bank amount unit (sum or tiyin) is not confirmed (Adapters:KapitalBank:AmountUnit); bank amounts and the threshold may be off by a factor of 100.";

    public static IReadOnlyList<AttentionRule> All { get; } =
    [
        new(AttentionRuleCodes.ConnectionNeedsAttention, AttentionCategory.Connection, AttentionSeverity.Critical, false, NeedsAttentionAsync,
            (_, f) => ($"The {f.Entry["kind"]} connection {f.Entry["source"]} needs attention: loads are stopped",
                "The connection is in the NeedsAttention state (authentication failed, or several loads in a row failed), so its data is not refreshed. " +
                "The reason is in data_freshness (status_reason)."),
            (_, l) => ($"{Count(l.Count, "connection")} need attention: loads are stopped", "")),
        new(AttentionRuleCodes.ConnectionStaleData, AttentionCategory.Connection, AttentionSeverity.High, false, StaleAsync,
            (c, f) => ($"Data of the {f.Entry["kind"]} connection {f.Entry["source"]} is older than {c.Options.StaleAfter.TotalHours:0} hours",
                f.Entry["last_successful_load_at"] is null
                    ? $"The active connection has never been loaded successfully although it exists for more than {c.Options.StaleAfter.TotalHours:0} hours (Attention:StaleAfter)."
                    : $"The last successful load was more than {c.Options.StaleAfter.TotalHours:0} hours ago (Attention:StaleAfter); answers about recent days may miss data."),
            (_, l) => ($"{Count(l.Count, "connection")} with stale data", "")),
        new(AttentionRuleCodes.ConnectionIncompleteLoad, AttentionCategory.Connection, AttentionSeverity.High, false, IncompleteAsync,
            (_, f) => ($"The last load of the {f.Entry["kind"]} connection {f.Entry["source"]} is incomplete",
                f.Entry["kind"] as string == "bank"
                    ? "The bank has not closed some days older than yesterday (is_day_closed = false); figures of those days may still change."
                    : "The last successful Didox load did not read every page; some documents may be missing."),
            (_, l) => ($"{Count(l.Count, "connection")} with incomplete loads", "")),

        new(AttentionRuleCodes.BankBalanceMismatch, AttentionCategory.Bank, AttentionSeverity.High, false, BalanceMismatchAsync,
            (c, f) => ($"Possible discrepancy in day balances of account {f.Entry["account"]}: {Count((int)f.Entry["day_count"]!, "day")}",
                $"On closed bank days of the last {c.Options.BalanceCheckDays} days, opening + credit - debit differs from the closing balance, or the day totals " +
                "differ from the sum of stored error-free operations (check_balance_integrity shows every check)."),
            (c, l) => ($"Possible discrepancies in day balances on {Count(l.Count, "account")}",
                $"Closed bank days of the last {c.Options.BalanceCheckDays} days where the day balance does not add up (see check_balance_integrity)."),
            BankAmountUnitNote),
        new(AttentionRuleCodes.BankFailedOperations, AttentionCategory.Bank, AttentionSeverity.Medium, true, FailedOperationsAsync,
            (c, f) => ("", ""),
            (c, l) => ($"{Count(l.Count, "bank operation")} rejected by the bank in the last {c.Options.FailedOperationsDays} days",
                "The bank reported an error for these operations (has_error); they did not move money and are excluded from balances and reconciliation."),
            BankAmountUnitNote),
        new(AttentionRuleCodes.BankIdleAccounts, AttentionCategory.Bank, AttentionSeverity.Low, true, IdleAccountsAsync,
            (c, f) => ("", ""),
            (c, l) => ($"{Count(l.Count, "bank account")} without movement for more than {c.Options.IdleAccountDays} days",
                $"No error-free operation for more than {c.Options.IdleAccountDays} days (Attention:IdleAccountDays), counted from the account's first appearance when it never moved.")),

        new(AttentionRuleCodes.PossibleDuplicatePayment, AttentionCategory.Reconciliation, AttentionSeverity.High, false, DuplicatesAsync,
            (_, f) => ($"Possible double payment: {Money(f.Entry)} to INN {f.Entry["inn"]} on {Date(f.Entry["posting_date"])}",
                $"The reconciliation found an earlier payment ({Date(f.Entry["earlier_posting_date"])}) of the same amount and direction to the same INN " +
                $"(confidence {f.Entry["confidence"]}; see get_possible_duplicate_payments for the reasons). This is a possible discrepancy, not a confirmed double payment."),
            (_, l) => ($"{Count(l.Count, "possible double payment")}",
                "Payments with an earlier payment of the same amount and direction to the same INN (see get_possible_duplicate_payments). Possible discrepancies, not confirmed double payments."),
            BankAmountUnitNote),
        new(AttentionRuleCodes.PaymentAccountMismatch, AttentionCategory.Reconciliation, AttentionSeverity.High, false, AccountMismatchesAsync,
            (_, f) => ($"Payment of {Money(f.Entry)} to INN {f.Entry["inn"]} went to account {f.Entry["payment_account"]}, the document names {f.Entry["seller_account"]}",
                "The payee account of an outgoing payment differs from sellerAccount of the incoming Didox document it is matched with " +
                "(see get_account_mismatches). A possible discrepancy: the supplier may use several accounts."),
            (_, l) => ($"{Count(l.Count, "payment")} to an account other than the document's sellerAccount",
                "Outgoing payments whose payee account differs from sellerAccount of the matched incoming document (see get_account_mismatches)."),
            BankAmountUnitNote),
        new(AttentionRuleCodes.LargeUnmatchedPayments, AttentionCategory.Reconciliation, AttentionSeverity.Medium, true, LargeUnmatchedAsync,
            (c, f) => ("", ""),
            (c, l) => ($"{Count(l.Count, "large payment")} without a matched document for more than {c.Options.UnmatchedGraceDays} days",
                $"Error-free {c.Reconciliation.Currency} payments of at least {MoneyText(c.Options.LargePaymentMinor, c.Reconciliation.Currency)} " +
                $"(Attention:LargePaymentMinor) from the last {c.Options.LookbackDays} days that are in no proposed or confirmed match " +
                $"{c.Options.UnmatchedGraceDays} days after the payment (see get_unmatched_payments)."),
            BankAmountUnitNote),
        new(AttentionRuleCodes.SignedUnpaidActs, AttentionCategory.Reconciliation, AttentionSeverity.Low, true, SignedUnpaidActsAsync,
            (c, f) => ("", ""),
            (c, l) => ($"{Count(l.Count, "signed act")} not fully paid after {c.Options.UnpaidActDays} days",
                $"Acts in the signed status dated more than {c.Options.UnpaidActDays} days ago (within the last {c.Options.LookbackDays} days) whose matched payments " +
                "do not cover the amount (see get_signed_unpaid_acts). Matches are possible matches; the accountant decides.")),

        new(AttentionRuleCodes.AwaitingOurSignature, AttentionCategory.Documents, AttentionSeverity.Medium, true,
            (c, ct) => WaitingAsync(c, "Incoming", "2", c.Options.AwaitingOurSignatureDays, ct),
            (c, f) => ("", ""),
            (c, l) => ($"{Count(l.Count, "incoming document")} waiting for our signature for more than {c.Options.AwaitingOurSignatureDays} days",
                "Incoming Didox documents in status 2 (awaiting our signature), by document date (see get_documents_awaiting_our_signature).")),
        new(AttentionRuleCodes.AwaitingPartnerSignature, AttentionCategory.Documents, AttentionSeverity.Low, true,
            (c, ct) => WaitingAsync(c, "Outgoing", "1", c.Options.AwaitingPartnerSignatureDays, ct),
            (c, f) => ("", ""),
            (c, l) => ($"{Count(l.Count, "outgoing document")} waiting for the partner's signature for more than {c.Options.AwaitingPartnerSignatureDays} days",
                "Outgoing Didox documents in status 1 (awaiting partner signature), by document date (see get_documents_awaiting_partner_signature).")),
        new(AttentionRuleCodes.RejectedRecently, AttentionCategory.Documents, AttentionSeverity.Medium, true, RejectedAsync,
            (c, f) => ("", ""),
            (c, l) => ($"{Count(l.Count, "document")} rejected in the last {c.Options.RejectedRecentDays} days",
                "Didox documents in status 4 (rejected) whose last change in Didox (or, without it, when Synect saw the rejection) is recent " +
                "(see get_rejected_documents for the rejection comments).")),
    ];

    // ---- Connections (LINQ: also readable on any provider) ---------------------------------------------------------

    private static Dictionary<string, object?> ConnectionEntry(Connection c) => new()
    {
        ["connection_id"] = c.Id,
        ["kind"] = c.AdapterKind switch { AdapterKind.Edo => "Didox", AdapterKind.File => "file_import", _ => c.AdapterKind.ToString().ToLowerInvariant() },
        ["source"] = c.SourceCode,
        ["status"] = c.Status.ToString(),
        ["status_changed_at"] = c.StatusChangedAt,
        ["last_successful_load_at"] = c.LastSuccessfulSyncAt,
        ["consecutive_failures"] = c.ConsecutiveFailures,
    };

    private static async Task<List<Finding>> NeedsAttentionAsync(RuleContext c, CancellationToken ct)
    {
        var rows = await c.Db.Connections.AsNoTracking().Where(x => x.Status == ConnectionStatus.NeedsAttention).OrderBy(x => x.Id).ToListAsync(ct);
        return rows.Select(x => new Finding(x.Id.ToString(), "connection", x.Id.ToString(), ConnectionEntry(x))).ToList();
    }

    /// <summary>Active connections only: a NeedsAttention connection already has a Critical item, a Disabled one is off on purpose.</summary>
    private static async Task<List<Finding>> StaleAsync(RuleContext c, CancellationToken ct)
    {
        // File import connections get data only when someone imports a statement: their own, longer threshold.
        TimeSpan Threshold(Connection x) => x.AdapterKind == AdapterKind.File ? c.Options.FileStaleAfter : c.Options.StaleAfter;
        var rows = await c.Db.Connections.AsNoTracking().Where(x => x.Status == ConnectionStatus.Active).OrderBy(x => x.Id).ToListAsync(ct);
        return rows.Where(x => (x.LastSuccessfulSyncAt ?? x.CreatedAt) < c.Now - Threshold(x))
            .Select(x =>
            {
                var entry = ConnectionEntry(x);
                entry["hours_since_last_load"] = x.LastSuccessfulSyncAt is { } at ? Math.Round((c.Now - at).TotalHours, 1) : null;
                entry["threshold_hours"] = Threshold(x).TotalHours;
                return new Finding(x.Id.ToString(), "connection", x.Id.ToString(), entry);
            }).ToList();
    }

    /// <summary>
    /// Bank: a day older than yesterday that the bank has not closed (today and yesterday are open in normal operation).
    /// Didox: the last successful load is marked incomplete (page limit).
    /// </summary>
    private static async Task<List<Finding>> IncompleteAsync(RuleContext c, CancellationToken ct)
    {
        var connections = await c.Db.Connections.AsNoTracking().Where(x => x.Status == ConnectionStatus.Active).OrderBy(x => x.Id).ToListAsync(ct);
        var result = new List<Finding>();
        // File imports store closed days only, so they are checked like bank connections.
        var bank = connections.Where(x => x.AdapterKind is AdapterKind.Bank or AdapterKind.File).Select(x => x.Id).ToList();
        if (bank.Count > 0)
        {
            var before = c.Today.AddDays(-1);
            var open = await (from b in c.Db.BankDailyBalances.AsNoTracking()
                              join a in c.Db.BankAccounts.AsNoTracking() on b.BankAccountId equals a.Id
                              where !b.IsDayClosed && b.Date < before && bank.Contains(a.ConnectionId)
                              select new { a.ConnectionId, b.Date }).ToListAsync(ct);
            foreach (var g in open.GroupBy(o => o.ConnectionId))
            {
                var connection = connections.Single(x => x.Id == g.Key);
                var days = g.Select(o => o.Date).Distinct().Order().ToList();
                var entry = ConnectionEntry(connection);
                entry["open_days"] = days.Count;
                entry["first_open_day"] = days[0];
                entry["last_open_day"] = days[^1];
                result.Add(new Finding(g.Key.ToString(), "connection", g.Key.ToString(), entry, days.Select(d => d.ToString("O")).ToList()));
            }
        }
        foreach (var x in connections.Where(x => x.AdapterKind is not (AdapterKind.Bank or AdapterKind.File)))
        {
            var last = await c.Db.SyncRuns.AsNoTracking()
                .Where(r => r.ConnectionId == x.Id && r.Result == SyncRunResult.Succeeded)
                .OrderByDescending(r => r.FinishedAt).Select(r => new { r.Id, r.IsComplete, r.FinishedAt }).FirstOrDefaultAsync(ct);
            if (last is null || last.IsComplete) continue;
            var entry = ConnectionEntry(x);
            entry["sync_run_id"] = last.Id;
            entry["load_finished_at"] = last.FinishedAt;
            result.Add(new Finding(x.Id.ToString(), "connection", x.Id.ToString(), entry));
        }
        return result.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    // ---- Bank ------------------------------------------------------------------------------------------------------

    /// <summary>The day equation and the day totals of closed days (the "saldo of the day"); see check_balance_integrity.</summary>
    private static async Task<List<Finding>> BalanceMismatchAsync(RuleContext c, CancellationToken ct)
    {
        var from = c.Today.AddDays(-c.Options.BalanceCheckDays + 1);
        var t = c.TenantId;
        var sql = new Sql()
            .Raw("with d as (select x.bank_account_id, x.date, x.opening_balance, x.closing_balance, x.total_debit, x.total_credit")
            .Raw(" from bank_daily_balances x where x.tenant_id = ").P(t).Raw(" and x.is_day_closed and x.date between ").P(from).Raw(" and ").P(c.Today).Raw("),")
            .Raw(" ops as (select o.bank_account_id, o.posting_date,")
            .Raw(" sum(case when o.direction = 'Incoming' then o.amount else 0 end)::bigint as incoming,")
            .Raw(" sum(case when o.direction = 'Outgoing' then o.amount else 0 end)::bigint as outgoing")
            .Raw(" from bank_transactions o where o.tenant_id = ").P(t).Raw(" and o.has_error = false and o.posting_date between ").P(from).Raw(" and ").P(c.Today)
            .Raw(" group by 1, 2)")
            .Raw(" select d.bank_account_id as account_id, a.account_number, a.currency, d.date,")
            .Raw(" (d.opening_balance + d.total_credit - d.total_debit - d.closing_balance)::bigint as day_difference,")
            .Raw(" (d.total_credit - coalesce(o.incoming, 0))::bigint as credit_difference, (d.total_debit - coalesce(o.outgoing, 0))::bigint as debit_difference")
            .Raw(" from d left join ops o on o.bank_account_id = d.bank_account_id and o.posting_date = d.date")
            .Raw(" join bank_accounts a on a.tenant_id = ").P(t).Raw(" and a.id = d.bank_account_id")
            .Raw(" where d.opening_balance + d.total_credit - d.total_debit <> d.closing_balance")
            .Raw(" or d.total_credit <> coalesce(o.incoming, 0) or d.total_debit <> coalesce(o.outgoing, 0)")
            .Raw(" order by d.bank_account_id, d.date");
        var rows = await c.QueryAsync<BalanceRow>(sql, ct);
        return rows.GroupBy(r => r.AccountId).Select(g =>
        {
            var first = g.First();
            var days = g.Select(r => new Dictionary<string, object?>
            {
                ["date"] = r.Date,
                ["day_difference_minor"] = r.DayDifference,
                ["credit_difference_minor"] = r.CreditDifference,
                ["debit_difference_minor"] = r.DebitDifference,
            }).ToList();
            var entry = new Dictionary<string, object?>
            {
                ["account_id"] = first.AccountId,
                ["account"] = Mask(first.AccountNumber),
                ["currency"] = first.Currency,
                ["day_count"] = days.Count,
                ["days"] = days.Take(c.Options.MaxListedEntries).ToList(),
            };
            return new Finding(first.AccountId.ToString(), "bank_account", first.AccountId.ToString(), entry,
                g.Select(r => $"{r.Date:O}:{r.DayDifference}:{r.CreditDifference}:{r.DebitDifference}").ToList());
        }).ToList();
    }

    private static async Task<List<Finding>> FailedOperationsAsync(RuleContext c, CancellationToken ct)
    {
        var from = c.Today.AddDays(-c.Options.FailedOperationsDays + 1);
        var sql = new Sql()
            .Raw("select t.id, a.account_number, t.posting_date, t.direction, t.amount, t.currency, t.counterparty_inn as inn, t.error_code")
            .Raw(" from bank_transactions t join bank_accounts a on a.tenant_id = t.tenant_id and a.id = t.bank_account_id")
            .Raw(" where t.tenant_id = ").P(c.TenantId).Raw(" and t.has_error and t.posting_date between ").P(from).Raw(" and ").P(c.Today)
            .Raw(" order by t.posting_date desc, t.id");
        var rows = await c.QueryAsync<PaymentRow>(sql, ct);
        return rows.Select(r => new Finding(r.Id.ToString(), "transaction", r.Id.ToString(), new()
        {
            ["transaction_id"] = r.Id,
            ["account"] = Mask(r.AccountNumber),
            ["posting_date"] = r.PostingDate,
            ["direction"] = r.Direction.ToLowerInvariant(),
            ["amount_minor"] = r.Amount,
            ["currency"] = r.Currency,
            ["inn"] = r.Inn,
            ["bank_error_code"] = r.ErrorCode,
        })).ToList();
    }

    private static async Task<List<Finding>> IdleAccountsAsync(RuleContext c, CancellationToken ct)
    {
        var threshold = c.Today.AddDays(-c.Options.IdleAccountDays);
        var sql = new Sql()
            .Raw("select a.id as account_id, a.account_number, a.currency, m.last_date, a.created_at")
            .Raw(" from bank_accounts a left join lateral (select max(t.posting_date) as last_date from bank_transactions t")
            .Raw(" where t.tenant_id = a.tenant_id and t.bank_account_id = a.id and t.has_error = false) m on true")
            .Raw(" where a.tenant_id = ").P(c.TenantId).Raw(" order by a.account_number");
        var rows = await c.QueryAsync<AccountRow>(sql, ct);
        return rows
            .Select(r => (Row: r, Since: r.LastDate ?? DateOnly.FromDateTime(r.CreatedAt.ToOffset(c.Analytics.UtcOffset).DateTime)))
            .Where(x => x.Since < threshold)
            .Select(x => new Finding(x.Row.AccountId.ToString(), "bank_account", x.Row.AccountId.ToString(), new()
            {
                ["account_id"] = x.Row.AccountId,
                ["account"] = Mask(x.Row.AccountNumber),
                ["currency"] = x.Row.Currency,
                ["last_movement_date"] = x.Row.LastDate,
                ["days_idle"] = c.Today.DayNumber - x.Since.DayNumber,
            })).ToList();
    }

    // ---- Reconciliation ----------------------------------------------------------------------------------------------

    /// <summary>Possible double payments of the reconciliation (G12) in the lookback period; a payment the accountant confirmed in a match is not reported.</summary>
    private static async Task<List<Finding>> DuplicatesAsync(RuleContext c, CancellationToken ct)
    {
        var sql = new Sql()
            .Raw("select b.id as payment_id, b.posting_date, a.id as earlier_payment_id, a.posting_date as earlier_posting_date, b.direction,")
            .Raw(" b.amount, b.currency, coalesce(b.counterparty_inn, '') as inn, f.confidence")
            .Raw(" from reconciliation_findings f join bank_transactions b on b.tenant_id = f.tenant_id and b.id = f.subject_id")
            .Raw(" join bank_transactions a on a.tenant_id = f.tenant_id and a.id = f.related_id")
            .Raw(" where f.tenant_id = ").P(c.TenantId).Raw(" and f.kind = 'PossibleDuplicate' and b.has_error = false and a.has_error = false")
            .Raw(" and b.posting_date >= ").P(c.LookbackFrom)
            .Raw(" and not exists (select 1 from reconciliation_match_items i where i.tenant_id = b.tenant_id and i.kind = 'BankTransaction'")
            .Raw(" and i.ref_id = b.id and i.match_status = 'Confirmed')")
            .Raw(" order by b.posting_date, b.id, a.id");
        var rows = await c.QueryAsync<DuplicateRow>(sql, ct);
        return rows.Select(r => new Finding($"{r.PaymentId}:{r.EarlierPaymentId}:{r.Amount}", "transaction", r.PaymentId.ToString(), new()
        {
            ["payment_id"] = r.PaymentId,
            ["posting_date"] = r.PostingDate,
            ["earlier_payment_id"] = r.EarlierPaymentId,
            ["earlier_posting_date"] = r.EarlierPostingDate,
            ["direction"] = r.Direction.ToLowerInvariant(),
            ["amount_minor"] = r.Amount,
            ["currency"] = r.Currency,
            ["inn"] = r.Inn,
            ["confidence"] = r.Confidence,
        })).ToList();
    }

    /// <summary>G11 in the lookback period: payee account differs from sellerAccount of the matched incoming document (not rejected).</summary>
    private static async Task<List<Finding>> AccountMismatchesAsync(RuleContext c, CancellationToken ct)
    {
        var sql = new Sql()
            .Raw("select m.id as match_id, m.status, m.counterparty_inn as inn, bt.id as payment_id, bt.posting_date, bt.amount, bt.currency,")
            .Raw(" regexp_replace(bt.counterparty_account, '[^0-9]', '', 'g') as payment_account, d.id as document_id, d.number as document_number,")
            .Raw(" d.document_date, regexp_replace(d.seller_account, '[^0-9]', '', 'g') as seller_account")
            .Raw(" from reconciliation_matches m")
            .Raw(" join reconciliation_match_items di on di.tenant_id = m.tenant_id and di.match_id = m.id and di.kind = 'EdoDocument'")
            .Raw(" join reconciliation_match_items pi on pi.tenant_id = m.tenant_id and pi.match_id = m.id and pi.kind = 'BankTransaction'")
            .Raw(" join edo_documents d on d.tenant_id = m.tenant_id and d.id = di.ref_id")
            .Raw(" join bank_transactions bt on bt.tenant_id = m.tenant_id and bt.id = pi.ref_id")
            .Raw(" where m.tenant_id = ").P(c.TenantId).Raw(" and m.status <> 'Rejected' and m.document_owner = 'Incoming'")
            .Raw(" and regexp_replace(coalesce(bt.counterparty_account, ''), '[^0-9]', '', 'g') <> ''")
            .Raw(" and regexp_replace(coalesce(d.seller_account, ''), '[^0-9]', '', 'g') <> ''")
            .Raw(" and regexp_replace(bt.counterparty_account, '[^0-9]', '', 'g') <> regexp_replace(d.seller_account, '[^0-9]', '', 'g')")
            .Raw(" and bt.posting_date >= ").P(c.LookbackFrom)
            .Raw(" order by bt.posting_date, bt.id, d.id");
        var rows = await c.QueryAsync<MismatchRow>(sql, ct);
        return rows.Select(r => new Finding($"{r.PaymentId}:{r.DocumentId}:{r.Amount}:{r.PaymentAccount}:{r.SellerAccount}", "transaction", r.PaymentId.ToString(), new()
        {
            ["payment_id"] = r.PaymentId,
            ["posting_date"] = r.PostingDate,
            ["amount_minor"] = r.Amount,
            ["currency"] = r.Currency,
            ["inn"] = r.Inn,
            ["payment_account"] = Mask(r.PaymentAccount),
            ["document_id"] = r.DocumentId,
            ["document_number"] = Code(r.DocumentNumber),
            ["document_date"] = r.DocumentDate,
            ["seller_account"] = Mask(r.SellerAccount),
            ["match_id"] = r.MatchId,
            ["match_status"] = r.Status.ToLowerInvariant(),
        })).ToList();
    }

    /// <summary>Large error-free payments in the reconciled currency that are in no proposed or confirmed match after the grace period.</summary>
    private static async Task<List<Finding>> LargeUnmatchedAsync(RuleContext c, CancellationToken ct)
    {
        var sql = new Sql()
            .Raw("select t.id, a.account_number, t.posting_date, t.direction, t.amount, t.currency, t.counterparty_inn as inn, t.error_code")
            .Raw(" from bank_transactions t join bank_accounts a on a.tenant_id = t.tenant_id and a.id = t.bank_account_id")
            .Raw(" where t.tenant_id = ").P(c.TenantId).Raw(" and t.has_error = false and t.currency = ").P(c.Reconciliation.Currency)
            .Raw(" and t.amount >= ").P(c.Options.LargePaymentMinor)
            .Raw(" and t.posting_date between ").P(c.LookbackFrom).Raw(" and ").P(c.Today.AddDays(-c.Options.UnmatchedGraceDays - 1))
            .Raw(" and not exists (select 1 from reconciliation_match_items i where i.tenant_id = t.tenant_id and i.kind = 'BankTransaction'")
            .Raw(" and i.ref_id = t.id and i.match_status <> 'Rejected')")
            .Raw(" order by t.amount desc, t.posting_date, t.id");
        var rows = await c.QueryAsync<PaymentRow>(sql, ct);
        return rows.Select(r => new Finding(r.Id.ToString(), "transaction", r.Id.ToString(), new()
        {
            ["payment_id"] = r.Id,
            ["account"] = Mask(r.AccountNumber),
            ["posting_date"] = r.PostingDate,
            ["days_unmatched"] = c.Today.DayNumber - r.PostingDate.DayNumber,
            ["direction"] = r.Direction.ToLowerInvariant(),
            ["amount_minor"] = r.Amount,
            ["currency"] = r.Currency,
            ["inn"] = string.IsNullOrWhiteSpace(r.Inn) ? null : r.Inn,
            ["without_inn"] = string.IsNullOrWhiteSpace(r.Inn),
        })).ToList();
    }

    /// <summary>G6 with an age: acts in the signed status, older than Attention:UnpaidActDays, whose non-rejected matches do not cover the amount.</summary>
    private static async Task<List<Finding>> SignedUnpaidActsAsync(RuleContext c, CancellationToken ct)
    {
        var t = c.TenantId;
        var o = c.Reconciliation;
        var sql = new Sql()
            .Raw("select * from (select d.id, d.number, d.document_date, d.counterparty_inn as inn, d.currency,")
            .Raw(" case when d.total_delivery_sum_with_vat > 0 then d.total_delivery_sum_with_vat else d.total_sum end as amount,")
            .Raw(" coalesce((select sum(i.allocated_amount_minor) from reconciliation_match_items i where i.tenant_id = d.tenant_id")
            .Raw(" and i.kind = 'EdoDocument' and i.ref_id = d.id and i.match_status <> 'Rejected'), 0)::bigint as paid,")
            .Raw(" null::timestamptz as rejected_at, d.owner from edo_documents d where d.tenant_id = ").P(t)
            .Raw(" and d.status = ").P(o.SignedStatus).Raw(" and d.status = any(").P(c.Analytics.Edo.CountedStatuses).Raw(")")
            .Raw(" and d.doc_type = any(").P(o.ActDocumentTypes).Raw(") and d.currency = ").P(o.Currency)
            .Raw(" and coalesce(d.counterparty_inn, '') <> '' and d.document_date between ").P(c.LookbackFrom)
            .Raw(" and ").P(c.Today.AddDays(-c.Options.UnpaidActDays - 1)).Raw(") x where x.paid < x.amount order by x.document_date, x.id");
        var rows = await c.QueryAsync<DocumentRow>(sql, ct);
        return rows.Select(r => new Finding($"{r.Id}:{r.Amount}", "document", r.Id.ToString(), new()
        {
            ["document_id"] = r.Id,
            ["number"] = Code(r.Number),
            ["document_date"] = r.DocumentDate,
            ["inn"] = r.Inn,
            ["amount_minor"] = r.Amount,
            ["paid_minor"] = r.Paid,
            ["currency"] = r.Currency,
        })).ToList();
    }

    // ---- Documents ---------------------------------------------------------------------------------------------------

    /// <summary>Documents of one owner and status whose document date (creation date without it) is older than <paramref name="days"/>.</summary>
    private static async Task<List<Finding>> WaitingAsync(RuleContext c, string owner, string status, int days, CancellationToken ct)
    {
        var sql = new Sql()
            .Raw("select * from (select d.id, d.number, coalesce(d.document_date, (d.source_created_at at time zone ").P(c.Analytics.UtcOffset)
            .Raw(")::date) as document_date, d.counterparty_inn as inn, d.currency, d.total_sum as amount, 0::bigint as paid,")
            .Raw(" null::timestamptz as rejected_at, d.owner")
            .Raw(" from edo_documents d where d.tenant_id = ").P(c.TenantId).Raw(" and d.owner = ").P(owner).Raw(" and d.status = ").P(status)
            .Raw(") x where x.document_date < ").P(c.Today.AddDays(-days)).Raw(" order by x.document_date, x.id");
        var rows = await c.QueryAsync<DocumentRow>(sql, ct);
        return rows.Select(r => new Finding(r.Id.ToString(), "document", r.Id.ToString(), new()
        {
            ["document_id"] = r.Id,
            ["number"] = Code(r.Number),
            ["document_date"] = r.DocumentDate,
            ["days_waiting"] = r.DocumentDate is { } d ? c.Today.DayNumber - d.DayNumber : null,
            ["inn"] = r.Inn,
            ["amount_minor"] = r.Amount,
            ["currency"] = r.Currency,
        })).ToList();
    }

    private static async Task<List<Finding>> RejectedAsync(RuleContext c, CancellationToken ct)
    {
        var since = c.Now.AddDays(-c.Options.RejectedRecentDays);
        var sql = new Sql()
            .Raw("select * from (select d.id, d.number, d.document_date, d.counterparty_inn as inn, d.currency, d.total_sum as amount, 0::bigint as paid,")
            .Raw(" coalesce(d.source_updated_at, (select max(l.recorded_at) from edo_document_status_logs l where l.tenant_id = d.tenant_id")
            .Raw(" and l.edo_document_id = d.id and l.status = '4')) as rejected_at, d.owner")
            .Raw(" from edo_documents d where d.tenant_id = ").P(c.TenantId).Raw(" and d.status = '4') x")
            .Raw(" where x.rejected_at >= ").P(since).Raw(" order by x.rejected_at desc, x.id");
        var rows = await c.QueryAsync<DocumentRow>(sql, ct);
        return rows.Select(r => new Finding(r.Id.ToString(), "document", r.Id.ToString(), new()
        {
            ["document_id"] = r.Id,
            ["number"] = Code(r.Number),
            ["document_date"] = r.DocumentDate,
            ["direction"] = r.Owner?.ToLowerInvariant(),
            ["rejected_at"] = r.RejectedAt,
            ["inn"] = r.Inn,
            ["amount_minor"] = r.Amount,
            ["currency"] = r.Currency,
        })).ToList();
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    /// <summary>Keeps the last 4 digits of an account: "****1234".</summary>
    public static string? Mask(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        var trimmed = account.Trim();
        return trimmed.Length <= 4 ? "****" : "****" + trimmed[^4..];
    }

    /// <summary>Document numbers come from Didox: only letters, digits and a few separators, at most 40 characters.</summary>
    public static string? Code(string? value)
    {
        if (value is null) return null;
        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '/' or '.' or '_' or ' ' or '№') sb.Append(ch);
            if (sb.Length >= 40) break;
        }
        return sb.ToString().Trim();
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Date(object? value) => value switch
    {
        DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset t => t.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture),
        null => "",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    public static string MoneyText(long minor, string currency) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{minor / 100m:0.00} {currency}");

    private static string Money(Dictionary<string, object?> entry) =>
        entry["amount_minor"] is long amount && entry["currency"] is string currency ? MoneyText(amount, currency) : "";

    // Row types for SqlQuery (settable properties, snake_case columns).
    private sealed class BalanceRow
    {
        public Guid AccountId { get; set; }
        public string AccountNumber { get; set; } = "";
        public string Currency { get; set; } = "";
        public DateOnly Date { get; set; }
        public long DayDifference { get; set; }
        public long CreditDifference { get; set; }
        public long DebitDifference { get; set; }
    }

    private sealed class PaymentRow
    {
        public Guid Id { get; set; }
        public string AccountNumber { get; set; } = "";
        public DateOnly PostingDate { get; set; }
        public string Direction { get; set; } = "";
        public long Amount { get; set; }
        public string Currency { get; set; } = "";
        public string? Inn { get; set; }
        public int? ErrorCode { get; set; }
    }

    private sealed class AccountRow
    {
        public Guid AccountId { get; set; }
        public string AccountNumber { get; set; } = "";
        public string Currency { get; set; } = "";
        public DateOnly? LastDate { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class DuplicateRow
    {
        public Guid PaymentId { get; set; }
        public DateOnly PostingDate { get; set; }
        public Guid EarlierPaymentId { get; set; }
        public DateOnly EarlierPostingDate { get; set; }
        public string Direction { get; set; } = "";
        public long Amount { get; set; }
        public string Currency { get; set; } = "";
        public string Inn { get; set; } = "";
        public int Confidence { get; set; }
    }

    private sealed class MismatchRow
    {
        public Guid MatchId { get; set; }
        public string Status { get; set; } = "";
        public string Inn { get; set; } = "";
        public Guid PaymentId { get; set; }
        public DateOnly PostingDate { get; set; }
        public long Amount { get; set; }
        public string Currency { get; set; } = "";
        public string? PaymentAccount { get; set; }
        public Guid DocumentId { get; set; }
        public string? DocumentNumber { get; set; }
        public DateOnly? DocumentDate { get; set; }
        public string? SellerAccount { get; set; }
    }

    private sealed class DocumentRow
    {
        public Guid Id { get; set; }
        public string? Number { get; set; }
        public DateOnly? DocumentDate { get; set; }
        public string? Inn { get; set; }
        public string Currency { get; set; } = "";
        public long Amount { get; set; }
        public long Paid { get; set; }
        public DateTimeOffset? RejectedAt { get; set; }
        public string? Owner { get; set; }
    }
}
