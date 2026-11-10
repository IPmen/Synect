using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Analytics;
using Synect.Application.Reconciliation;
using Synect.Domain.Reconciliation;
using Synect.Mcp.Hosting;
using Synect.Mcp.Responses;
using static Synect.Mcp.Tools.ToolText;

namespace Synect.Mcp.Tools;

internal static class ReconciliationText
{
    public const string Matching =
        " Matching is deterministic and explainable: every match has match_type (exact, one_to_many, many_to_one, partial), status" +
        " (proposed, confirmed, rejected), confidence 0-100 and reasons (codes with weights and parameters, e.g." +
        " DocumentNumberInPurpose, AmountEqual, DateInWindow, UniqueCandidate, AccountMismatch, Overpaid). Matches are POSSIBLE matches;" +
        " the accountant decides. Document direction: outgoing = we sell (paid by incoming payments), incoming = we buy (paid by outgoing payments).";

    public const string DocumentDirection = "Optional document direction: outgoing (we sell) or incoming (we buy).";
    public const string PaymentDirection = "Optional payment direction: incoming (money received) or outgoing (money paid).";
    public const string Status = "Optional match status: proposed, confirmed or rejected (default: proposed and confirmed).";
    public const string OptionalInn = "Optional counterparty INN (9 or 14 digits).";
}

/// <summary>Block G, E3 and E11: the stored reconciliation of bank payments with Didox documents. Read only.</summary>
[McpServerToolType]
public sealed class ReconciliationTools(ToolRunner runner, Presenter p, ReconciliationQueries queries, TimeProvider time)
{
    private const ToolGroup Group = ToolGroup.Bank | ToolGroup.Documents | ToolGroup.Reconciliation;

