using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Analytics;
using Synect.Mcp.Responses;
using static Synect.Mcp.Tools.ToolText;

namespace Synect.Mcp.Tools;

/// <summary>Block V: Didox documents. One analytics method per tool.</summary>
[McpServerToolType]
public sealed class DocumentTools(ToolRunner runner, Presenter p, DocumentsAnalytics analytics, IOptions<AnalyticsOptions> analyticsOptions)
{
    private const ToolGroup Group = ToolGroup.Documents;

    [McpServerTool(Name = "get_documents_awaiting_our_signature", Title = "Documents awaiting our signature", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Incoming Didox documents waiting for our signature (status 2), oldest first, with age in days (V1). " +
                 "Examples: «Какие документы нам нужно подписать?», «Что висит на подписи у нас дольше недели?»." + Common)]
    public Task<CallToolResult> GetDocumentsAwaitingOurSignature(
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_documents_awaiting_our_signature", Group, async (a, ct) =>
        {
            var (r, summary) = await ListSummary.FirstPageAsync((pg, c) => analytics.GetAwaitingOurSignatureAsync(pg, c), a.Page(limit, cursor), runner.Options.MaxLimit,
                d => new ListSummary.Item(d.Currency, d.TotalSum), ct);
            return ToolOutputs.Page(r, summary, p.Document);
        }, cancellationToken);

    [McpServerTool(Name = "get_documents_awaiting_partner_signature", Title = "Documents awaiting partner signature", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Outgoing Didox documents waiting for the partner's signature (status 1), oldest first, with age in days (V2). " +
                 "Examples: «Какие наши счета-фактуры контрагенты ещё не подписали?», «Кто не подписал акты?»." + Common)]
    public Task<CallToolResult> GetDocumentsAwaitingPartnerSignature(
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_documents_awaiting_partner_signature", Group, async (a, ct) =>
        {
            var (r, summary) = await ListSummary.FirstPageAsync((pg, c) => analytics.GetAwaitingPartnerSignatureAsync(pg, c), a.Page(limit, cursor), runner.Options.MaxLimit,
                d => new ListSummary.Item(d.Currency, d.TotalSum), ct);
            return ToolOutputs.Page(r, summary, p.Document);
        }, cancellationToken);

