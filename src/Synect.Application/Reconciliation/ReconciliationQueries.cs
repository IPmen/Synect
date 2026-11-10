using System.Text.Json;
using Microsoft.Extensions.Options;
using Synect.Application.Analytics;
using Synect.Domain.Banking;
using Synect.Domain.Edo;
using Synect.Domain.Reconciliation;

namespace Synect.Application.Reconciliation;

/// <summary>Payment state of a document, from the non-rejected matches.</summary>
public enum DocumentPaymentState
{
    Paid,
    Unpaid,
    PartiallyPaid,
}

/// <summary>A match a payment or document takes part in. Reasons are the stored JSON array (code, weight, params).</summary>
public sealed record MatchReference(Guid MatchId, ReconciliationMatchType Type, MatchStatus Status, int Confidence, JsonElement Reasons, long AllocatedAmount);

/// <summary>Closest candidate below the threshold (a hint, never a match).</summary>
public sealed record CandidateHint(
    MatchItemKind Kind, Guid Id, int Confidence, JsonElement Reasons, string? DocumentNumber, DateOnly? Date, long? Amount, string? Currency);

/// <param name="DocumentDirection">Outgoing = we sell (expect an incoming payment); Incoming = we buy (expect an outgoing payment).</param>
/// <param name="ExpectedPaymentDirection">Bank direction of the payments that pay this document.</param>
public sealed record ReconciledDocument(
    Guid Id, string? Number, string DocType, string Status, EdoDocumentOwner DocumentDirection, TransactionDirection ExpectedPaymentDirection,
    DateOnly? DocumentDate, string Inn, string? CounterpartyName, string? ContractNumber, string Currency, long Amount, long PaidAmount,
    long RemainingAmount, DateOnly? LastPaymentDate, DocumentPaymentState State, IReadOnlyList<MatchReference> Matches, CandidateHint? NearestCandidate);

/// <param name="ExcludedReason">Why the payment is not matched automatically: without_inn, currency_not_reconciled, or null.</param>
/// <param name="CounterpartyHasDocuments">The INN has documents of a paid type (counted statuses) for this payment direction.</param>
public sealed record UnmatchedPayment(
    Guid Id, Guid AccountId, string AccountNumber, string OperationId, DateOnly PostingDate, TransactionDirection PaymentDirection,
    EdoDocumentOwner ExpectedDocumentDirection, long Amount, string Currency, string? Inn, string? CounterpartyName,
    string? CounterpartyAccount, string? Purpose, bool WithoutInn, string? ExcludedReason, bool CounterpartyHasDocuments,
    CandidateHint? NearestCandidate);

public sealed record MatchPaymentItem(
    Guid Id, string AccountNumber, string OperationId, DateOnly PostingDate, TransactionDirection Direction, long Amount, long AllocatedAmount,
    string Currency, string? CounterpartyAccount, string? Purpose);

public sealed record MatchDocumentItem(
    Guid Id, string? Number, string DocType, string Status, EdoDocumentOwner Direction, DateOnly? DocumentDate, long Amount,
    long AllocatedAmount, string Currency, string? ContractNumber, string? SellerAccount);

public sealed record MatchSummary(
    Guid Id, ReconciliationMatchType Type, MatchStatus Status, int Confidence, JsonElement Reasons, string Inn, string? CounterpartyName,
    string Currency, EdoDocumentOwner DocumentDirection, TransactionDirection PaymentDirection, long PaymentsTotal, long DocumentsTotal,
    long Difference, DateOnly? FirstPaymentDate, DateOnly? LastPaymentDate, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt,
    string? DecisionComment, IReadOnlyList<MatchPaymentItem> Payments, IReadOnlyList<MatchDocumentItem> Documents);

public sealed record MatchDetails(MatchSummary Match, Guid RunId, string RulesVersion, DateTimeOffset? RunFinishedAt, Guid? DecidedByKey);

public sealed record NoReferencePayment(
    Guid Id, string AccountNumber, DateOnly PostingDate, TransactionDirection PaymentDirection, long Amount, string Currency, string? Inn,
    string? CounterpartyName, string? Purpose, bool Matched);

/// <summary>
/// G10 / E11 per counterparty and document direction (E11 "in how many days on average does a client pay an invoice" =
/// the Outgoing direction). Based on the document date (the signing date is not in the Didox list).
/// </summary>
public sealed record PaymentDelay(
    string Inn, string? CounterpartyName, EdoDocumentOwner DocumentDirection, int Documents, int Links, decimal AverageDays,
    int MinDays, int MaxDays);

/// <summary>G10 / E11 over all counterparties of the filter, per document direction (never mixing receivables and payables).</summary>
/// <param name="AverageDays">Average over all payment-document links (each link counts once, so large counterparties weigh more).</param>
/// <param name="AverageOfCounterparties">Average of the per-counterparty averages (each counterparty counts once).</param>
public sealed record PaymentDelayOverall(
    EdoDocumentOwner DocumentDirection, int Counterparties, int Documents, int Links, decimal AverageDays,
    decimal AverageOfCounterparties, int MinDays, int MaxDays);

/// <summary>G10 / E11: one page of counterparties and the overall figures of the whole filter (not only of the page).</summary>
public sealed record PaymentDelayStats(Page<PaymentDelay> Counterparties, IReadOnlyList<PaymentDelayOverall> Overall);

public sealed record AccountMismatch(
    Guid MatchId, MatchStatus Status, int Confidence, string Inn, string? CounterpartyName, Guid PaymentId, DateOnly PostingDate,
    long Amount, string Currency, string? PaymentAccount, Guid DocumentId, string? DocumentNumber, DateOnly? DocumentDate, string? SellerAccount);

public sealed record DuplicatePayment(
    Guid PaymentId, DateOnly PostingDate, Guid EarlierPaymentId, DateOnly EarlierPostingDate, TransactionDirection PaymentDirection,
    long Amount, string Currency, string? Inn, string? CounterpartyName, string? Purpose, string? EarlierPurpose, int Confidence,
    JsonElement Reasons, bool PaymentMatched, bool EarlierPaymentMatched);

