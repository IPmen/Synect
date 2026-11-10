using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Analytics.Counterparties;
using Synect.Mcp.Responses;
using static Synect.Mcp.Tools.ToolText;

namespace Synect.Mcp.Tools;

/// <summary>Finding a counterparty's INN by name (any spelling) before calling the INN-based tools.</summary>
[McpServerToolType]
public sealed class CounterpartySearchTools(ToolRunner runner, Presenter p, CounterpartySearch search, IOptions<SearchOptions> options)
{
    public const string FindCounterparties = "find_counterparties";

    [McpServerTool(Name = FindCounterparties, Title = "Find counterparties", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Finds counterparties by a fragment of the name in any spelling (Russian, Uzbek Cyrillic or Latin, with or without " +
                 "the legal form such as ООО / MChJ / YaTT; typos are tolerated) or by INN, and returns candidates: INN, main name, " +
                 "other spellings, similarity score, last payment and document dates, bank turnover per currency and Didox totals " +
                 "for the period (default: the last 12 months). Use it whenever the user names a counterparty instead of giving an INN, " +
                 "then pass the chosen INN to the other tools. best_match is set only when one candidate is clearly ahead; when it is " +
                 "null, or the user may mean someone else, show the candidates and ask the user which one they mean; never guess an INN. " +
                 "Payments without an INN are never found here (see notes). " +
                 "Examples: «Рассвет», «Rassvet», «ООО Ромашка», «Romashka MChJ», «Сколько мы заплатили Ромашке?», «Qurilish servis YaTT», «Қурилиш сервис ЯТТ», «Romashka MChJ bilan hisob-kitob qancha?»." + Common)]
    public Task<CallToolResult> Find(
        [Description("Name fragment (any language, script or legal form) or an INN of 9 or 14 digits.")] string query,
        [Description("Optional activity period start, yyyy-MM-dd (default: 12 months ago). " + Dates)] string? from = null,
        [Description("Optional activity period end, yyyy-MM-dd (default: today).")] string? to = null,
        [Description("Candidates per answer: default 10, max 50.")] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(FindCounterparties, ToolGroup.Bank | ToolGroup.Documents, async (a, ct) =>
        {
            var text = a.SearchText("query", query);
            var period = a.OptionalPeriod("from", from, "to", to);
            var rows = a.Int("limit", limit, 10, 1, 50);
            var page = a.Page(rows, cursor);
            var r = await search.FindAsync(text, page, period, ct);
            var s = r.Data;
            a.SetPeriodEcho(new { s.Period.From, s.Period.To, s.Period.Days });

            var items = s.Candidates.Items;
            var best = cursor is null && items.Count > 0 && IsClearlyBest(items, s.MinSimilarity, s.IsInnQuery) ? items[0].Inn : null;
            var notes = new List<string>
            {
                s.PaymentsWithoutInn > 0
                    ? $"{s.PaymentsWithoutInn} bank payments in the period have no counterparty INN; name search never finds them. Look for them with get_largest_transactions or get_transactions_for_day (counterparty_name), or get_top_counterparties (without_inn)."
                    : "There are no bank payments without a counterparty INN in the period.",
                best is not null
                    ? $"best_match {best} is clearly ahead of the other candidates; still confirm with the user if the name could mean someone else."
                    : items.Count == 0
                        ? "No counterparty matches. Try another spelling, a shorter fragment, or the INN."
                        : "No single clear match: show the candidates to the user and ask which one they mean before calling other tools.",
                $"Similarity threshold Search:MinSimilarity = {s.MinSimilarity:0.##}; names are compared after normalization (legal forms and quotes removed, Cyrillic transliterated).",
            };
            return new ToolOutput(new
            {
                s.Query,
                s.NormalizedQuery,
                MatchedBy = s.IsInnQuery ? "inn" : "name",
                BestMatch = best,
                Candidates = items.Select(c => new
                {
                    c.Inn,
                    Name = p.Untrusted(c.Name),
                    OtherNames = c.OtherNames.Select(p.Untrusted).ToList(),
                    c.Score,
                    BestMatch = c.Inn == best,
                    c.SubstringMatch,
                    c.LastPaymentDate,
                    c.LastDocumentDate,
                    ActivityInPeriod = c.Activity,
                    BankTurnover = c.BankTurnover.Select(t => new { t.Currency, Totals = p.DirectionTotal(t) }).ToList(),
                    Documents = c.DocumentTurnover.Select(d => new
                    {
                        d.Currency,
                        Incoming = new { Count = d.IncomingCount, Total = p.Money(d.IncomingSum, d.Currency) },
                        Outgoing = new { Count = d.OutgoingCount, Total = p.Money(d.OutgoingSum, d.Currency) },
                    }).ToList(),
                }).ToList(),
                PaymentsWithoutInnInPeriod = s.PaymentsWithoutInn,
            }, items.Count, r.Freshness, s.Candidates.NextCursor, Notes: notes);
        }, cancellationToken);

    /// <summary>One candidate above the threshold and ahead of the second by Search:BestMatchMargin (or the only one).</summary>
    private bool IsClearlyBest(IReadOnlyList<CounterpartyCandidate> items, double minSimilarity, bool byInn)
    {
        if (byInn) return items.Count == 1;
        var first = (double)items[0].Score;
        if (first < minSimilarity) return false;
        return items.Count == 1 || first - (double)items[1].Score >= options.Value.BestMatchMargin;
    }
}