    [McpServerTool(Name = "get_document_counts", Title = "Document counts by status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Number and total sum of Didox documents by status, document type and direction for a period (all statuses) (V3). " +
                 "Examples: «Сколько документов мы выставили в октябре?», «Сколько документов в каждом статусе?»." + Common)]
    public Task<CallToolResult> GetDocumentCounts(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(DateBasisText)] string date_basis = "document_date",
        [Description(Limit)] int? limit = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_document_counts", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var basis = a.Enum("date_basis", date_basis, ToolEnums.DateBasis);
            var rows = a.Limit(limit);
            var r = await analytics.GetCountsAsync(period, basis, ct);
            var currency = analyticsOptions.Value.Edo.Currency;
            return ToolOutputs.List(r.Data, rows, r.Freshness, c => new
            {
                c.Owner,
                c.Status,
                c.StatusName,
                DocType = p.Code(c.DocType),
                c.Count,
                Total = p.Money(c.TotalSum, currency),
            });
        }, cancellationToken);

    [McpServerTool(Name = "get_rejected_documents", Title = "Rejected documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Didox documents rejected (status 4) in a period, with the rejection comment and counterparty (V4). " +
                 "Examples: «Какие документы отклонили в октябре и почему?», «Список отказов по счетам-фактурам»." + Common)]
    public Task<CallToolResult> GetRejectedDocuments(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(DateBasisText)] string date_basis = "document_date",
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_rejected_documents", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var basis = a.Enum("date_basis", date_basis, ToolEnums.DateBasis);
            var (r, summary) = await ListSummary.FirstPageAsync((pg, c) => analytics.GetRejectedAsync(period, basis, pg, c), a.Page(limit, cursor), runner.Options.MaxLimit,
                d => new ListSummary.Item(d.Currency, d.TotalSum), ct);
            return ToolOutputs.Page(r, summary, p.Document);
        }, cancellationToken);

    [McpServerTool(Name = "search_documents", Title = "Search documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Searches Didox documents by number (exact or prefix), counterparty INN, contract number, type, status, direction and period; newest first. " +
                 "All filters are optional and combined with AND (V5). " +
                 "Examples: «Найди счёт-фактуру INV-002», «Документы по договору C-1», «Все документы с ИНН 301000001 за октябрь»." + InnHint + Common)]
    public Task<CallToolResult> SearchDocuments(
        [Description("Document number, exact or prefix.")] string? number = null,
        [Description("Counterparty INN, 9 or 14 digits.")] string? inn = null,
        [Description("Contract number, exact or prefix.")] string? contract_number = null,
        [Description("Didox document type code, e.g. 002 (invoice).")] string? doc_type = null,
        [Description("Didox status code: 0 draft, 1 awaiting partner signature, 2 awaiting our signature, 3 signed, 4 rejected, 8 signed by agent.")] string? status = null,
        [Description("incoming or outgoing.")] string? owner = null,
        [Description("Optional period start, yyyy-MM-dd (give together with 'to').")] string? from = null,
        [Description("Optional period end, yyyy-MM-dd.")] string? to = null,
        [Description(DateBasisText)] string date_basis = "document_date",
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("search_documents", Group, async (a, ct) =>
        {
            var filter = new DocumentSearch(
                Number: a.OptionalText("number", number),
                CounterpartyInn: a.OptionalInn("inn", inn),
                ContractNumber: a.OptionalText("contract_number", contract_number),
                DocType: a.OptionalCode("doc_type", doc_type),
                Status: a.OptionalCode("status", status),
                Owner: a.OptionalEnum("owner", owner, ToolEnums.Owner),
                Period: a.OptionalPeriod("from", from, "to", to),
                Basis: a.Enum("date_basis", date_basis, ToolEnums.DateBasis));
            var r = await analytics.SearchAsync(filter, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, p.Document);
        }, cancellationToken);

    [McpServerTool(Name = "get_largest_documents", Title = "Largest documents", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Largest Didox documents (counted statuses) of a period by total sum, optionally only incoming or outgoing (V6). " +
                 "Examples: «Самые крупные счета-фактуры за квартал», «Крупнейшие входящие документы за октябрь»." + Common)]
    public Task<CallToolResult> GetLargestDocuments(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(DateBasisText)] string date_basis = "document_date",
        [Description("Optional: incoming or outgoing.")] string? owner = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_largest_documents", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var basis = a.Enum("date_basis", date_basis, ToolEnums.DateBasis);
            var o = a.OptionalEnum("owner", owner, ToolEnums.Owner);
            var r = await analytics.GetLargestAsync(period, basis, o, a.Page(limit, cursor), ct);
            return ToolOutputs.Page(r, p.Document);
        }, cancellationToken);

    [McpServerTool(Name = "get_vat_summary", Title = "VAT summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("VAT and totals of incoming (purchase) and outgoing (sales) Didox documents for a period: totals over counted statuses, plus a breakdown " +
                 "by status, so that signed documents (statuses 3 and 8) are separate from those awaiting a signature (V7). " +
                 "Examples: «Какой НДС к зачёту за октябрь?», «НДС по продажам за квартал», «Сколько НДС в неподписанных документах?»." + Common)]
    public Task<CallToolResult> GetVatSummary(
        [Description("Period start, yyyy-MM-dd. " + Dates)] string from,
        [Description("Period end, yyyy-MM-dd.")] string to,
        [Description(DateBasisText)] string date_basis = "document_date",
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_vat_summary", Group, async (a, ct) =>
        {
            var period = a.DatePeriod("from", from, "to", to);
            var basis = a.Enum("date_basis", date_basis, ToolEnums.DateBasis);
            var r = await analytics.GetVatReportAsync(period, basis, ct);
            object Sum(IEnumerable<VatStatusLine> lines, string currency)
            {
                var l = lines.ToList();
                return new
                {
                    Count = l.Sum(x => x.Count),
                    Total = p.Money(l.Sum(x => x.TotalSum), currency),
                    Vat = p.Money(l.Sum(x => x.TotalVatSum), currency),
                    WithoutVat = p.Money(l.Sum(x => x.TotalDeliverySum), currency),
                };
            }
            var byOwner = r.Data.Totals.Select(t =>
            {
                var lines = r.Data.ByStatus.Where(l => l.Owner == t.Owner).ToList();
                return new
                {
                    t.Owner,
                    CountedTotal = new
                    {
                        t.Count,
                        Total = p.Money(t.TotalSum, t.Currency),
                        Vat = p.Money(t.TotalVatSum, t.Currency),
                        WithoutVat = p.Money(t.TotalDeliverySum, t.Currency),
                    },
                    Signed = Sum(lines.Where(l => l.Group == VatStatusGroup.Signed), t.Currency),
                    AwaitingSignature = Sum(lines.Where(l => l.Group == VatStatusGroup.AwaitingSignature), t.Currency),
                    ByStatus = lines.Select(l => new
                    {
                        l.Status,
                        l.StatusName,
                        l.Group,
                        CountedInTotal = l.IsCounted,
                        l.Count,
                        Total = p.Money(l.TotalSum, l.Currency),
                        Vat = p.Money(l.TotalVatSum, l.Currency),
                        WithoutVat = p.Money(l.TotalDeliverySum, l.Currency),
                    }).ToList(),
                };
            }).ToList();
            // Outgoing minus incoming VAT per currency, computed here so that the model never subtracts by itself.
            long VatOf(Synect.Domain.Edo.EdoDocumentOwner owner, string currency, VatStatusGroup? group) => group is { } g
                ? r.Data.ByStatus.Where(l => l.Owner == owner && l.Currency == currency && l.Group == g).Sum(l => l.TotalVatSum)
                : r.Data.Totals.Where(t => t.Owner == owner && t.Currency == currency).Sum(t => t.TotalVatSum);
            var vatDifference = r.Data.Totals.Select(t => t.Currency).Distinct().Order(StringComparer.Ordinal).Select(c => new
            {
                Currency = c,
                CountedTotal = p.Money(VatOf(Synect.Domain.Edo.EdoDocumentOwner.Outgoing, c, null) - VatOf(Synect.Domain.Edo.EdoDocumentOwner.Incoming, c, null), c),
                Signed = p.Money(VatOf(Synect.Domain.Edo.EdoDocumentOwner.Outgoing, c, VatStatusGroup.Signed) - VatOf(Synect.Domain.Edo.EdoDocumentOwner.Incoming, c, VatStatusGroup.Signed), c),
                AwaitingSignature = p.Money(VatOf(Synect.Domain.Edo.EdoDocumentOwner.Outgoing, c, VatStatusGroup.AwaitingSignature) - VatOf(Synect.Domain.Edo.EdoDocumentOwner.Incoming, c, VatStatusGroup.AwaitingSignature), c),
            }).ToList();
            return new ToolOutput(new { ByOwner = byOwner, VatDifferenceOutgoingMinusIncoming = vatDifference, r.Data.CountedStatuses }, r.Data.ByStatus.Count, r.Freshness, Notes:
            [
                "signed = statuses 3 and 8; awaiting_signature = statuses 1, 2, 6 and 60. Only signed documents are final for VAT; documents awaiting a signature may still be rejected.",
                "incoming = documents received from suppliers (purchase VAT); outgoing = documents issued to customers (sales VAT).",
            ]);
        }, cancellationToken);
}
