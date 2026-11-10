using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Domain.Counterparties;

namespace Synect.Application.Analytics.Counterparties;

/// <summary>Search section.</summary>
public sealed class SearchOptions
{
    /// <summary>Smallest trigram similarity (0..1) of a name to count as a match; substring matches always count.</summary>
    public double MinSimilarity { get; set; } = 0.3;

    /// <summary>Default activity period of candidates: the last N months up to today.</summary>
    public int DefaultPeriodMonths { get; set; } = 12;

    /// <summary>At most this many best-scoring INNs are considered (then ordered by activity and paged).</summary>
    public int MaxCandidates { get; set; } = 200;

    /// <summary>A single best match must lead the second candidate by at least this much similarity.</summary>
    public double BestMatchMargin { get; set; } = 0.15;

    /// <summary>A warning is logged (once a day per tenant) when a tenant has more name spellings than this; 0 turns it off.</summary>
    public int AliasWarningThreshold { get; set; } = 30_000;
}

public sealed record EdoTurnover(string Currency, int IncomingCount, long IncomingSum, int OutgoingCount, long OutgoingSum);

/// <param name="Score">Similarity 0..1 of the best spelling (1 for an exact INN).</param>
/// <param name="Activity">Bank operations plus Didox documents (counted statuses) in the period; orders equal scores.</param>
public sealed record CounterpartyCandidate(
    string Inn, string? Name, IReadOnlyList<string> OtherNames, decimal Score, bool MatchedByInn, bool SubstringMatch,
    DateOnly? LastPaymentDate, DateOnly? LastDocumentDate, int Activity,
    IReadOnlyList<DirectionTotal> BankTurnover, IReadOnlyList<EdoTurnover> DocumentTurnover);

/// <param name="PaymentsWithoutInn">Error-free bank operations of the period without an INN (never matched by name).</param>
public sealed record CounterpartySearchResult(
    string Query, string NormalizedQuery, bool IsInnQuery, DatePeriod Period, double MinSimilarity,
    Page<CounterpartyCandidate> Candidates, long PaymentsWithoutInn);

/// <summary>
/// Finds counterparties by a fragment of the name (any spelling: Russian, Uzbek Cyrillic or Latin, with or without the
/// legal form) or by INN. Matches counterparty_aliases by substring and pg_trgm similarity (GIN index), orders by
/// similarity, then activity in the period, then INN. Payments without an INN are not searchable by name.
/// </summary>
public sealed partial class CounterpartySearch(AnalyticsContext ctx, IOptions<SearchOptions> options)
{
    public const int MaxQueryLength = 200;

    public static bool IsInn(string query) => InnPattern().IsMatch(query);

