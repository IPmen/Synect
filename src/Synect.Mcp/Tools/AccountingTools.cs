using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Accounting1C;
using Synect.Application.Analytics;
using Synect.Mcp.Responses;
using static Synect.Mcp.Tools.ToolText;

namespace Synect.Mcp.Tools;

internal static class AccountingText
{
    public const string About =
        " 1C data is what the 1C extension sent to Synect (see the 1C source in data_freshness); nothing is read from 1C itself and nothing is" +
        " written to 1C. Records marked deleted in 1C are left out unless asked for. Fields named accounting_* are the 1C side.";

    public const string Compare =
        " The comparison pairs records by fixed rules (amount, INN, dates within a few days, document number); every row is a POSSIBLE" +
        " discrepancy with its basis, not a confirmed error: present it so and let the accountant decide. Bank operations rejected by the bank" +
        " are excluded; currencies are never mixed.";

    public const string Kinds =
        "Optional kinds of differences, comma separated: missing_in_1c, missing_in_source (in 1C only), amount_differs, not_posted_in_1c. Default: all.";
}

/// <summary>
/// Questions about 1C data (documents, payment orders, receivables and payables, bank account balances) and its
/// comparison with the bank statement and Didox. Read only; one application query per tool.
/// </summary>
[McpServerToolType]
public sealed class AccountingTools(ToolRunner runner, Presenter p, AccountingQueries queries, AccountingComparison comparison)
{
    private const ToolGroup Group = ToolGroup.Accounting;

    public static readonly IReadOnlyDictionary<string, AccountingDocumentState> States = new Dictionary<string, AccountingDocumentState>
    {
        ["active"] = AccountingDocumentState.Active,
        ["posted"] = AccountingDocumentState.Posted,
        ["unposted"] = AccountingDocumentState.Unposted,
        ["deleted"] = AccountingDocumentState.Deleted,
        ["all"] = AccountingDocumentState.All,
    };

    public static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>
    {
        ["sales"] = "Sales",
        ["purchase"] = "Purchase",
    };

    public static readonly IReadOnlyDictionary<string, AccountingDifferenceKind> Differences = new Dictionary<string, AccountingDifferenceKind>
    {
        ["missing_in_1c"] = AccountingDifferenceKind.MissingIn1C,
        ["missing_in_source"] = AccountingDifferenceKind.MissingInSource,
        ["amount_differs"] = AccountingDifferenceKind.AmountDiffers,
        ["not_posted_in_1c"] = AccountingDifferenceKind.NotPostedIn1C,
    };

    private static string KindName(AccountingDifferenceKind kind, string source) => kind switch
    {
        AccountingDifferenceKind.MissingIn1C => "missing_in_1c",
        AccountingDifferenceKind.MissingInSource => "missing_in_" + source,
        AccountingDifferenceKind.AmountDiffers => "amount_differs",
        _ => "not_posted_in_1c",
    };

