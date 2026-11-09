using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Analytics;
using Synect.Application.Analytics.Counterparties;
using Synect.Mcp.Responses;
using static Synect.Mcp.Tools.ToolText;

namespace Synect.Mcp.Tools;

/// <summary>Block B: counterparties. One analytics method per tool.</summary>
[McpServerToolType]
public sealed class CounterpartyTools(ToolRunner runner, Presenter p, CounterpartyAnalytics analytics)
{
    private const ToolGroup BankGroup = ToolGroup.Bank | ToolGroup.Counterparties;

    [McpServerTool(Name = "get_counterparty_payments", Title = "Payments with a counterparty", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Payments to and from one counterparty (by INN) in a period: totals per currency and the operations, newest first (B1). " +
                 "Examples: «Сколько мы заплатили ООО Альфа в октябре?», «Платежи от ИНН 301000001 за год»." + InnHint + Common)]
    public Task<CallToolResult> GetCounterpartyPayments(
        [Description("Counterparty INN: 9 digits (organisation) or 14 digits (individual).")] string inn,
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_counterparty_payments", BankGroup, async (a, ct) =>
        {
            var i = a.Inn("inn", inn);
            var period = a.DatePeriod("from", from, "to", to);
            var r = await analytics.GetPaymentsAsync(i, period, a.Page(limit, cursor), ct);
            var ops = r.Data.Operations;
            return new ToolOutput(new
            {
                Inn = r.Data.Inn,
                TotalsByCurrency = r.Data.Totals.Select(t => new { t.Currency, Totals = p.DirectionTotal(t) }).ToList(),
                Operations = ops.Items.Select(p.Operation).ToList(),
            }, ops.Items.Count, r.Freshness, ops.NextCursor);
        }, cancellationToken);