    public Task<AnalyticsResult<CounterpartySearchResult>> FindAsync(
        string query, PageRequest? page = null, DatePeriod? period = null, CancellationToken ct = default)
    {
        var text = query?.Trim() ?? "";
        if (text.Length == 0) throw new ArgumentException("The search query is empty.", nameof(query));
        if (text.Length > MaxQueryLength) throw new ArgumentException($"The search query is longer than {MaxQueryLength} characters.", nameof(query));
        var o = options.Value;
        if (o.MinSimilarity is <= 0 or > 1) throw new InvalidOperationException("Search:MinSimilarity must be in (0, 1].");

        return ctx.RunAsync(async token =>
        {
            var t = ctx.TenantId;
            var today = ctx.Today;
            var p = period ?? new DatePeriod(today.AddMonths(-o.DefaultPeriodMonths).AddDays(1), today);
            var isInn = IsInn(text);
            var normalized = isInn ? text : CounterpartyNameNormalizer.Normalize(text);
            if (normalized.Length == 0) throw new ArgumentException("The search query has no letters or digits.", nameof(query));
            var limit = ctx.Limit(page);
            var counted = ctx.Options.Edo.CountedStatuses;

            // 1. Candidate INNs with their score and activity, one keyset page.
            var sql = new Sql();
            if (isInn)
            {
                sql.Raw("with c as (select inn, 1.0::numeric as score, false as substring_match from (")
                    .Raw("select counterparty_inn as inn from counterparty_aliases where tenant_id = ").P(t).Raw(" and counterparty_inn = ").P(text)
                    .Raw(" union select inn from counterparties where tenant_id = ").P(t).Raw(" and inn = ").P(text).Raw(") x)");
            }
            else
            {
                var threshold = o.MinSimilarity.ToString(CultureInfo.InvariantCulture);
                await ctx.Db.Database.ExecuteSqlAsync($"SELECT set_config('pg_trgm.similarity_threshold', {threshold}, true)", token);
                await ctx.Db.Database.ExecuteSqlAsync($"SELECT set_config('pg_trgm.word_similarity_threshold', {threshold}, true)", token);
                var like = "%" + normalized.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                sql.Raw("with m as (select a.counterparty_inn as inn,")
                    .Raw(" round(max(greatest(similarity(a.normalized_name, ").P(normalized).Raw("), word_similarity(").P(normalized)
                    .Raw(", a.normalized_name)))::numeric, 4) as score, bool_or(a.normalized_name like ").P(like).Raw(") as substring_match")
                    .Raw(" from counterparty_aliases a where a.tenant_id = ").P(t)
                    .Raw(" and (a.normalized_name % ").P(normalized).Raw(" or ").P(normalized).Raw(" <% a.normalized_name")
                    .Raw(" or a.normalized_name like ").P(like).Raw(") group by a.counterparty_inn),")
                    .Raw(" c as (select inn, score, substring_match from m where score >= ").P((decimal)o.MinSimilarity)
                    .Raw(" or substring_match order by score desc, inn limit ").P(o.MaxCandidates).Raw(")");
            }
            sql.Raw(", r as (select c.inn, c.score, c.substring_match,")
                .Raw(" (select count(*) from bank_transactions b where b.tenant_id = ").P(t)
                .Raw(" and b.counterparty_inn = c.inn and b.has_error = false and b.posting_date between ").P(p.From).Raw(" and ").P(p.To).Raw(")::int")
                .Raw(" + (select count(*) from edo_documents d where d.tenant_id = ").P(t)
                .Raw(" and d.counterparty_inn = c.inn and d.status = any(").P(counted).Raw(") and d.document_date between ").P(p.From).Raw(" and ").P(p.To)
                .Raw(")::int as activity from c)")
                .Raw(" select inn, score, substring_match, activity from r");
            if (page?.Cursor is { } cursor)
            {
                var k = Cursor.Decode(cursor, 3);
                var score = decimal.Parse(k[0]!, CultureInfo.InvariantCulture);
                var activity = int.Parse(k[1]!, CultureInfo.InvariantCulture);
                sql.Raw(" where (score < ").P(score).Raw(" or (score = ").P(score).Raw(" and (activity < ").P(activity)
                    .Raw(" or (activity = ").P(activity).Raw(" and inn > ").P(k[2]!).Raw("))))");
            }
            sql.Raw(" order by score desc, activity desc, inn limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<CandidateRow>(sql, token);
            var pageRows = rows.Take(limit).ToList();
            var inns = pageRows.Select(r => r.Inn).ToArray();

            // 2. Details of the page: names, last activity, turnover per currency.
            var names = inns.Length == 0 ? [] : await ctx.QueryAsync<NameRow>(new Sql()
                .Raw("select counterparty_inn as inn, name, sum(occurrences)::int as occurrences, max(last_seen_at) as last_seen_at")
                .Raw(" from counterparty_aliases where tenant_id = ").P(t).Raw(" and counterparty_inn = any(").P(inns).Raw(")")
                .Raw(" group by counterparty_inn, name"), token);
            var directory = inns.Length == 0 ? [] : await ctx.QueryAsync<NameRow>(new Sql()
                .Raw("select inn, coalesce(name, '') as name, 0 as occurrences, null::timestamptz as last_seen_at from counterparties where tenant_id = ").P(t)
                .Raw(" and inn = any(").P(inns).Raw(")"), token);
            var lastPayments = inns.Length == 0 ? [] : await ctx.QueryAsync<LastRow>(new Sql()
                .Raw("select counterparty_inn as inn, max(posting_date) as date from bank_transactions where tenant_id = ").P(t)
                .Raw(" and counterparty_inn = any(").P(inns).Raw(") and has_error = false group by counterparty_inn"), token);
            var lastDocuments = inns.Length == 0 ? [] : await ctx.QueryAsync<LastRow>(new Sql()
                .Raw("select counterparty_inn as inn, max(document_date) as date from edo_documents where tenant_id = ").P(t)
                .Raw(" and counterparty_inn = any(").P(inns).Raw(") group by counterparty_inn"), token);
            var bank = inns.Length == 0 ? [] : await ctx.QueryAsync<BankRow>(new Sql()
                .Raw("select counterparty_inn as inn, currency,")
                .Raw(" coalesce(sum(amount) filter (where direction = 'Incoming'), 0)::bigint as incoming,")
                .Raw(" coalesce(sum(amount) filter (where direction = 'Outgoing'), 0)::bigint as outgoing,")
                .Raw(" (count(*) filter (where direction = 'Incoming'))::int as incoming_count,")
                .Raw(" (count(*) filter (where direction = 'Outgoing'))::int as outgoing_count")
                .Raw(" from bank_transactions where tenant_id = ").P(t).Raw(" and counterparty_inn = any(").P(inns).Raw(")")
                .Raw(" and has_error = false and posting_date between ").P(p.From).Raw(" and ").P(p.To)
                .Raw(" group by counterparty_inn, currency order by currency"), token);
            var documents = inns.Length == 0 ? [] : await ctx.QueryAsync<EdoRow>(new Sql()
                .Raw("select counterparty_inn as inn,")
                .Raw(" (count(*) filter (where owner = 'Incoming'))::int as incoming_count,")
                .Raw(" coalesce(sum(total_sum) filter (where owner = 'Incoming'), 0)::bigint as incoming_sum,")
                .Raw(" (count(*) filter (where owner = 'Outgoing'))::int as outgoing_count,")
                .Raw(" coalesce(sum(total_sum) filter (where owner = 'Outgoing'), 0)::bigint as outgoing_sum")
                .Raw(" from edo_documents where tenant_id = ").P(t).Raw(" and counterparty_inn = any(").P(inns).Raw(")")
                .Raw(" and status = any(").P(counted).Raw(") and document_date between ").P(p.From).Raw(" and ").P(p.To)
                .Raw(" group by counterparty_inn"), token);

            var edoCurrency = ctx.Options.Edo.Currency;
            var candidates = pageRows.Select(r =>
            {
                var spellings = names.Where(n => n.Inn == r.Inn)
                    .OrderByDescending(n => n.Occurrences).ThenByDescending(n => n.LastSeenAt).ThenBy(n => n.Name, StringComparer.Ordinal)
                    .Select(n => n.Name).ToList();
                if (spellings.Count == 0 && directory.FirstOrDefault(d => d.Inn == r.Inn)?.Name is { Length: > 0 } dirName) spellings.Add(dirName);
                var docs = documents.Where(d => d.Inn == r.Inn)
                    .Select(d => new EdoTurnover(edoCurrency, d.IncomingCount, d.IncomingSum, d.OutgoingCount, d.OutgoingSum)).ToList();
                return new CounterpartyCandidate(r.Inn, spellings.FirstOrDefault(), spellings.Skip(1).Take(3).ToList(), r.Score, isInn,
                    r.SubstringMatch, lastPayments.FirstOrDefault(x => x.Inn == r.Inn)?.Date, lastDocuments.FirstOrDefault(x => x.Inn == r.Inn)?.Date,
                    r.Activity,
                    bank.Where(b => b.Inn == r.Inn).Select(b => new DirectionTotal(b.Currency, b.Incoming, b.Outgoing, b.IncomingCount, b.OutgoingCount)).ToList(),
                    docs);
            }).ToList();
            var next = rows.Count > limit ? Cursor.Encode(pageRows[^1].Score, pageRows[^1].Activity, pageRows[^1].Inn) : null;

            // 3. Payments without an INN in the period: never matched, reported separately.
            var withoutInn = (await ctx.QueryAsync<CountRow>(new Sql()
                .Raw("select count(*)::bigint as value from bank_transactions where tenant_id = ").P(t)
                .Raw(" and has_error = false and coalesce(counterparty_inn, '') = '' and posting_date between ").P(p.From).Raw(" and ").P(p.To), token))
                .Single().Value;

            return new CounterpartySearchResult(text, normalized, isInn, p, o.MinSimilarity,
                new Page<CounterpartyCandidate>(candidates, next), withoutInn);
        }, ct);
    }

    [GeneratedRegex(@"^(\d{9}|\d{14})$")]
    private static partial Regex InnPattern();

    private sealed class CandidateRow
    {
        public string Inn { get; set; } = "";
        public decimal Score { get; set; }
        public bool SubstringMatch { get; set; }
        public int Activity { get; set; }
    }

    private sealed class NameRow
    {
        public string Inn { get; set; } = "";
        public string Name { get; set; } = "";
        public int Occurrences { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
    }

    private sealed class LastRow
    {
        public string Inn { get; set; } = "";
        public DateOnly? Date { get; set; }
    }

    private sealed class BankRow
    {
        public string Inn { get; set; } = "";
        public string Currency { get; set; } = "";
        public long Incoming { get; set; }
        public long Outgoing { get; set; }
        public int IncomingCount { get; set; }
        public int OutgoingCount { get; set; }
    }

    private sealed class EdoRow
    {
        public string Inn { get; set; } = "";
        public int IncomingCount { get; set; }
        public long IncomingSum { get; set; }
        public int OutgoingCount { get; set; }
        public long OutgoingSum { get; set; }
    }

    private sealed class CountRow
    {
        public long Value { get; set; }
    }
}