    [McpServerTool(Name = "get_1c_documents", Title = "1C documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Sales (реализация) and purchase (поступление) documents as recorded in 1C, newest first: number, date, counterparty, amount, VAT, " +
                 "posted or not, ESF number and status. Filters: period, kind, counterparty INN, state (active = not deleted, posted, unposted, deleted, " +
                 "all), without_esf (documents with no ESF data in 1C), number. Examples: «Какие реализации проведены в 1С за октябрь?», «Какие документы " +
                 "в 1С не проведены?», «Что помечено на удаление в 1С?», «По каким документам в 1С нет ЭСФ?», «Документы 1С по контрагенту с ИНН 301000001»." +
                 AccountingText.About + InnHint + Common)]
    public Task<CallToolResult> GetDocuments(
        [Description("Optional period start, yyyy-MM-dd (give together with 'to'). " + Dates)] string? from = null,
        [Description("Optional period end, yyyy-MM-dd.")] string? to = null,
        [Description("sales or purchase; default both.")] string? kind = null,
        [Description("Counterparty INN, 9 or 14 digits.")] string? inn = null,
        [Description("active (default: not deleted), posted, unposted, deleted or all.")] string state = "active",
        [Description("true: only documents without ESF data (no ESF number and no Didox id) in 1C.")] bool without_esf = false,
        [Description("Document number or ESF number, exact.")] string? number = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_1c_documents", Group, async (a, ct) =>
        {
            var filter = new AccountingDocumentFilter(
                Period: a.OptionalPeriod("from", from, "to", to),
                Kind: string.IsNullOrWhiteSpace(kind) ? null : a.Enum("kind", kind, Kinds),
                CounterpartyTin: a.OptionalInn("inn", inn),
                State: a.Enum("state", state, States),
                WithoutEsf: without_esf,
                Number: a.OptionalText("number", number));
            var r = await queries.GetDocumentsAsync(filter, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, d => new
            {
                Ref = p.Code(d.Ref),
                Kind = d.DocKind,
                DocType = p.Untrusted(d.DocType),
                Number = p.Code(d.Number),
                d.Date,
                d.Posted,
                d.IsDeleted,
                CounterpartyInn = d.CounterpartyTin,
                CounterpartyName = p.Untrusted(d.CounterpartyName),
                ContractNumber = p.Code(d.ContractNumber),
                Total = p.Money(d.AmountTotal, d.Currency),
                Vat = d.VatAmount is { } vat ? p.Money(vat, d.Currency) : null,
                EsfNumber = p.Code(d.EsfNumber),
                d.EsfDate,
                EsfStatus = p.Code(d.EsfStatus),
                d.DueDate,
                Comment = p.Untrusted(d.Comment),
                ModifiedInAccountingAt = d.ModifiedAt,
            });
        }, cancellationToken);

    [McpServerTool(Name = "get_1c_payment_orders", Title = "1C payment orders", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Payment orders (платёжные поручения) created in 1C, newest first, with the bank status as 1C shows it (e.g. prepared, sent, executed). " +
                 "Examples: «Какие платёжные поручения созданы в 1С и не отправлены?», «Платёжки 1С за эту неделю», «В каком статусе платёжка поставщику?»." +
                 AccountingText.About + InnHint + Common)]
    public Task<CallToolResult> GetPaymentOrders(
        [Description("Optional period start, yyyy-MM-dd (give together with 'to'). " + Dates)] string? from = null,
        [Description("Optional period end, yyyy-MM-dd.")] string? to = null,
        [Description("Counterparty INN, 9 or 14 digits.")] string? inn = null,
        [Description("active (default: not deleted), posted, unposted, deleted or all.")] string state = "active",
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_1c_payment_orders", Group, async (a, ct) =>
        {
            var period = a.OptionalPeriod("from", from, "to", to);
            var tin = a.OptionalInn("inn", inn);
            var r = await queries.GetPaymentOrdersAsync(period, tin, a.Enum("state", state, States), a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, o => new
            {
                Ref = p.Code(o.Ref),
                Number = p.Code(o.Number),
                o.Date,
                o.Posted,
                o.IsDeleted,
                CounterpartyInn = o.CounterpartyTin,
                CounterpartyName = p.Untrusted(o.CounterpartyName),
                BankAccountNumber = p.Account(o.BankAccountNumber),
                Amount = p.Money(o.Amount, o.Currency),
                Purpose = p.Untrusted(o.Purpose),
                BankStatus = p.Untrusted(o.BankStatus),
                ModifiedInAccountingAt = o.ModifiedAt,
            });
        }, cancellationToken);