/// <summary>
/// E3 (and its extension to all counterparties): an ESTIMATE from bank and Didox data, not the accounting balance: documents of paid types in counted statuses up to
/// the date minus the payments matched to them (Proposed and Confirmed matches).
/// Receivable = our outgoing documents - matched incoming payments; Payable = incoming documents - matched outgoing payments.
/// </summary>
public sealed record BalanceEstimate(
    string Inn, string? CounterpartyName, string Currency, DateOnly AsOf,
    long OutgoingDocuments, long MatchedIncomingPayments, long Receivable,
    long IncomingDocuments, long MatchedOutgoingPayments, long Payable,
    long UnmatchedIncomingPayments, long UnmatchedOutgoingPayments, int UnpaidDocuments, int PartiallyPaidDocuments);

/// <summary>Filters shared by the document lists.</summary>
public sealed record DocumentFilter(DatePeriod Period, string? Inn = null, EdoDocumentOwner? Direction = null);

/// <summary>Filters of payment lists.</summary>
public sealed record PaymentFilter(DatePeriod Period, string? Inn = null, TransactionDirection? Direction = null, bool? WithoutInn = null);

/// <summary>
/// Block G (G1-G12), E3 and E11 over the stored reconciliation: reads only the Synect database, tenant from the context,
/// DataFreshness on every answer, keyset paging, local (UTC+5) dates, no mixing of currencies (documents are matched in
/// Reconciliation:Currency only). Matches are possible matches with a confidence; the accountant decides.
/// </summary>
public sealed class ReconciliationQueries(AnalyticsContext ctx, IOptions<ReconciliationOptions> options)
{
    private ReconciliationOptions O => options.Value;

    // ---- G1, G2, G3, G6: documents by payment state -----------------------------------------------------------------

    public Task<AnalyticsResult<Page<ReconciledDocument>>> GetPaidDocumentsAsync(DocumentFilter filter, PageRequest? page, CancellationToken ct) =>
        DocumentsAsync(filter, "paid >= amount", actsOnly: false, page, ct);

    public Task<AnalyticsResult<Page<ReconciledDocument>>> GetUnpaidDocumentsAsync(DocumentFilter filter, PageRequest? page, CancellationToken ct) =>
        DocumentsAsync(filter, "paid = 0", actsOnly: false, page, ct);

    public Task<AnalyticsResult<Page<ReconciledDocument>>> GetPartiallyPaidDocumentsAsync(DocumentFilter filter, PageRequest? page, CancellationToken ct) =>
        DocumentsAsync(filter, "paid > 0 and paid < amount", actsOnly: false, page, ct);

    /// <summary>G6. Acts in the signed status that are not fully paid (unpaid or partially paid).</summary>
    public Task<AnalyticsResult<Page<ReconciledDocument>>> GetSignedUnpaidActsAsync(DocumentFilter filter, PageRequest? page, CancellationToken ct) =>
        DocumentsAsync(filter, "paid < amount", actsOnly: true, page, ct);