    [McpServerTool(Name = "get_top_counterparties", Title = "Top counterparties", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Top counterparties by incoming or outgoing payments, per currency, with payments without an INN shown separately (B2). " +
                 "Examples: «Кому мы больше всего платим?», «Топ-5 клиентов по поступлениям за год»." + Common)]
    public Task<CallToolResult> GetTopCounterparties(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description("incoming (who pays us) or outgoing (whom we pay).")] string direction,
        [Description("How many counterparties per currency, 1-50 (default 10).")] int? top = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_top_counterparties", BankGroup, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var dir = a.Enum("direction", direction, ToolEnums.Direction);
            var n = a.Int("top", top, 10, 1, 50);
            var r = await analytics.GetTopAsync(period, dir, n, ct);
            var rows = r.Data.Sum(c => c.Top.Count);
            return new ToolOutput(r.Data.Select(c => new
            {
                c.Currency,
                Total = p.Money(c.Total, c.Currency),
                Top = c.Top.Select(t => new { t.Rank, t.Inn, Name = p.Untrusted(t.Name), Amount = p.Money(t.Amount, c.Currency), t.Count }).ToList(),
                WithoutInn = new { Amount = p.Money(c.WithoutInnAmount, c.Currency), Count = c.WithoutInnCount },
            }).ToList(), rows, r.Freshness);
        }, cancellationToken);

    [McpServerTool(Name = "get_counterparty_summary", Title = "Counterparty summary and balance", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Documents (Didox, counted statuses) and payments with one counterparty in a period and the open difference: " +
                 "receivable = our outgoing documents - their payments; payable = their documents - our payments, in the Didox currency (B3, B4). " +
                 "Examples: «Сколько нам должен ООО Альфа?», «Какой долг перед поставщиком 302000002?», «Сверка с контрагентом за год»." + InnHint + Common)]
    public Task<CallToolResult> GetCounterpartySummary(
        [Description("Counterparty INN: 9 or 14 digits.")] string inn,
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(DateBasisText)] string date_basis = "document_date",
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_counterparty_summary", BankGroup | ToolGroup.Documents, async (a, ct) =>
        {
            var i = a.Inn("inn", inn);
            var period = a.DatePeriod("from", from, "to", to);
            var basis = a.Enum("date_basis", date_basis, ToolEnums.DateBasis);
            var r = await analytics.GetSummaryAsync(i, period, basis, ct);
            var s = r.Data;
            object Docs(EdoOwnerTotals t) => new { t.Count, Total = p.Money(t.TotalSum, s.EdoCurrency), Vat = p.Money(t.TotalVatSum, s.EdoCurrency) };
            return new ToolOutput(new
            {
                s.Inn,
                Name = p.Untrusted(s.Name),
                IncomingDocuments = Docs(s.IncomingDocuments),
                OutgoingDocuments = Docs(s.OutgoingDocuments),
                PaymentsByCurrency = s.Payments.Select(t => new { t.Currency, Totals = p.DirectionTotal(t) }).ToList(),
                Receivable = p.Money(s.Receivable, s.EdoCurrency),
                Payable = p.Money(s.Payable, s.EdoCurrency),
            }, 1, r.Freshness, Notes:
            [
                $"Receivable and payable compare Didox documents with bank payments in {s.EdoCurrency} only; payments in other currencies are listed but not netted.",
                "The difference covers only the period; balances from before the period are not included.",
            ]);
        }, cancellationToken);

    [McpServerTool(Name = "compare_counterparty_periods", Title = "Compare counterparty turnover", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Turnover per counterparty and currency in two periods (A and B) with the change; all counterparties or one INN (B5). " +
                 "Examples: «Сравни оплаты контрагентам за сентябрь и октябрь», «Как изменились поступления от Альфы по сравнению с прошлым годом?»." + InnHint + Common)]
    public Task<CallToolResult> CompareCounterpartyPeriods(
        [Description("Period A start, yyyy-MM-dd. " + Dates)] string from_a,
        [Description("Period A end, yyyy-MM-dd.")] string to_a,
        [Description("Period B start, yyyy-MM-dd.")] string from_b,
        [Description("Period B end, yyyy-MM-dd.")] string to_b,
        [Description("Optional counterparty INN (9 or 14 digits).")] string? inn = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("compare_counterparty_periods", BankGroup, async (a, ct) =>
        {
            var pa = a.DatePeriod("from_a", from_a, "to_a", to_a, echo: false);
            var pb = a.DatePeriod("from_b", from_b, "to_b", to_b, echo: false);
            a.SetPeriodEcho(new { A = new { pa.From, pa.To }, B = new { pb.From, pb.To } });
            var i = a.OptionalInn("inn", inn);
            var r = await analytics.CompareAsync(pa, pb, i, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, c => new
            {
                c.Inn,
                Name = p.Untrusted(c.Name),
                c.Currency,
                IncomingA = p.Money(c.IncomingA, c.Currency),
                IncomingB = p.Money(c.IncomingB, c.Currency),
                IncomingChange = p.Money(c.IncomingDelta, c.Currency),
                OutgoingA = p.Money(c.OutgoingA, c.Currency),
                OutgoingB = p.Money(c.OutgoingB, c.Currency),
                OutgoingChange = p.Money(c.OutgoingDelta, c.Currency),
            });
        }, cancellationToken);

    [McpServerTool(Name = "get_counterparty_changes", Title = "New or lost counterparties", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("New counterparties (active in the period and never before) or lost ones (active in the previous period of the same length, not in this one) (B6). " +
                 "Examples: «Какие новые контрагенты появились в октябре?», «Кого из клиентов мы потеряли за квартал?»." + Common)]
    public Task<CallToolResult> GetCounterpartyChanges(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description("new or lost.")] string kind,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_counterparty_changes", BankGroup | ToolGroup.Documents, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var k = a.Enum("kind", kind, ToolEnums.ChangeKind);
            var r = await analytics.GetChangesAsync(period, k, a.Page(limit, cursor), ct);
            var page = r.Data.Counterparties;
            var notes = new List<string>
            {
                r.Data.HistoryStartsAt is { } start
                    ? $"Accuracy is limited by the depth of loaded history: data starts in {start:yyyy-MM}; activity before that is unknown."
                    : "No counterparty history is loaded, so new/lost cannot be determined.",
            };
            if (r.Data.LimitedByHistory)
                notes.Add("The loaded history does not reach before this period: 'new' counterparties may have been active earlier, and 'lost' ones cannot be judged reliably.");
            return new ToolOutput(new
            {
                Kind = k,
                r.Data.HistoryStartsAt,
                r.Data.LimitedByHistory,
                Counterparties = page.Items.Select(c => new { c.Inn, Name = p.Untrusted(c.Name), c.FirstMonth, c.LastMonth }).ToList(),
            }, page.Items.Count, r.Freshness, page.NextCursor, Notes: notes);
        }, cancellationToken);

    [McpServerTool(Name = "get_payments_without_documents", Title = "Payments without documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Outgoing payments of a period to INNs that have no Didox document at all (counted statuses) (B8). " +
                 "Examples: «Каким контрагентам мы платили без документов?», «Платежи без счетов-фактур за октябрь»." + Common)]
    public Task<CallToolResult> GetPaymentsWithoutDocuments(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_payments_without_documents", BankGroup | ToolGroup.Documents, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var r = await analytics.GetPaymentsWithoutDocumentsAsync(period, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, u => new
            {
                u.Inn,
                Name = p.Untrusted(u.Name),
                Amount = p.Money(u.Amount, u.Currency),
                u.Count,
                u.FirstDate,
                u.LastDate,
            });
        }, cancellationToken);

    [McpServerTool(Name = "get_counterparty_concentration", Title = "Counterparty concentration", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Share of the largest counterparty and of the top 3 and top 5 in incoming or outgoing payments, per currency (B9). " +
                 "Examples: «Насколько мы зависим от крупнейшего клиента?», «Какая доля у топ-3 поставщиков?»." + Common)]
    public Task<CallToolResult> GetCounterpartyConcentration(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description("incoming or outgoing.")] string direction,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_counterparty_concentration", BankGroup, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var dir = a.Enum("direction", direction, ToolEnums.Direction);
            var r = await analytics.GetConcentrationAsync(period, dir, ct);
            return new ToolOutput(r.Data.Select(c => new
            {
                c.Currency,
                Total = p.Money(c.Total, c.Currency),
                c.Counterparties,
                c.TopInn,
                TopName = p.Untrusted(c.TopName),
                c.TopShare,
                c.Top3Share,
                c.Top5Share,
                WithoutInn = p.Money(c.WithoutInnAmount, c.Currency),
            }).ToList(), r.Data.Count, r.Freshness);
        }, cancellationToken);
}