    [McpServerTool(Name = "get_1c_settlement_balances", Title = "Receivables and payables by 1C", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Receivables and payables (дебиторская и кредиторская задолженность) by the accounting records of 1C: settlement balances per counterparty " +
                 "and currency from the latest 1C export on or before the date, largest first, with totals per currency. net > 0: the counterparty owes us " +
                 "(receivable); net < 0: we owe (payable). This is the accounting debt of 1C, unlike get_counterparty_balance_estimate (an estimate from Didox " +
                 "and bank data). Examples: «Какая дебиторка по 1С?», «Кому мы должны по данным учёта?», «Сколько нам должен контрагент по 1С?», " +
                 "«Кредиторская задолженность на конец октября»." + AccountingText.About + InnHint + Common)]
    public Task<CallToolResult> GetSettlementBalances(
        [Description("Balance date, yyyy-MM-dd; the latest 1C export on or before it is used. Default: the latest export.")] string? date = null,
        [Description("Counterparty INN, 9 or 14 digits; default all counterparties.")] string? inn = null,
        [Description(Limit)] int? limit = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_1c_settlement_balances", Group, async (a, ct) =>
        {
            var asOf = a.OptionalDate("date", date);
            var tin = a.OptionalInn("inn", inn);
            var rows = a.Limit(limit);
            var r = await queries.GetSettlementBalancesAsync(asOf, tin, ct);
            var notes = r.Data.AsOfDate is null ? new[] { "1C has sent no settlement balances on or before this date; there is nothing to show." } : null;
            return new ToolOutput(new
            {
                BalanceDate = r.Data.AsOfDate,
                Totals = r.Data.Totals.Select(t => new
                {
                    Receivable = p.Money(t.Receivable, t.Currency),
                    Payable = p.Money(t.Payable, t.Currency),
                    t.Counterparties,
                }).ToList(),
                Counterparties = r.Data.Rows.Take(rows).Select(x => new
                {
                    CounterpartyInn = x.CounterpartyTin,
                    CounterpartyName = p.Untrusted(x.CounterpartyName),
                    CounterpartyRef = p.Code(x.CounterpartyRef),
                    Side = x.Net > 0 ? "receivable" : x.Net < 0 ? "payable" : "settled",
                    Net = p.Money(x.Net, x.Currency),
                    Debit = p.Money(x.Debit, x.Currency),
                    Credit = p.Money(x.Credit, x.Currency),
                    GlAccounts = p.Code(x.GlAccounts),
                }).ToList(),
            }, Math.Min(r.Data.Rows.Count, rows), r.Freshness, null, r.Data.Rows.Count > rows, notes);
        }, cancellationToken);

    [McpServerTool(Name = "get_1c_bank_account_balances", Title = "Bank account balances by 1C", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Balances of the organization's bank accounts as 1C shows them (latest 1C export on or before the date). For the bank's own figures use " +
                 "get_account_balances; to compare both use compare_bank_balances_with_1c. Examples: «Какой остаток по расчётному счёту в 1С?», " +
                 "«Остатки денежных средств по данным учёта»." + AccountingText.About + Common)]
    public Task<CallToolResult> GetBankAccountBalances(
        [Description("Balance date, yyyy-MM-dd; the latest 1C export on or before it is used. Default: the latest export.")] string? date = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_1c_bank_account_balances", Group, async (a, ct) =>
        {
            var r = await queries.GetBankAccountBalancesAsync(a.OptionalDate("date", date), ct);
            return ToolOutputs.List(r.Data, runner.Options.MaxLimit, r.Freshness, b => new
            {
                AccountNumber = p.Account(b.AccountNumber),
                BankName = p.Untrusted(b.BankName),
                BalanceDate = b.AsOfDate,
                Balance = p.Money(b.Balance, b.Currency ?? "UZS"),
                TurnoverDebit = b.TurnoverDebit is { } td ? p.Money(td, b.Currency ?? "UZS") : null,
                TurnoverCredit = b.TurnoverCredit is { } tc ? p.Money(tc, b.Currency ?? "UZS") : null,
            });
        }, cancellationToken);