    [McpServerTool(Name = "get_paid_documents", Title = "Paid documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Didox documents (invoices, acts) of the period that are fully covered by matched payments, with the paid amount, the last payment date and the matches (G1). " +
                 "Examples: «Какие счета-фактуры за октябрь оплачены?», «Оплачены ли документы ООО Альфа?», «Qaysi hisob-fakturalar to'langan?»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetPaidDocuments(
        [Description("Period start (document date), yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(ReconciliationText.OptionalInn)] string? inn = null,
        [Description(ReconciliationText.DocumentDirection)] string? direction = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Documents("get_paid_documents", from, to, inn, direction, limit, cursor, queries.GetPaidDocumentsAsync, cancellationToken);

    [McpServerTool(Name = "get_unpaid_documents", Title = "Unpaid documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Didox documents of the period with no matched payment at all, with the closest candidate payment when there is one (a hint below the threshold, never a match) (G2). " +
                 "Examples: «Какие счета-фактуры не оплачены?», «Что нам не оплатили за сентябрь?», «Какие входящие счета мы ещё не оплатили?», «To'lanmagan hisob-fakturalar»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetUnpaidDocuments(
        [Description("Period start (document date), yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(ReconciliationText.OptionalInn)] string? inn = null,
        [Description(ReconciliationText.DocumentDirection)] string? direction = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Documents("get_unpaid_documents", from, to, inn, direction, limit, cursor, queries.GetUnpaidDocumentsAsync, cancellationToken);

    [McpServerTool(Name = "get_partially_paid_documents", Title = "Partially paid documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Didox documents of the period paid only in part, with the remaining amount to pay (G3). " +
                 "Examples: «Какие документы оплачены частично и сколько осталось?», «Остаток к оплате по счетам Альфы», «Qisman to'langan hujjatlar»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetPartiallyPaidDocuments(
        [Description("Period start (document date), yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(ReconciliationText.OptionalInn)] string? inn = null,
        [Description(ReconciliationText.DocumentDirection)] string? direction = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Documents("get_partially_paid_documents", from, to, inn, direction, limit, cursor, queries.GetPartiallyPaidDocumentsAsync, cancellationToken);

    [McpServerTool(Name = "get_signed_unpaid_acts", Title = "Signed unpaid acts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Acts (document type 005) in the signed status (3) that are not fully paid, with paid and remaining amounts (G6). " +
                 "Examples: «Какие подписанные акты ещё не оплачены?», «Подписанные акты без оплаты за квартал», «Imzolangan, lekin to'lanmagan dalolatnomalar»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetSignedUnpaidActs(
        [Description("Period start (document date), yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(ReconciliationText.OptionalInn)] string? inn = null,
        [Description(ReconciliationText.DocumentDirection)] string? direction = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Documents("get_signed_unpaid_acts", from, to, inn, direction, limit, cursor, queries.GetSignedUnpaidActsAsync, cancellationToken);

    [McpServerTool(Name = "get_unmatched_payments", Title = "Unmatched payments", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Bank payments of the period that are in no proposed or confirmed match: flag without_inn, excluded_reason (without_inn, currency_not_reconciled), " +
                 "whether the counterparty has documents at all, and the closest candidate document when there is one (G4). With direction = outgoing it lists " +
                 "expense payments without a counterparty document (G5). " +
                 "Examples: «Какие поступления не сопоставлены с документами?», «Какие расходы прошли без документа контрагента?», «Платежи без ИНН за октябрь», «Hujjatsiz to'lovlar»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetUnmatchedPayments(
        [Description("Period start (payment date), yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(ReconciliationText.PaymentDirection)] string? direction = null,
        [Description(ReconciliationText.OptionalInn)] string? inn = null,
        [Description("Payments without a counterparty INN: include (default), only or exclude.")] string without_inn = "include",
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_unmatched_payments", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var dir = a.OptionalEnum("direction", direction, ToolEnums.PaymentDirection);
            var i = a.OptionalInn("inn", inn);
            var noInn = a.Enum("without_inn", without_inn, ToolEnums.WithoutInn);
            var r = await queries.GetUnmatchedPaymentsAsync(new PaymentFilter(period, i, dir, noInn), a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, u => new
            {
                u.Id,
                u.AccountId,
                AccountNumber = p.Account(u.AccountNumber),
                u.PostingDate,
                u.PaymentDirection,
                u.ExpectedDocumentDirection,
                Amount = p.Money(u.Amount, u.Currency),
                u.Inn,
                CounterpartyName = p.Untrusted(u.CounterpartyName),
                CounterpartyAccount = p.Account(u.CounterpartyAccount),
                Purpose = p.Untrusted(u.Purpose),
                u.WithoutInn,
                u.ExcludedReason,
                u.CounterpartyHasDocuments,
                NearestCandidate = Hint(u.NearestCandidate),
            }, [ReconciliationNotes.Hints]);
        }, cancellationToken);

    [McpServerTool(Name = "get_multi_document_payments", Title = "Payments for several documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Matches where one payment covers several documents (one_to_many) or several payments cover one document (many_to_one), with all payments, documents and allocations (G7). " +
                 "Examples: «Какие платежи закрывают сразу несколько счетов?», «Какие счета оплачены несколькими платежами?»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetMultiDocumentPayments(
        [Description("Period start (first payment date of the match), yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(ReconciliationText.OptionalInn)] string? inn = null,
        [Description(ReconciliationText.Status)] string? status = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Matches("get_multi_document_payments", from, to, inn, status, limit, cursor, queries.GetMultiDocumentMatchesAsync, cancellationToken);

    [McpServerTool(Name = "get_amount_differences", Title = "Amount differences", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Matches where the payments and the documents differ in total: partial payments (difference < 0) and overpayments (difference > 0, reason Overpaid) (G8). " +
                 "Examples: «Где сумма платежа не совпадает с суммой счёта?», «Есть ли переплаты?», «Расхождения сумм по Альфе»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetAmountDifferences(
        [Description("Period start (first payment date of the match), yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(ReconciliationText.OptionalInn)] string? inn = null,
        [Description(ReconciliationText.Status)] string? status = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        Matches("get_amount_differences", from, to, inn, status, limit, cursor, queries.GetAmountDifferencesAsync, cancellationToken);

    [McpServerTool(Name = "get_payments_without_reference", Title = "Payments without document numbers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Payments of the period whose purpose names neither a document number nor a contract number (Russian and Uzbek words are recognised), with whether the payment is matched anyway (G9). " +
                 "Examples: «Какие платежи без номера счёта и договора в назначении?», «Shartnoma raqamisiz to'lovlar»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetPaymentsWithoutReference(
        [Description("Period start (payment date), yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(ReconciliationText.PaymentDirection)] string? direction = null,
        [Description(ReconciliationText.OptionalInn)] string? inn = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_payments_without_reference", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var dir = a.OptionalEnum("direction", direction, ToolEnums.PaymentDirection);
            var i = a.OptionalInn("inn", inn);
            var r = await queries.GetPaymentsWithoutReferenceAsync(new PaymentFilter(period, i, dir), a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, x => new
            {
                x.Id,
                AccountNumber = p.Account(x.AccountNumber),
                x.PostingDate,
                x.PaymentDirection,
                Amount = p.Money(x.Amount, x.Currency),
                x.Inn,
                CounterpartyName = p.Untrusted(x.CounterpartyName),
                Purpose = p.Untrusted(x.Purpose),
                x.Matched,
            }, ["The purpose is checked for document and contract numbers with a word list (Russian, Uzbek Latin and Cyrillic); unusual spellings may be missed."]);
        }, cancellationToken);

    [McpServerTool(Name = "get_payment_delay_stats", Title = "Days from document to payment", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("In how many days on average documents (ESF, acts) get paid: days from the document date to the payment date over matched payments of the period. " +
                 "Returns the average, minimum and maximum per counterparty and document direction, and overall figures per direction for the whole filter " +
                 "(average over all payment-document links and the average of the counterparties' averages). Covers E11 (how fast a client pays our ESF: " +
                 "direction = outgoing) and G10 (both directions; incoming = how fast we pay suppliers). Days are counted from the DOCUMENT date, not the signing date. " +
                 "Examples: «За сколько дней в среднем клиент оплачивает ЭСФ?», «Через сколько дней в среднем нам платят клиенты?», " +
                 "«Как быстро мы оплачиваем счета поставщиков?», «Mijozlar o'rtacha necha kunda to'laydi?»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetPaymentDelayStats(
        [Description("Period start (payment date), yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(ReconciliationText.OptionalInn)] string? inn = null,
        [Description(ReconciliationText.DocumentDirection + " For E11 (clients paying us) use outgoing.")] string? direction = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_payment_delay_stats", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var i = a.OptionalInn("inn", inn);
            var dir = a.OptionalEnum("direction", direction, ToolEnums.Owner);
            var r = await queries.GetPaymentDelayStatsAsync(period, i, dir, a.Page(limit, cursor), ct);
            var page = r.Data.Counterparties;
            return new ToolOutput(new
            {
                Counterparties = page.Items.Select(x => new
                {
                    x.Inn,
                    CounterpartyName = p.Untrusted(x.CounterpartyName),
                    x.DocumentDirection,
                    PaymentDirection = ReconciliationQueries.PaymentDirectionFor(x.DocumentDirection),
                    x.Documents,
                    PaymentDocumentLinks = x.Links,
                    x.AverageDays,
                    x.MinDays,
                    x.MaxDays,
                }).ToList(),
                Overall = r.Data.Overall.Select(x => new
                {
                    x.DocumentDirection,
                    PaymentDirection = ReconciliationQueries.PaymentDirectionFor(x.DocumentDirection),
                    x.Counterparties,
                    x.Documents,
                    PaymentDocumentLinks = x.Links,
                    x.AverageDays,
                    x.AverageOfCounterparties,
                    x.MinDays,
                    x.MaxDays,
                }).ToList(),
            }, page.Items.Count, r.Freshness, page.NextCursor, Notes:
            [
                "Days are counted from the DOCUMENT date, not the signing date: the Didox document list does not give a confirmed signing date " +
                "(Считается от даты документа, а не от даты подписания).",
                "Negative days mean the payment came before the document date (prepayment).",
                "overall covers every counterparty of the filter (not only this page): average_days weighs every payment-document link, " +
                "average_of_counterparties weighs every counterparty equally.",
            ]);
        }, cancellationToken);

    [McpServerTool(Name = "get_account_mismatches", Title = "Payments to another account", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Matched payments for incoming documents (we buy) sent to a bank account other than the sellerAccount written in the document (G11). " +
                 "Examples: «Платили ли мы на счёт, которого нет в счёте-фактуре?», «Платежи поставщикам на другой расчётный счёт»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetAccountMismatches(
        [Description("Period start (payment date), yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(ReconciliationText.OptionalInn)] string? inn = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_account_mismatches", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var i = a.OptionalInn("inn", inn);
            var r = await queries.GetAccountMismatchesAsync(period, i, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, x => new
            {
                x.MatchId,
                x.Status,
                x.Confidence,
                x.Inn,
                CounterpartyName = p.Untrusted(x.CounterpartyName),
                x.PaymentId,
                x.PostingDate,
                Amount = p.Money(x.Amount, x.Currency),
                PaymentAccount = p.Account(x.PaymentAccount),
                x.DocumentId,
                DocumentNumber = p.Code(x.DocumentNumber),
                x.DocumentDate,
                SellerAccount = p.Account(x.SellerAccount),
            }, ["Only documents that carry sellerAccount can be checked; account numbers are compared by their digits."]);
        }, cancellationToken);

    [McpServerTool(Name = "get_possible_duplicate_payments", Title = "Possible double payments", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Pairs of payments to or from one counterparty with the same direction, currency and amount that may be a double payment: the same document named in both purposes, " +
                 "the same purpose within 30 days, or dates within 3 days; with confidence and reasons (G12). " +
                 "Examples: «Не оплатили ли мы один счёт дважды?», «Возможные двойные оплаты за месяц», «Ikki marta to'langan hisoblar bormi?»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetPossibleDuplicatePayments(
        [Description("Period start (date of the later payment), yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(ReconciliationText.OptionalInn)] string? inn = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_possible_duplicate_payments", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var i = a.OptionalInn("inn", inn);
            var r = await queries.GetPossibleDuplicatePaymentsAsync(period, i, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, x => new
            {
                x.PaymentId,
                x.PostingDate,
                x.EarlierPaymentId,
                x.EarlierPostingDate,
                x.PaymentDirection,
                Amount = p.Money(x.Amount, x.Currency),
                x.Inn,
                CounterpartyName = p.Untrusted(x.CounterpartyName),
                Purpose = p.Untrusted(x.Purpose),
                EarlierPurpose = p.Untrusted(x.EarlierPurpose),
                x.Confidence,
                x.Reasons,
                x.PaymentMatched,
                x.EarlierPaymentMatched,
            }, ["A possible double payment is a pattern in the data, not a confirmed error; the accountant checks it."]);
        }, cancellationToken);

    [McpServerTool(Name = "get_counterparty_balance_estimate", Title = "Counterparty balance estimate", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("ESTIMATE of what a counterparty owes us and what we owe it without 1C (E3): documents (paid types, counted statuses, UZS) up to as_of minus the payments matched to them; " +
                 "receivable = our outgoing documents - matched incoming payments, payable = incoming documents - matched outgoing payments; also unmatched payment totals and " +
                 "the number of unpaid and partially paid documents. With inn: one counterparty (E3). Without inn: every counterparty with documents, by INN " +
                 "(an extension of E3 to all counterparties, not E11; for how fast clients pay use get_payment_delay_stats). " +
                 "Examples: «Сколько нам должна Альфа по документам и оплатам?», «Какой примерный долг перед поставщиками?», «Kontragent bo'yicha qarzdorlik qancha?»." +
                 ReconciliationText.Matching + InnHint + Common)]
    public Task<CallToolResult> GetCounterpartyBalanceEstimate(
        [Description(ReconciliationText.OptionalInn + " Without it: all counterparties with documents.")] string? inn = null,
        [Description("Optional date (yyyy-MM-dd, default today): documents and payments up to this date.")] string? as_of = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_counterparty_balance_estimate", Group, async (a, ct) =>
        {
            var i = a.OptionalInn("inn", inn);
            var date = a.OptionalDate("as_of", as_of) ?? Today();
            a.SetPeriodEcho(new { AsOf = date });
            var r = await queries.GetBalanceEstimatesAsync(i, date, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, x => new
            {
                x.Inn,
                CounterpartyName = p.Untrusted(x.CounterpartyName),
                x.AsOf,
                OutgoingDocuments = p.Money(x.OutgoingDocuments, x.Currency),
                MatchedIncomingPayments = p.Money(x.MatchedIncomingPayments, x.Currency),
                ReceivableEstimate = p.Money(x.Receivable, x.Currency),
                IncomingDocuments = p.Money(x.IncomingDocuments, x.Currency),
                MatchedOutgoingPayments = p.Money(x.MatchedOutgoingPayments, x.Currency),
                PayableEstimate = p.Money(x.Payable, x.Currency),
                UnmatchedIncomingPayments = p.Money(x.UnmatchedIncomingPayments, x.Currency),
                UnmatchedOutgoingPayments = p.Money(x.UnmatchedOutgoingPayments, x.Currency),
                x.UnpaidDocuments,
                x.PartiallyPaidDocuments,
            }, ["This is an ESTIMATE from bank and Didox data, not the accounting debt (Это оценка по данным банка и Didox, а не бухгалтерский долг): no 1C, no opening balances, unmatched payments are not netted."]);
        }, cancellationToken);

    [McpServerTool(Name = "get_match_details", Title = "Match details", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Everything about one match by match_id (from another reconciliation tool): its payments and documents with allocated amounts, reasons, confidence, " +
                 "status and decision, rules version. Examples: «Почему этот платёж сопоставлен с этим счётом?», «Покажи детали сопоставления»." +
                 ReconciliationText.Matching + Common)]
    public Task<CallToolResult> GetMatchDetails(
        [Description("match_id returned by another reconciliation tool.")] string match_id,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_match_details", Group, async (a, ct) =>
        {
            var id = a.RequiredGuid("match_id", match_id);
            var r = await queries.GetMatchDetailsAsync(id, ct);
            if (r.Data is not { } d)
                return new ToolOutput(null, 0, r.Freshness, Notes: ["No match with this match_id exists for the client (it may have been recalculated); call the list tool again."]);
            return new ToolOutput(new
            {
                Match = Match(d.Match),
                d.RunId,
                d.RulesVersion,
                d.RunFinishedAt,
                DecidedByKeyId = d.DecidedByKey,
            }, 1, r.Freshness);
        }, cancellationToken);

    // ---- Shared ----

    private Task<CallToolResult> Documents(
        string tool, string from, string to, string? inn, string? direction, int? limit, string? cursor,
        Func<DocumentFilter, PageRequest?, CancellationToken, Task<AnalyticsResult<Page<ReconciledDocument>>>> query, CancellationToken cancellationToken) =>
        runner.RunAsync(tool, Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var i = a.OptionalInn("inn", inn);
            var dir = a.OptionalEnum("direction", direction, ToolEnums.Owner);
            var r = await query(new DocumentFilter(period, i, dir), a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, d => new
            {
                d.Id,
                Number = p.Code(d.Number),
                DocType = p.Code(d.DocType),
                d.Status,
                d.DocumentDirection,
                d.ExpectedPaymentDirection,
                d.DocumentDate,
                d.Inn,
                CounterpartyName = p.Untrusted(d.CounterpartyName),
                ContractNumber = p.Code(d.ContractNumber),
                Amount = p.Money(d.Amount, d.Currency),
                Paid = p.Money(d.PaidAmount, d.Currency),
                Remaining = p.Money(d.RemainingAmount, d.Currency),
                d.LastPaymentDate,
                PaymentState = d.State,
                Matches = d.Matches.Select(m => new { m.MatchId, m.Type, m.Status, m.Confidence, m.Reasons, Allocated = p.Money(m.AllocatedAmount, d.Currency) }).ToList(),
                NearestCandidate = Hint(d.NearestCandidate),
            }, [ReconciliationNotes.Hints]);
        }, cancellationToken);

    private Task<CallToolResult> Matches(
        string tool, string from, string to, string? inn, string? status, int? limit, string? cursor,
        Func<DatePeriod, string?, MatchStatus?, PageRequest?, CancellationToken, Task<AnalyticsResult<Page<MatchSummary>>>> query, CancellationToken cancellationToken) =>
        runner.RunAsync(tool, Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var i = a.OptionalInn("inn", inn);
            var s = a.OptionalEnum("status", status, ToolEnums.MatchStatus);
            var r = await query(period, i, s, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, Match);
        }, cancellationToken);

    private object Match(MatchSummary m) => new
    {
        MatchId = m.Id,
        m.Type,
        m.Status,
        m.Confidence,
        m.Reasons,
        m.Inn,
        CounterpartyName = p.Untrusted(m.CounterpartyName),
        m.DocumentDirection,
        m.PaymentDirection,
        PaymentsTotal = p.Money(m.PaymentsTotal, m.Currency),
        DocumentsTotal = p.Money(m.DocumentsTotal, m.Currency),
        Difference = p.Money(m.Difference, m.Currency),
        m.FirstPaymentDate,
        m.LastPaymentDate,
        m.CreatedAt,
        m.DecidedAt,
        DecisionComment = p.Untrusted(m.DecisionComment),
        Payments = m.Payments.Select(x => new
        {
            x.Id,
            AccountNumber = p.Account(x.AccountNumber),
            x.PostingDate,
            x.Direction,
            Amount = p.Money(x.Amount, x.Currency),
            Allocated = p.Money(x.AllocatedAmount, x.Currency),
            CounterpartyAccount = p.Account(x.CounterpartyAccount),
            Purpose = p.Untrusted(x.Purpose),
        }).ToList(),
        Documents = m.Documents.Select(x => new
        {
            x.Id,
            Number = p.Code(x.Number),
            DocType = p.Code(x.DocType),
            x.Status,
            x.Direction,
            x.DocumentDate,
            Amount = p.Money(x.Amount, x.Currency),
            Allocated = p.Money(x.AllocatedAmount, x.Currency),
            ContractNumber = p.Code(x.ContractNumber),
            SellerAccount = p.Account(x.SellerAccount),
        }).ToList(),
    };

    private object? Hint(CandidateHint? h) => h is null ? null : new
    {
        h.Kind,
        h.Id,
        h.Confidence,
        h.Reasons,
        DocumentNumber = p.Code(h.DocumentNumber),
        h.Date,
        Amount = h.Amount is { } amount && h.Currency is { } c ? p.Money(amount, c) : null,
    };

    private DateOnly Today() => DateOnly.FromDateTime(time.GetUtcNow().ToOffset(TimeSpan.FromHours(5)).DateTime);
}

internal static class ReconciliationNotes
{
    public const string Hints =
        "nearest_candidate is a hint below the automatic threshold (Reconciliation:AutoMatchThreshold), not a match; ambiguous candidates are never matched automatically.";
}

/// <summary>confirm_match and reject_match: change only Synect's own reconciliation data (never a source); scope review.</summary>
[McpServerToolType]
public sealed class ReviewTools(ToolRunner runner, ReconciliationReviewService review, McpCaller caller, ReconciliationQueries queries)
{
    public const string ConfirmMatch = "confirm_match";
    public const string RejectMatch = "reject_match";

    [McpServerTool(Name = ConfirmMatch, Title = "Confirm a match", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Confirms a match (by match_id) as decided by the accountant, with an optional comment: a proposed match, or a previously rejected one " +
                 "(the decision can be changed). Changes only Synect's reconciliation data, never Didox, the bank or 1C. A confirmed match is never changed " +
                 "or removed by recalculation; every decision is kept in the decision history. Requires the review scope; call it only when the user explicitly " +
                 "asks to confirm this match. Examples: «Подтверди это сопоставление», «Да, этот платёж оплачивает счёт СФ-101», «Bu moslikni tasdiqla»." + Common)]
    public Task<CallToolResult> Confirm(
        [Description("match_id returned by a reconciliation tool.")] string match_id,
        [Description("Optional comment of the accountant (at most 1000 characters).")] string? comment = null,
        CancellationToken cancellationToken = default) =>
        DecideAsync(ConfirmMatch, match_id, comment, confirm: true, cancellationToken);

    [McpServerTool(Name = RejectMatch, Title = "Reject a match", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rejects a match (by match_id), with an optional comment: a proposed match, or a previously confirmed one (the decision can be changed). " +
                 "Changes only Synect's reconciliation data. The rejected payment-document pairs are not proposed again while the match stays rejected; " +
                 "every decision is kept in the decision history. Requires the review scope; call it only when the user explicitly asks to reject this match. " +
                 "Examples: «Отклони это сопоставление, это другой платёж», «Я ошибся, отмени подтверждение», «Bu moslikni rad et»." + Common)]
    public Task<CallToolResult> Reject(
        [Description("match_id returned by a reconciliation tool.")] string match_id,
        [Description("Optional comment of the accountant (at most 1000 characters).")] string? comment = null,
        CancellationToken cancellationToken = default) =>
        DecideAsync(RejectMatch, match_id, comment, confirm: false, cancellationToken);

    private Task<CallToolResult> DecideAsync(string tool, string matchId, string? comment, bool confirm, CancellationToken cancellationToken) =>
        runner.RunAsync(tool, ToolGroup.Reconciliation, async (a, ct) =>
        {
            var id = a.RequiredGuid("match_id", matchId);
            var text = a.OptionalComment("comment", comment, ReconciliationMatch.MaxCommentLength);
            var result = confirm
                ? await review.ConfirmAsync(id, caller.ApiKeyId, text, ct)
                : await review.RejectAsync(id, caller.ApiKeyId, text, ct);
            if (result.Outcome == ReviewOutcome.NotFound)
                throw new ToolArgumentException("No match with this match_id exists for the client. Take match_id from a reconciliation tool.");
            if (result.Outcome == ReviewOutcome.Conflict)
                throw new ToolArgumentException("The match cannot be confirmed: its payments or documents are already fully used by other confirmed matches (" +
                    string.Join(", ", result.ConflictingMatches!) + "). Reject one of them first if the user wants this match instead.");
            var changed = result.Outcome != ReviewOutcome.Unchanged;
            caller.AuditDetails = JsonSerializer.Serialize(new
            {
                match_id = id,
                previous_status = result.PreviousStatus?.ToString(),
                new_status = result.Status?.ToString(),
                changed,
                decision_id = result.DecisionId,
            });
            var freshness = (await queries.GetMatchDetailsAsync(id, ct)).Freshness;
            return new ToolOutput(new
            {
                MatchId = id,
                Result = result.Outcome,
                result.Status,
                result.PreviousStatus,
                result.DecidedAt,
            }, 1, freshness, Notes:
            [
                !changed ? "The match already had this status; nothing changed."
                    : result.PreviousStatus == MatchStatus.Proposed ? $"The match is now {result.Status.ToString()!.ToLowerInvariant()}."
                    : $"The decision changed from {result.PreviousStatus.ToString()!.ToLowerInvariant()} to {result.Status.ToString()!.ToLowerInvariant()}; " +
                      "the counterparty was recalculated, and the earlier decision stays in the decision history.",
                "Only Synect's reconciliation data changed; no source system (Didox, bank, 1C) was contacted.",
            ]);
        }, cancellationToken);
}