    private Task<AnalyticsResult<Page<ReconciledDocument>>> DocumentsAsync(
        DocumentFilter filter, string stateCondition, bool actsOnly, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var t = ctx.TenantId;
            var sql = new Sql()
                .Raw("with docs as (select d.id, d.number, d.doc_type, d.status, d.owner, d.document_date, d.counterparty_inn, d.counterparty_name,")
                .Raw(" d.contract_number, d.currency, case when d.total_delivery_sum_with_vat > 0 then d.total_delivery_sum_with_vat else d.total_sum end as amount")
                .Raw(" from edo_documents d where d.tenant_id = ").P(t)
                .Raw(" and d.status = any(").P(ctx.Options.Edo.CountedStatuses).Raw(") and d.doc_type = any(").P(actsOnly ? O.ActDocumentTypes : O.DocumentTypes).Raw(")")
                .Raw(" and d.currency = ").P(O.Currency).Raw(" and coalesce(d.counterparty_inn, '') <> ''")
                .Raw(" and d.document_date between ").P(filter.Period.From).Raw(" and ").P(filter.Period.To);
            if (actsOnly) sql.Raw(" and d.status = ").P(O.SignedStatus);
            if (filter.Inn is not null) sql.Raw(" and d.counterparty_inn = ").P(filter.Inn);
            if (filter.Direction is { } dir) sql.Raw(" and d.owner = ").P(dir.ToString());
            sql.Raw("), state as (select docs.*, coalesce((select sum(i.allocated_amount_minor) from reconciliation_match_items i where i.tenant_id = ").P(t)
                .Raw(" and i.kind = 'EdoDocument' and i.ref_id = docs.id and i.match_status <> 'Rejected'), 0)::bigint as paid,")
                .Raw(" (select max(bt.posting_date) from reconciliation_match_items di join reconciliation_match_items pi on pi.tenant_id = di.tenant_id")
                .Raw(" and pi.match_id = di.match_id and pi.kind = 'BankTransaction' join bank_transactions bt on bt.tenant_id = pi.tenant_id and bt.id = pi.ref_id")
                .Raw(" where di.tenant_id = ").P(t).Raw(" and di.kind = 'EdoDocument' and di.ref_id = docs.id and di.match_status <> 'Rejected') as last_payment_date")
                .Raw(" from docs) select s.id, s.number, s.doc_type, s.status, s.owner, s.document_date, s.counterparty_inn as inn,")
                .Raw(" coalesce(c.name, s.counterparty_name) as counterparty_name, s.contract_number, s.currency, s.amount, s.paid, s.last_payment_date")
                .Raw(" from state s left join counterparties c on c.tenant_id = ").P(t).Raw(" and c.inn = s.counterparty_inn where ").Raw(stateCondition);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (s.document_date, s.id) > (").P(DateOnly.Parse(c[0]!)).Raw(", ").P(Guid.Parse(c[1]!)).Raw(")");
            }
            sql.Raw(" order by s.document_date, s.id limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<DocumentRow>(sql, token);
            var pageRows = rows.Take(limit).ToList();
            var ids = pageRows.Select(r => r.Id).ToArray();
            var matches = await MatchReferencesAsync(MatchItemKind.EdoDocument, ids, token);
            var hints = await HintsAsync(MatchItemKind.EdoDocument, ids, token);
            return AnalyticsContext.ToPage(rows, limit, r =>
            {
                var owner = Enum.Parse<EdoDocumentOwner>(r.Owner);
                var state = r.Paid >= r.Amount ? DocumentPaymentState.Paid : r.Paid == 0 ? DocumentPaymentState.Unpaid : DocumentPaymentState.PartiallyPaid;
                return new ReconciledDocument(r.Id, r.Number, r.DocType, r.Status, owner, PaymentDirectionFor(owner), r.DocumentDate, r.Inn,
                    r.CounterpartyName, r.ContractNumber, r.Currency, r.Amount, r.Paid, Math.Max(0, r.Amount - r.Paid), r.LastPaymentDate, state,
                    matches[r.Id].ToList(), hints.GetValueOrDefault(r.Id));
            }, r => Cursor.Encode(r.DocumentDate, r.Id));
        }, ct);

    // ---- G4, G5: unmatched payments ---------------------------------------------------------------------------------

    /// <summary>G4 (and G5 with Direction = Outgoing): error-free payments of the period that are in no Proposed or Confirmed match.</summary>
    public Task<AnalyticsResult<Page<UnmatchedPayment>>> GetUnmatchedPaymentsAsync(PaymentFilter filter, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var t = ctx.TenantId;
            var sql = new Sql()
                .Raw("select t.id, t.bank_account_id as account_id, a.account_number, t.operation_id, t.posting_date, t.direction, t.amount, t.currency,")
                .Raw(" t.counterparty_inn as inn, coalesce(c.name, t.counterparty_name) as counterparty_name, t.counterparty_account, t.purpose,")
                .Raw(" exists (select 1 from edo_documents d where d.tenant_id = ").P(t).Raw(" and d.counterparty_inn = t.counterparty_inn")
                .Raw(" and d.status = any(").P(ctx.Options.Edo.CountedStatuses).Raw(") and d.doc_type = any(").P(O.DocumentTypes).Raw(")")
                .Raw(" and d.owner = case when t.direction = 'Incoming' then 'Outgoing' else 'Incoming' end) as counterparty_has_documents")
                .Raw(" from bank_transactions t join bank_accounts a on a.tenant_id = t.tenant_id and a.id = t.bank_account_id")
                .Raw(" left join counterparties c on c.tenant_id = t.tenant_id and c.inn = t.counterparty_inn")
                .Raw(" where t.tenant_id = ").P(t).Raw(" and t.has_error = false and t.posting_date between ").P(filter.Period.From).Raw(" and ").P(filter.Period.To)
                .Raw(" and not exists (select 1 from reconciliation_match_items i where i.tenant_id = ").P(t)
                .Raw(" and i.kind = 'BankTransaction' and i.ref_id = t.id and i.match_status <> 'Rejected')");
            PaymentFilters(sql, filter);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (t.posting_date, t.id) > (").P(DateOnly.Parse(c[0]!)).Raw(", ").P(Guid.Parse(c[1]!)).Raw(")");
            }
            sql.Raw(" order by t.posting_date, t.id limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<PaymentRow>(sql, token);
            var hints = await HintsAsync(MatchItemKind.BankTransaction, rows.Take(limit).Select(r => r.Id).ToArray(), token);
            return AnalyticsContext.ToPage(rows, limit, r =>
            {
                var direction = Enum.Parse<TransactionDirection>(r.Direction);
                var withoutInn = string.IsNullOrWhiteSpace(r.Inn);
                var excluded = withoutInn ? "without_inn" : r.Currency != O.Currency ? "currency_not_reconciled" : null;
                return new UnmatchedPayment(r.Id, r.AccountId, r.AccountNumber, r.OperationId, r.PostingDate, direction, DocumentDirectionFor(direction),
                    r.Amount, r.Currency, withoutInn ? null : r.Inn, r.CounterpartyName, r.CounterpartyAccount, r.Purpose, withoutInn, excluded,
                    r.CounterpartyHasDocuments, hints.GetValueOrDefault(r.Id));
            }, r => Cursor.Encode(r.PostingDate, r.Id));
        }, ct);

    // ---- G7, G8: matches by shape -----------------------------------------------------------------------------------

    /// <summary>G7. One payment for several documents (OneToMany) and several payments for one document (ManyToOne).</summary>
    public Task<AnalyticsResult<Page<MatchSummary>>> GetMultiDocumentMatchesAsync(
        DatePeriod period, string? inn, MatchStatus? status, PageRequest? page, CancellationToken ct) =>
        MatchesAsync(period, inn, status, " and m.match_type in ('OneToMany', 'ManyToOne')", page, ct);

    /// <summary>G8. Matches whose payments and documents differ in total (partial payments and overpayments).</summary>
    public Task<AnalyticsResult<Page<MatchSummary>>> GetAmountDifferencesAsync(
        DatePeriod period, string? inn, MatchStatus? status, PageRequest? page, CancellationToken ct) =>
        MatchesAsync(period, inn, status, " and m.payments_total <> m.documents_total", page, ct);

    private Task<AnalyticsResult<Page<MatchSummary>>> MatchesAsync(
        DatePeriod period, string? inn, MatchStatus? status, string condition, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var t = ctx.TenantId;
            var sql = new Sql()
                .Raw("select * from (select m.id, (select min(bt.posting_date) from reconciliation_match_items i join bank_transactions bt")
                .Raw(" on bt.tenant_id = i.tenant_id and bt.id = i.ref_id where i.tenant_id = m.tenant_id and i.match_id = m.id and i.kind = 'BankTransaction') as first_payment_date")
                .Raw(" from reconciliation_matches m where m.tenant_id = ").P(t).Raw(condition);
            sql.Raw(status is { } s ? " and m.status = " : " and m.status <> 'Rejected'");
            if (status is { } st) sql.P(st.ToString());
            if (inn is not null) sql.Raw(" and m.counterparty_inn = ").P(inn);
            sql.Raw(") x where x.first_payment_date between ").P(period.From).Raw(" and ").P(period.To);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (x.first_payment_date, x.id) > (").P(DateOnly.Parse(c[0]!)).Raw(", ").P(Guid.Parse(c[1]!)).Raw(")");
            }
            sql.Raw(" order by x.first_payment_date, x.id limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<MatchKeyRow>(sql, token);
            var summaries = await SummariesAsync(rows.Take(limit).Select(r => r.Id).ToArray(), token);
            return AnalyticsContext.ToPage(rows, limit, r => summaries[r.Id], r => Cursor.Encode(r.FirstPaymentDate, r.Id));
        }, ct);

    /// <summary>Everything about one match: payments, documents, reasons, decision, the run that last produced it.</summary>
    public Task<AnalyticsResult<MatchDetails?>> GetMatchDetailsAsync(Guid matchId, CancellationToken ct) =>
        ctx.RunAsync<MatchDetails?>(async token =>
        {
            var summaries = await SummariesAsync([matchId], token);
            if (!summaries.TryGetValue(matchId, out var summary)) return null;
            var run = (await ctx.QueryAsync<RunRow>(new Sql()
                .Raw("select m.run_id, r.rules_version, r.finished_at, m.decided_by_key from reconciliation_matches m")
                .Raw(" join reconciliation_runs r on r.tenant_id = m.tenant_id and r.id = m.run_id where m.tenant_id = ").P(ctx.TenantId)
                .Raw(" and m.id = ").P(matchId), token)).Single();
            return new MatchDetails(summary, run.RunId, run.RulesVersion, run.FinishedAt, run.DecidedByKey);
        }, ct);

    // ---- G9: payments without a document or contract number ---------------------------------------------------------

    public Task<AnalyticsResult<Page<NoReferencePayment>>> GetPaymentsWithoutReferenceAsync(PaymentFilter filter, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var t = ctx.TenantId;
            var sql = new Sql()
                .Raw("select t.id, a.account_number, t.posting_date, t.direction, t.amount, t.currency, t.counterparty_inn as inn,")
                .Raw(" coalesce(c.name, t.counterparty_name) as counterparty_name, t.purpose,")
                .Raw(" exists (select 1 from reconciliation_match_items i where i.tenant_id = t.tenant_id and i.kind = 'BankTransaction'")
                .Raw(" and i.ref_id = t.id and i.match_status <> 'Rejected') as matched")
                .Raw(" from reconciliation_findings f join bank_transactions t on t.tenant_id = f.tenant_id and t.id = f.subject_id")
                .Raw(" join bank_accounts a on a.tenant_id = t.tenant_id and a.id = t.bank_account_id")
                .Raw(" left join counterparties c on c.tenant_id = t.tenant_id and c.inn = t.counterparty_inn")
                .Raw(" where f.tenant_id = ").P(t).Raw(" and f.kind = 'NoReference' and t.has_error = false")
                .Raw(" and t.posting_date between ").P(filter.Period.From).Raw(" and ").P(filter.Period.To);
            PaymentFilters(sql, filter);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (t.posting_date, t.id) > (").P(DateOnly.Parse(c[0]!)).Raw(", ").P(Guid.Parse(c[1]!)).Raw(")");
            }
            sql.Raw(" order by t.posting_date, t.id limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<NoReferenceRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit,
                r => new NoReferencePayment(r.Id, r.AccountNumber, r.PostingDate, Enum.Parse<TransactionDirection>(r.Direction), r.Amount, r.Currency,
                    string.IsNullOrWhiteSpace(r.Inn) ? null : r.Inn, r.CounterpartyName, r.Purpose, r.Matched),
                r => Cursor.Encode(r.PostingDate, r.Id));
        }, ct);

    // ---- G10 / E11: days from the document date to payment --------------------------------------------------------

    /// <summary>
    /// G10 and E11 ("за сколько дней в среднем клиент оплачивает ЭСФ"): days from the document date to the posting date of
    /// the payments matched to it (Proposed and Confirmed matches, payments in the period), per counterparty and document
    /// direction, plus the overall figures per direction. The document date is used because the Didox list gives no
    /// confirmed signing date.
    /// </summary>
    public Task<AnalyticsResult<PaymentDelayStats>> GetPaymentDelayStatsAsync(
        DatePeriod period, string? inn, EdoDocumentOwner? direction, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var sql = DelayLinks(period, inn, direction)
                .Raw(" select inn, max(counterparty_name) as counterparty_name, owner,")
                .Raw(" count(distinct document_id)::int as documents, count(*)::int as links,")
                .Raw(" round(avg(days), 1)::numeric as average_days, min(days)::int as min_days, max(days)::int as max_days")
                .Raw(" from links");
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" where (inn, owner) > (").P(c[0]!).Raw(", ").P(c[1]!).Raw(")");
            }
            sql.Raw(" group by inn, owner order by inn, owner limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<DelayRow>(sql, token);
            var counterparties = AnalyticsContext.ToPage(rows, limit,
                r => new PaymentDelay(r.Inn, r.CounterpartyName, Enum.Parse<EdoDocumentOwner>(r.Owner), r.Documents, r.Links, r.AverageDays, r.MinDays, r.MaxDays),
                r => Cursor.Encode(r.Inn, r.Owner));

            var overallSql = DelayLinks(period, inn, direction)
                .Raw(", per_inn as (select inn, owner, avg(days) as average from links group by inn, owner)")
                .Raw(" select l.owner, count(distinct l.inn)::int as counterparties, count(distinct l.document_id)::int as documents,")
                .Raw(" count(*)::int as links, round(avg(l.days), 1)::numeric as average_days,")
                .Raw(" (select round(avg(p.average), 1) from per_inn p where p.owner = l.owner)::numeric as average_of_counterparties,")
                .Raw(" min(l.days)::int as min_days, max(l.days)::int as max_days")
                .Raw(" from links l group by l.owner order by l.owner");
            var overall = (await ctx.QueryAsync<DelayOverallRow>(overallSql, token))
                .Select(r => new PaymentDelayOverall(Enum.Parse<EdoDocumentOwner>(r.Owner), r.Counterparties, r.Documents, r.Links,
                    r.AverageDays, r.AverageOfCounterparties, r.MinDays, r.MaxDays))
                .ToList();
            return new PaymentDelayStats(counterparties, overall);
        }, ct);

    /// <summary>CTE "links": one row per payment-document pair of a non-rejected match, with the days between them.</summary>
    private Sql DelayLinks(DatePeriod period, string? inn, EdoDocumentOwner? direction)
    {
        var t = ctx.TenantId;
        var sql = new Sql()
            .Raw("with links as (select m.counterparty_inn as inn, c.name as counterparty_name, m.document_owner as owner,")
            .Raw(" d.id as document_id, bt.posting_date - d.document_date as days")
            .Raw(" from reconciliation_matches m")
            .Raw(" join reconciliation_match_items di on di.tenant_id = m.tenant_id and di.match_id = m.id and di.kind = 'EdoDocument'")
            .Raw(" join reconciliation_match_items pi on pi.tenant_id = m.tenant_id and pi.match_id = m.id and pi.kind = 'BankTransaction'")
            .Raw(" join edo_documents d on d.tenant_id = m.tenant_id and d.id = di.ref_id")
            .Raw(" join bank_transactions bt on bt.tenant_id = m.tenant_id and bt.id = pi.ref_id")
            .Raw(" left join counterparties c on c.tenant_id = m.tenant_id and c.inn = m.counterparty_inn")
            .Raw(" where m.tenant_id = ").P(t).Raw(" and m.status <> 'Rejected' and d.document_date is not null")
            .Raw(" and bt.posting_date between ").P(period.From).Raw(" and ").P(period.To);
        if (inn is not null) sql.Raw(" and m.counterparty_inn = ").P(inn);
        if (direction is { } dir) sql.Raw(" and m.document_owner = ").P(dir.ToString());
        return sql.Raw(")");
    }

    // ---- G11: payee account differs from sellerAccount --------------------------------------------------------------

    public Task<AnalyticsResult<Page<AccountMismatch>>> GetAccountMismatchesAsync(DatePeriod period, string? inn, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var t = ctx.TenantId;
            var sql = new Sql()
                .Raw("select m.id as match_id, m.status, m.confidence, m.counterparty_inn as inn, c.name as counterparty_name,")
                .Raw(" bt.id as payment_id, bt.posting_date, bt.amount, bt.currency, bt.counterparty_account as payment_account,")
                .Raw(" d.id as document_id, d.number as document_number, d.document_date, d.seller_account")
                .Raw(" from reconciliation_matches m")
                .Raw(" join reconciliation_match_items di on di.tenant_id = m.tenant_id and di.match_id = m.id and di.kind = 'EdoDocument'")
                .Raw(" join reconciliation_match_items pi on pi.tenant_id = m.tenant_id and pi.match_id = m.id and pi.kind = 'BankTransaction'")
                .Raw(" join edo_documents d on d.tenant_id = m.tenant_id and d.id = di.ref_id")
                .Raw(" join bank_transactions bt on bt.tenant_id = m.tenant_id and bt.id = pi.ref_id")
                .Raw(" left join counterparties c on c.tenant_id = m.tenant_id and c.inn = m.counterparty_inn")
                .Raw(" where m.tenant_id = ").P(t).Raw(" and m.status <> 'Rejected' and m.document_owner = 'Incoming'")
                .Raw(" and regexp_replace(coalesce(bt.counterparty_account, ''), '[^0-9]', '', 'g') <> ''")
                .Raw(" and regexp_replace(coalesce(d.seller_account, ''), '[^0-9]', '', 'g') <> ''")
                .Raw(" and regexp_replace(bt.counterparty_account, '[^0-9]', '', 'g') <> regexp_replace(d.seller_account, '[^0-9]', '', 'g')")
                .Raw(" and bt.posting_date between ").P(period.From).Raw(" and ").P(period.To);
            if (inn is not null) sql.Raw(" and m.counterparty_inn = ").P(inn);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 3);
                sql.Raw(" and (bt.posting_date, bt.id, d.id) > (").P(DateOnly.Parse(c[0]!)).Raw(", ").P(Guid.Parse(c[1]!)).Raw(", ").P(Guid.Parse(c[2]!)).Raw(")");
            }
            sql.Raw(" order by bt.posting_date, bt.id, d.id limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<MismatchRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit,
                r => new AccountMismatch(r.MatchId, Enum.Parse<MatchStatus>(r.Status), r.Confidence, r.Inn, r.CounterpartyName, r.PaymentId,
                    r.PostingDate, r.Amount, r.Currency, r.PaymentAccount, r.DocumentId, r.DocumentNumber, r.DocumentDate, r.SellerAccount),
                r => Cursor.Encode(r.PostingDate, r.PaymentId, r.DocumentId));
        }, ct);

    // ---- G12: possible double payments ------------------------------------------------------------------------------

    public Task<AnalyticsResult<Page<DuplicatePayment>>> GetPossibleDuplicatePaymentsAsync(DatePeriod period, string? inn, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var t = ctx.TenantId;
            var sql = new Sql()
                .Raw("select b.id as payment_id, b.posting_date, a.id as earlier_payment_id, a.posting_date as earlier_posting_date, b.direction,")
                .Raw(" b.amount, b.currency, b.counterparty_inn as inn, coalesce(c.name, b.counterparty_name) as counterparty_name, b.purpose,")
                .Raw(" a.purpose as earlier_purpose, f.confidence, f.reasons::text as reasons,")
                .Raw(" exists (select 1 from reconciliation_match_items i where i.tenant_id = b.tenant_id and i.kind = 'BankTransaction' and i.ref_id = b.id and i.match_status <> 'Rejected') as payment_matched,")
                .Raw(" exists (select 1 from reconciliation_match_items i where i.tenant_id = a.tenant_id and i.kind = 'BankTransaction' and i.ref_id = a.id and i.match_status <> 'Rejected') as earlier_payment_matched")
                .Raw(" from reconciliation_findings f join bank_transactions b on b.tenant_id = f.tenant_id and b.id = f.subject_id")
                .Raw(" join bank_transactions a on a.tenant_id = f.tenant_id and a.id = f.related_id")
                .Raw(" left join counterparties c on c.tenant_id = b.tenant_id and c.inn = b.counterparty_inn")
                .Raw(" where f.tenant_id = ").P(t).Raw(" and f.kind = 'PossibleDuplicate' and b.has_error = false and a.has_error = false")
                .Raw(" and b.posting_date between ").P(period.From).Raw(" and ").P(period.To);
            if (inn is not null) sql.Raw(" and b.counterparty_inn = ").P(inn);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (b.posting_date, b.id) > (").P(DateOnly.Parse(c[0]!)).Raw(", ").P(Guid.Parse(c[1]!)).Raw(")");
            }
            sql.Raw(" order by b.posting_date, b.id limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<DuplicateRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit,
                r => new DuplicatePayment(r.PaymentId, r.PostingDate, r.EarlierPaymentId, r.EarlierPostingDate, Enum.Parse<TransactionDirection>(r.Direction),
                    r.Amount, r.Currency, r.Inn, r.CounterpartyName, r.Purpose, r.EarlierPurpose, r.Confidence, Json(r.Reasons),
                    r.PaymentMatched, r.EarlierPaymentMatched),
                r => Cursor.Encode(r.PostingDate, r.PaymentId));
        }, ct);

    // ---- E3: balance estimate ---------------------------------------------------------------------------------------

    /// <summary>E3 for one INN (<paramref name="inn"/> set) or, as an extension of E3, for every counterparty with documents or payments (paged by INN).</summary>
    public Task<AnalyticsResult<Page<BalanceEstimate>>> GetBalanceEstimatesAsync(string? inn, DateOnly asOf, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var t = ctx.TenantId;
            var cur = O.Currency;
            var sql = new Sql()
                .Raw("with docs as (select d.id, d.counterparty_inn as inn, d.owner,")
                .Raw(" case when d.total_delivery_sum_with_vat > 0 then d.total_delivery_sum_with_vat else d.total_sum end as amount")
                .Raw(" from edo_documents d where d.tenant_id = ").P(t).Raw(" and d.status = any(").P(ctx.Options.Edo.CountedStatuses)
                .Raw(") and d.doc_type = any(").P(O.DocumentTypes).Raw(") and d.currency = ").P(cur)
                .Raw(" and coalesce(d.counterparty_inn, '') <> '' and d.document_date <= ").P(asOf);
            if (inn is not null) sql.Raw(" and d.counterparty_inn = ").P(inn);
            sql.Raw("), doc_paid as (select docs.*, coalesce((select sum(i.allocated_amount_minor) from reconciliation_match_items i where i.tenant_id = ").P(t)
                .Raw(" and i.kind = 'EdoDocument' and i.ref_id = docs.id and i.match_status <> 'Rejected'), 0) as paid from docs),")
                .Raw(" doc_totals as (select inn, coalesce(sum(amount) filter (where owner = 'Outgoing'), 0)::bigint as outgoing_documents,")
                .Raw(" coalesce(sum(amount) filter (where owner = 'Incoming'), 0)::bigint as incoming_documents,")
                .Raw(" (count(*) filter (where paid = 0))::int as unpaid_documents, (count(*) filter (where paid > 0 and paid < amount))::int as partially_paid_documents")
                .Raw(" from doc_paid group by inn),")
                .Raw(" pays as (select t.id, t.counterparty_inn as inn, t.direction, t.amount,")
                .Raw(" (select coalesce(sum(i.allocated_amount_minor), 0) from reconciliation_match_items i where i.tenant_id = t.tenant_id")
                .Raw(" and i.kind = 'BankTransaction' and i.ref_id = t.id and i.match_status <> 'Rejected') as matched")
                .Raw(" from bank_transactions t where t.tenant_id = ").P(t).Raw(" and t.has_error = false and t.currency = ").P(cur)
                .Raw(" and coalesce(t.counterparty_inn, '') <> '' and t.posting_date <= ").P(asOf);
            if (inn is not null) sql.Raw(" and t.counterparty_inn = ").P(inn);
            sql.Raw("), pay_totals as (select inn,")
                .Raw(" coalesce(sum(matched) filter (where direction = 'Incoming'), 0)::bigint as matched_incoming,")
                .Raw(" coalesce(sum(matched) filter (where direction = 'Outgoing'), 0)::bigint as matched_outgoing,")
                .Raw(" coalesce(sum(amount - matched) filter (where direction = 'Incoming'), 0)::bigint as unmatched_incoming,")
                .Raw(" coalesce(sum(amount - matched) filter (where direction = 'Outgoing'), 0)::bigint as unmatched_outgoing")
                .Raw(" from pays group by inn)")
                .Raw(" select coalesce(d.inn, p.inn) as inn, c.name as counterparty_name, coalesce(d.outgoing_documents, 0) as outgoing_documents,")
                .Raw(" coalesce(p.matched_incoming, 0) as matched_incoming, coalesce(d.incoming_documents, 0) as incoming_documents,")
                .Raw(" coalesce(p.matched_outgoing, 0) as matched_outgoing, coalesce(p.unmatched_incoming, 0) as unmatched_incoming,")
                .Raw(" coalesce(p.unmatched_outgoing, 0) as unmatched_outgoing, coalesce(d.unpaid_documents, 0) as unpaid_documents,")
                .Raw(" coalesce(d.partially_paid_documents, 0) as partially_paid_documents")
                .Raw(" from doc_totals d full join pay_totals p on p.inn = d.inn")
                .Raw(" left join counterparties c on c.tenant_id = ").P(t).Raw(" and c.inn = coalesce(d.inn, p.inn) where true");
            if (inn is null) sql.Raw(" and d.inn is not null"); // all counterparties (extension of E3): those with documents
            if (page?.Cursor is { } cursor) sql.Raw(" and coalesce(d.inn, p.inn) > ").P(Cursor.Decode(cursor, 1)[0]!);
            sql.Raw(" order by 1 limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<BalanceRow>(sql, token);
            if (inn is not null && rows.Count == 0) rows.Add(new BalanceRow { Inn = inn });
            return AnalyticsContext.ToPage(rows, limit,
                r => new BalanceEstimate(r.Inn, r.CounterpartyName, cur, asOf, r.OutgoingDocuments, r.MatchedIncoming,
                    r.OutgoingDocuments - r.MatchedIncoming, r.IncomingDocuments, r.MatchedOutgoing, r.IncomingDocuments - r.MatchedOutgoing,
                    r.UnmatchedIncoming, r.UnmatchedOutgoing, r.UnpaidDocuments, r.PartiallyPaidDocuments),
                r => Cursor.Encode(r.Inn));
        }, ct);

    // ---- Shared -----------------------------------------------------------------------------------------------------

    public static TransactionDirection PaymentDirectionFor(EdoDocumentOwner owner) =>
        owner == EdoDocumentOwner.Outgoing ? TransactionDirection.Incoming : TransactionDirection.Outgoing;

    public static EdoDocumentOwner DocumentDirectionFor(TransactionDirection direction) =>
        direction == TransactionDirection.Incoming ? EdoDocumentOwner.Outgoing : EdoDocumentOwner.Incoming;

    private static void PaymentFilters(Sql sql, PaymentFilter filter)
    {
        if (filter.Direction is { } d) sql.Raw(" and t.direction = ").P(d.ToString());
        if (filter.Inn is not null) sql.Raw(" and t.counterparty_inn = ").P(filter.Inn);
        if (filter.WithoutInn == true) sql.Raw(" and coalesce(t.counterparty_inn, '') = ''");
        if (filter.WithoutInn == false) sql.Raw(" and coalesce(t.counterparty_inn, '') <> ''");
    }

    private async Task<ILookup<Guid, MatchReference>> MatchReferencesAsync(MatchItemKind kind, Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return Array.Empty<MatchReference>().ToLookup(_ => Guid.Empty);
        var rows = await ctx.QueryAsync<MatchRefRow>(new Sql()
            .Raw("select i.ref_id, m.id as match_id, m.match_type, m.status, m.confidence, m.reasons::text as reasons, i.allocated_amount_minor as allocated")
            .Raw(" from reconciliation_match_items i join reconciliation_matches m on m.tenant_id = i.tenant_id and m.id = i.match_id")
            .Raw(" where i.tenant_id = ").P(ctx.TenantId).Raw(" and i.kind = ").P(kind.ToString()).Raw(" and i.ref_id = any(").P(ids).Raw(")")
            .Raw(" and m.status <> 'Rejected' order by m.created_at, m.id"), ct);
        return rows.ToLookup(r => r.RefId, r => new MatchReference(r.MatchId, Enum.Parse<ReconciliationMatchType>(r.MatchType),
            Enum.Parse<MatchStatus>(r.Status), r.Confidence, Json(r.Reasons), r.Allocated));
    }

    private async Task<Dictionary<Guid, CandidateHint>> HintsAsync(MatchItemKind kind, Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var rows = await ctx.QueryAsync<HintRow>(new Sql()
            .Raw("select f.subject_id, f.related_kind, f.related_id, f.confidence, f.reasons::text as reasons,")
            .Raw(" d.number as document_number, coalesce(d.document_date, bt.posting_date) as date,")
            .Raw(" coalesce(case when d.total_delivery_sum_with_vat > 0 then d.total_delivery_sum_with_vat else d.total_sum end, bt.amount) as amount,")
            .Raw(" coalesce(d.currency, bt.currency) as currency")
            .Raw(" from reconciliation_findings f")
            .Raw(" left join edo_documents d on f.related_kind = 'EdoDocument' and d.tenant_id = f.tenant_id and d.id = f.related_id")
            .Raw(" left join bank_transactions bt on f.related_kind = 'BankTransaction' and bt.tenant_id = f.tenant_id and bt.id = f.related_id")
            .Raw(" where f.tenant_id = ").P(ctx.TenantId).Raw(" and f.kind = 'NearestCandidate' and f.subject_kind = ").P(kind.ToString())
            .Raw(" and f.subject_id = any(").P(ids).Raw(") order by f.confidence desc, f.id"), ct);
        return rows.GroupBy(r => r.SubjectId).ToDictionary(g => g.Key, g =>
        {
            var r = g.First();
            return new CandidateHint(Enum.Parse<MatchItemKind>(r.RelatedKind!), r.RelatedId!.Value, r.Confidence, Json(r.Reasons), r.DocumentNumber,
                r.Date, r.Amount, r.Currency);
        });
    }

    private async Task<Dictionary<Guid, MatchSummary>> SummariesAsync(Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var t = ctx.TenantId;
        var matches = await ctx.QueryAsync<MatchRow>(new Sql()
            .Raw("select m.id, m.match_type, m.status, m.confidence, m.reasons::text as reasons, m.counterparty_inn as inn, c.name as counterparty_name,")
            .Raw(" m.currency, m.document_owner, m.payments_total, m.documents_total, m.created_at, m.decided_at, m.decision_comment")
            .Raw(" from reconciliation_matches m left join counterparties c on c.tenant_id = m.tenant_id and c.inn = m.counterparty_inn")
            .Raw(" where m.tenant_id = ").P(t).Raw(" and m.id = any(").P(ids).Raw(")"), ct);
        var payments = (await ctx.QueryAsync<PaymentItemRow>(new Sql()
            .Raw("select i.match_id, bt.id, a.account_number, bt.operation_id, bt.posting_date, bt.direction, bt.amount, i.allocated_amount_minor as allocated,")
            .Raw(" bt.currency, bt.counterparty_account, bt.purpose from reconciliation_match_items i")
            .Raw(" join bank_transactions bt on bt.tenant_id = i.tenant_id and bt.id = i.ref_id")
            .Raw(" join bank_accounts a on a.tenant_id = bt.tenant_id and a.id = bt.bank_account_id")
            .Raw(" where i.tenant_id = ").P(t).Raw(" and i.kind = 'BankTransaction' and i.match_id = any(").P(ids).Raw(") order by bt.posting_date, bt.id"), ct))
            .ToLookup(r => r.MatchId);
        var documents = (await ctx.QueryAsync<DocumentItemRow>(new Sql()
            .Raw("select i.match_id, d.id, d.number, d.doc_type, d.status, d.owner, d.document_date,")
            .Raw(" case when d.total_delivery_sum_with_vat > 0 then d.total_delivery_sum_with_vat else d.total_sum end as amount,")
            .Raw(" i.allocated_amount_minor as allocated, d.currency, d.contract_number, d.seller_account from reconciliation_match_items i")
            .Raw(" join edo_documents d on d.tenant_id = i.tenant_id and d.id = i.ref_id")
            .Raw(" where i.tenant_id = ").P(t).Raw(" and i.kind = 'EdoDocument' and i.match_id = any(").P(ids).Raw(") order by d.document_date, d.id"), ct))
            .ToLookup(r => r.MatchId);
        return matches.ToDictionary(m => m.Id, m =>
        {
            var owner = Enum.Parse<EdoDocumentOwner>(m.DocumentOwner);
            var ps = payments[m.Id].Select(p => new MatchPaymentItem(p.Id, p.AccountNumber, p.OperationId, p.PostingDate,
                Enum.Parse<TransactionDirection>(p.Direction), p.Amount, p.Allocated, p.Currency, p.CounterpartyAccount, p.Purpose)).ToList();
            var ds = documents[m.Id].Select(d => new MatchDocumentItem(d.Id, d.Number, d.DocType, d.Status, Enum.Parse<EdoDocumentOwner>(d.Owner),
                d.DocumentDate, d.Amount, d.Allocated, d.Currency, d.ContractNumber, d.SellerAccount)).ToList();
            return new MatchSummary(m.Id, Enum.Parse<ReconciliationMatchType>(m.MatchType), Enum.Parse<MatchStatus>(m.Status), m.Confidence,
                Json(m.Reasons), m.Inn, m.CounterpartyName, m.Currency, owner, PaymentDirectionFor(owner), m.PaymentsTotal, m.DocumentsTotal,
                m.PaymentsTotal - m.DocumentsTotal, ps.Count > 0 ? ps.Min(p => p.PostingDate) : null, ps.Count > 0 ? ps.Max(p => p.PostingDate) : null,
                m.CreatedAt, m.DecidedAt, m.DecisionComment, ps, ds);
        });
    }

    private static JsonElement Json(string? json) =>
        JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json).RootElement.Clone();

    // Row types (snake_case columns map to these properties).

    private sealed class DocumentRow
    {
        public Guid Id { get; set; }
        public string? Number { get; set; }
        public string DocType { get; set; } = "";
        public string Status { get; set; } = "";
        public string Owner { get; set; } = "";
        public DateOnly? DocumentDate { get; set; }
        public string Inn { get; set; } = "";
        public string? CounterpartyName { get; set; }
        public string? ContractNumber { get; set; }
        public string Currency { get; set; } = "";
        public long Amount { get; set; }
        public long Paid { get; set; }
        public DateOnly? LastPaymentDate { get; set; }
    }

    private sealed class PaymentRow
    {
        public Guid Id { get; set; }
        public Guid AccountId { get; set; }
        public string AccountNumber { get; set; } = "";
        public string OperationId { get; set; } = "";
        public DateOnly PostingDate { get; set; }
        public string Direction { get; set; } = "";
        public long Amount { get; set; }
        public string Currency { get; set; } = "";
        public string? Inn { get; set; }
        public string? CounterpartyName { get; set; }
        public string? CounterpartyAccount { get; set; }
        public string? Purpose { get; set; }
        public bool CounterpartyHasDocuments { get; set; }
    }

    private sealed class MatchKeyRow
    {
        public Guid Id { get; set; }
        public DateOnly FirstPaymentDate { get; set; }
    }

    private sealed class RunRow
    {
        public Guid RunId { get; set; }
        public string RulesVersion { get; set; } = "";
        public DateTimeOffset? FinishedAt { get; set; }
        public Guid? DecidedByKey { get; set; }
    }

    private sealed class NoReferenceRow
    {
        public Guid Id { get; set; }
        public string AccountNumber { get; set; } = "";
        public DateOnly PostingDate { get; set; }
        public string Direction { get; set; } = "";
        public long Amount { get; set; }
        public string Currency { get; set; } = "";
        public string? Inn { get; set; }
        public string? CounterpartyName { get; set; }
        public string? Purpose { get; set; }
        public bool Matched { get; set; }
    }

    private sealed class DelayRow
    {
        public string Inn { get; set; } = "";
        public string? CounterpartyName { get; set; }
        public string Owner { get; set; } = "";
        public int Documents { get; set; }
        public int Links { get; set; }
        public decimal AverageDays { get; set; }
        public int MinDays { get; set; }
        public int MaxDays { get; set; }
    }

    private sealed class DelayOverallRow
    {
        public string Owner { get; set; } = "";
        public int Counterparties { get; set; }
        public int Documents { get; set; }
        public int Links { get; set; }
        public decimal AverageDays { get; set; }
        public decimal AverageOfCounterparties { get; set; }
        public int MinDays { get; set; }
        public int MaxDays { get; set; }
    }

    private sealed class MismatchRow
    {
        public Guid MatchId { get; set; }
        public string Status { get; set; } = "";
        public int Confidence { get; set; }
        public string Inn { get; set; } = "";
        public string? CounterpartyName { get; set; }
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

    private sealed class DuplicateRow
    {
        public Guid PaymentId { get; set; }
        public DateOnly PostingDate { get; set; }
        public Guid EarlierPaymentId { get; set; }
        public DateOnly EarlierPostingDate { get; set; }
        public string Direction { get; set; } = "";
        public long Amount { get; set; }
        public string Currency { get; set; } = "";
        public string? Inn { get; set; }
        public string? CounterpartyName { get; set; }
        public string? Purpose { get; set; }
        public string? EarlierPurpose { get; set; }
        public int Confidence { get; set; }
        public string Reasons { get; set; } = "[]";
        public bool PaymentMatched { get; set; }
        public bool EarlierPaymentMatched { get; set; }
    }

    private sealed class BalanceRow
    {
        public string Inn { get; set; } = "";
        public string? CounterpartyName { get; set; }
        public long OutgoingDocuments { get; set; }
        public long MatchedIncoming { get; set; }
        public long IncomingDocuments { get; set; }
        public long MatchedOutgoing { get; set; }
        public long UnmatchedIncoming { get; set; }
        public long UnmatchedOutgoing { get; set; }
        public int UnpaidDocuments { get; set; }
        public int PartiallyPaidDocuments { get; set; }
    }

    private sealed class MatchRefRow
    {
        public Guid RefId { get; set; }
        public Guid MatchId { get; set; }
        public string MatchType { get; set; } = "";
        public string Status { get; set; } = "";
        public int Confidence { get; set; }
        public string Reasons { get; set; } = "[]";
        public long Allocated { get; set; }
    }

    private sealed class HintRow
    {
        public Guid SubjectId { get; set; }
        public string? RelatedKind { get; set; }
        public Guid? RelatedId { get; set; }
        public int Confidence { get; set; }
        public string Reasons { get; set; } = "[]";
        public string? DocumentNumber { get; set; }
        public DateOnly? Date { get; set; }
        public long? Amount { get; set; }
        public string? Currency { get; set; }
    }

    private sealed class MatchRow
    {
        public Guid Id { get; set; }
        public string MatchType { get; set; } = "";
        public string Status { get; set; } = "";
        public int Confidence { get; set; }
        public string Reasons { get; set; } = "[]";
        public string Inn { get; set; } = "";
        public string? CounterpartyName { get; set; }
        public string Currency { get; set; } = "";
        public string DocumentOwner { get; set; } = "";
        public long PaymentsTotal { get; set; }
        public long DocumentsTotal { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? DecidedAt { get; set; }
        public string? DecisionComment { get; set; }
    }

    private sealed class PaymentItemRow
    {
        public Guid MatchId { get; set; }
        public Guid Id { get; set; }
        public string AccountNumber { get; set; } = "";
        public string OperationId { get; set; } = "";
        public DateOnly PostingDate { get; set; }
        public string Direction { get; set; } = "";
        public long Amount { get; set; }
        public long Allocated { get; set; }
        public string Currency { get; set; } = "";
        public string? CounterpartyAccount { get; set; }
        public string? Purpose { get; set; }
    }

    private sealed class DocumentItemRow
    {
        public Guid MatchId { get; set; }
        public Guid Id { get; set; }
        public string? Number { get; set; }
        public string DocType { get; set; } = "";
        public string Status { get; set; } = "";
        public string Owner { get; set; } = "";
        public DateOnly? DocumentDate { get; set; }
        public long Amount { get; set; }
        public long Allocated { get; set; }
        public string Currency { get; set; } = "";
        public string? ContractNumber { get; set; }
        public string? SellerAccount { get; set; }
    }
}