    [McpServerTool(Name = "compare_bank_with_1c", Title = "Bank statement against 1C", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Compares the bank statement with the bank documents of 1C for a period: payments in the bank that are not in 1C (missing_in_1c), 1C " +
                 "documents with no payment in the bank (missing_in_bank), pairs with different amounts (amount_differs) and payments entered in 1C but " +
                 "not posted (not_posted_in_1c). The summary counts every kind for the whole period. Examples: «Какие платежи из выписки не проведены в " +
                 "1С?», «Сходится ли банк с 1С за октябрь?», «Что есть в 1С, но нет в выписке?», «Какие платежи в 1С не проведены?»." +
                 AccountingText.Compare + AccountingText.About + Common)]
    public Task<CallToolResult> CompareBank(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(AccountingText.Kinds)] string? kinds = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("compare_bank_with_1c", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var r = await comparison.CompareBankAsync(period, a.EnumList("kinds", kinds, Differences), a.Page(limit, cursor), ct);
            var rows = r.Data.Rows.Items.Select(x => new
            {
                Kind = KindName(x.Kind, "bank"),
                x.Date,
                x.MatchedBy,
                AmountDifference = x.AmountDifference is { } diff ? p.Money(diff, (x.Accounting?.Currency ?? x.Source!.Currency)) : null,
                BankPayment = x.Source is { } b ? new
                {
                    b.Id,
                    b.Date,
                    Direction = b.Direction.ToLowerInvariant(),
                    Amount = p.Money(b.Amount, b.Currency),
                    b.CounterpartyInn,
                    CounterpartyName = p.Untrusted(b.CounterpartyName),
                    Purpose = p.Untrusted(b.Purpose),
                    DocumentNumber = p.Code(b.DocumentNumber),
                    AccountNumber = p.Account(b.AccountNumber),
                } : null,
                AccountingDocument = x.Accounting is { } d ? new
                {
                    Ref = p.Code(d.Ref),
                    Number = p.Code(d.Number),
                    d.Date,
                    Direction = d.Direction == "Receipt" ? "incoming" : "outgoing",
                    Amount = p.Money(d.Amount, d.Currency),
                    CounterpartyInn = d.CounterpartyTin,
                    d.Posted,
                    Purpose = p.Untrusted(d.Purpose),
                    AccountNumber = p.Account(d.BankAccountNumber),
                } : null,
            }).ToList();
            return new ToolOutput(new { Summary = Summary(r.Data.Matched, r.Data.Counts, "bank"), Differences = rows }, rows.Count, r.Freshness, r.Data.Rows.NextCursor);
        }, cancellationToken);

    [McpServerTool(Name = "compare_didox_with_1c", Title = "Didox documents against 1C", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Compares Didox documents (invoices and acts in the statuses counted in sums) with the sales and purchase documents of 1C for a period: " +
                 "Didox documents not entered in 1C (missing_in_1c), 1C documents with no Didox document (missing_in_didox), pairs with different amounts " +
                 "(amount_differs) and documents entered in 1C but not posted (not_posted_in_1c). Examples: «Какие счета-фактуры из Didox не введены в 1С?», " +
                 "«Какие документы есть в 1С, но нет в Didox?», «Где сумма в 1С отличается от ЭСФ?», «Сходятся ли документы Didox и 1С за октябрь?»." +
                 AccountingText.Compare + AccountingText.About + Common)]
    public Task<CallToolResult> CompareDocuments(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(AccountingText.Kinds)] string? kinds = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("compare_didox_with_1c", Group | ToolGroup.Documents, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var r = await comparison.CompareDocumentsAsync(period, a.EnumList("kinds", kinds, Differences), a.Page(limit, cursor), ct);
            var rows = r.Data.Rows.Items.Select(x => new
            {
                Kind = KindName(x.Kind, "didox"),
                x.Date,
                x.MatchedBy,
                AmountDifference = x.AmountDifference is { } diff ? p.Money(diff, (x.Accounting?.Currency ?? x.Source!.Currency)) : null,
                DidoxDocument = x.Source is { } d ? new
                {
                    d.Id,
                    ExternalId = p.Code(d.ExternalId),
                    Number = p.Code(d.Number),
                    d.Date,
                    Owner = d.Owner.ToLowerInvariant(),
                    DocType = p.Code(d.DocType),
                    Status = p.Code(d.Status),
                    d.CounterpartyInn,
                    CounterpartyName = p.Untrusted(d.CounterpartyName),
                    Total = p.Money(d.Total, d.Currency),
                } : null,
                AccountingDocument = x.Accounting is { } c ? new
                {
                    Ref = p.Code(c.Ref),
                    Number = p.Code(c.Number),
                    EsfNumber = p.Code(c.EsfNumber),
                    c.Date,
                    Kind = c.DocKind.ToLowerInvariant(),
                    Total = p.Money(c.Total, c.Currency),
                    CounterpartyInn = c.CounterpartyTin,
                    c.Posted,
                } : null,
            }).ToList();
            return new ToolOutput(new { Summary = Summary(r.Data.Matched, r.Data.Counts, "didox"), Differences = rows }, rows.Count, r.Freshness, r.Data.Rows.NextCursor);
        }, cancellationToken);

    [McpServerTool(Name = "compare_bank_balances_with_1c", Title = "Bank balances against 1C", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Per bank account: the balance by 1C (latest 1C export on or before the date) against the bank's closing balance of the same date, with " +
                 "the difference and a status: equal, differs, no_1c_balance (1C sent no balance for the account) or no_bank_data (the 1C account is not " +
                 "among the bank accounts loaded into Synect). Examples: «Совпадает ли остаток в 1С с банком?», «Сверь остатки по счетам с 1С на 31 октября»." +
                 AccountingText.About + Common)]
    public Task<CallToolResult> CompareBankBalances(
        [Description("Balance date, yyyy-MM-dd; the latest 1C export on or before it is used. Default: the latest export.")] string? date = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("compare_bank_balances_with_1c", Group | ToolGroup.Bank, async (a, ct) =>
        {
            var r = await comparison.CompareBankBalancesAsync(a.OptionalDate("date", date), ct);
            return ToolOutputs.List(r.Data, runner.Options.MaxLimit, r.Freshness, b =>
            {
                var currency = b.Currency ?? "UZS";
                return new
                {
                    AccountNumber = p.Account(b.AccountNumber),
                    b.Status,
                    AccountingBalance = b.AccountingBalance is { } x ? p.Money(x, currency) : null,
                    AccountingBalanceDate = b.AccountingDate,
                    BankBalance = b.BankBalance is { } y ? p.Money(y, currency) : null,
                    BankBalanceDate = b.BankDate,
                    Difference = b.Difference is { } d ? p.Money(d, currency) : null,
                };
            }, ["difference = balance by 1C minus the bank's balance."]);
        }, cancellationToken);

    [McpServerTool(Name = "compare_counterparty_with_1c", Title = "Settlements with a counterparty: Synect data against 1C", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("For one counterparty and a period: turnovers by Synect's sources against 1C per currency (sales and purchase documents: Didox against " +
                 "posted 1C documents; receipts and payments: bank statement against posted 1C bank documents), the differences, and the settlement balance " +
                 "by 1C at the period end. Shows whether settlements with the counterparty agree (сверка взаиморасчётов), not an act of reconciliation signed " +
                 "by the parties. For the rows behind a difference call compare_bank_with_1c or compare_didox_with_1c. Examples: «Сходятся ли взаиморасчёты " +
                 "с контрагентом с ИНН 303333333 за октябрь?», «Сверь обороты с поставщиком по 1С и по банку»." +
                 AccountingText.Compare + AccountingText.About + InnHint + Common)]
    public Task<CallToolResult> CompareCounterparty(
        [Description("Counterparty INN, 9 or 14 digits.")] string inn,
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("compare_counterparty_with_1c", Group, async (a, ct) =>
        {
            var tin = a.Inn("inn", inn);
            var period = a.DatePeriod("from", from, "to", to);
            var r = await comparison.CompareCounterpartyAsync(tin, period, ct);
            var lines = r.Data.Lines.Select(l => new
            {
                l.Line,
                ComparedWith = l.SourceName,
                Source = p.Money(l.Source, l.Currency),
                l.SourceCount,
                Accounting = p.Money(l.Accounting, l.Currency),
                AccountingCount = l.AccountingCount,
                Difference = p.Money(l.Difference, l.Currency),
                Agrees = l.Difference == 0,
            }).ToList();
            return new ToolOutput(new
            {
                CounterpartyInn = tin,
                Agrees = lines.All(l => l.Agrees),
                Lines = lines,
                AccountingBalanceDate = r.Data.BalanceDate,
                AccountingBalance = r.Data.AccountingBalances.Select(b => new
                {
                    Side = b.Net > 0 ? "receivable" : b.Net < 0 ? "payable" : "settled",
                    Net = p.Money(b.Net, b.Currency),
                    Debit = p.Money(b.Debit, b.Currency),
                    Credit = p.Money(b.Credit, b.Currency),
                }).ToList(),
            }, lines.Count, r.Freshness, Notes: ["difference = amount by 1C minus the amount by the source (Didox or bank); 1C figures count posted, not deleted documents only."]);
        }, cancellationToken);

    private object Summary(int matched, IReadOnlyList<ComparisonCount> counts, string source) => new
    {
        MatchedWithoutDifference = matched,
        Differences = counts.Select(c => new { Kind = KindName(c.Kind, source), c.Count, Totals = p.Totals(c.Totals) }).ToList(),
        TotalDifferences = counts.Sum(c => c.Count),
    };
}
