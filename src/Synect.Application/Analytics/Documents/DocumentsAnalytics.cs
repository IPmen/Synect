using Synect.Domain.Edo;

namespace Synect.Application.Analytics;

public sealed record EdoDocumentItem(
    Guid Id, string ExternalId, string DocType, string Status, string? StatusName, string? StatusComment,
    EdoDocumentOwner Owner, string? Number, DateOnly? DocumentDate,
    string? CounterpartyInn, string? CounterpartyName, string? ContractNumber, DateOnly? ContractDate,
    long TotalSum, long TotalVatSum, string Currency,
    DateTimeOffset? SourceCreatedAt, DateTimeOffset? SourceUpdatedAt, int? AgeDays);

public sealed record DocumentCount(string Status, string? StatusName, string DocType, EdoDocumentOwner Owner, int Count, long TotalSum);

public sealed record VatSummary(EdoDocumentOwner Owner, string Currency, int Count, long TotalSum, long TotalVatSum, long TotalDeliverySum);

/// <summary>Search filters; all optional and combined with AND. Number and contract number match exactly or by prefix.</summary>
public sealed record DocumentSearch(
    string? Number = null, string? CounterpartyInn = null, string? ContractNumber = null,
    string? DocType = null, string? Status = null, EdoDocumentOwner? Owner = null,
    DatePeriod? Period = null, DateBasis Basis = DateBasis.DocumentDate);

/// <summary>Block V: Didox documents (V1-V7).</summary>
public sealed class DocumentsAnalytics(AnalyticsContext ctx)
{
    public const string AwaitingPartnerSignature = "1";
    public const string AwaitingOurSignature = "2";
    public const string Rejected = "4";

    /// <summary>V1. Incoming documents waiting for our signature (status 2), oldest first, with age in days.</summary>
    public Task<AnalyticsResult<Page<EdoDocumentItem>>> GetAwaitingOurSignatureAsync(PageRequest? page, CancellationToken ct) =>
        AwaitingAsync(EdoDocumentOwner.Incoming, AwaitingOurSignature, page, ct);

    /// <summary>V2. Outgoing documents waiting for the partner's signature (status 1), oldest first, with age in days.</summary>
    public Task<AnalyticsResult<Page<EdoDocumentItem>>> GetAwaitingPartnerSignatureAsync(PageRequest? page, CancellationToken ct) =>
        AwaitingAsync(EdoDocumentOwner.Outgoing, AwaitingPartnerSignature, page, ct);

    /// <summary>V3. Document counts (and sums) by status, type and owner for the period.</summary>
    public Task<AnalyticsResult<IReadOnlyList<DocumentCount>>> GetCountsAsync(DatePeriod period, DateBasis basis, CancellationToken ct) =>
        ctx.RunAsync<IReadOnlyList<DocumentCount>>(async token =>
        {
            var sql = new Sql()
                .Raw("select d.status, d.doc_type, d.owner, count(*)::int as count, coalesce(sum(d.total_sum), 0)::bigint as total_sum")
                .Raw(" from edo_documents d where d.tenant_id = ").P(ctx.TenantId);
            PeriodFilter(sql, ctx, "d", period, basis);
            sql.Raw(" group by d.status, d.doc_type, d.owner order by d.owner, d.status, d.doc_type");
            return (await ctx.QueryAsync<CountRow>(sql, token))
                .Select(r => new DocumentCount(r.Status, StatusName(r.Status), r.DocType, Enum.Parse<EdoDocumentOwner>(r.Owner), r.Count, r.TotalSum))
                .ToList();
        }, ct);

    /// <summary>V4. Rejected documents (status 4) of the period with the status comment and counterparty.</summary>
    public Task<AnalyticsResult<Page<EdoDocumentItem>>> GetRejectedAsync(
        DatePeriod period, DateBasis basis, PageRequest? page, CancellationToken ct) =>
        SearchAsync(new DocumentSearch(Status: Rejected, Period: period, Basis: basis), page, ct);

    /// <summary>V5. Search by number, counterparty INN, contract number, type, status, owner and period. Newest first.</summary>
    public Task<AnalyticsResult<Page<EdoDocumentItem>>> SearchAsync(DocumentSearch filter, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var sql = Select().Raw(" where d.tenant_id = ").P(ctx.TenantId);
            if (filter.Number is { Length: > 0 } number) PrefixOrExact(sql, "d.number", number);
            if (filter.ContractNumber is { Length: > 0 } contract) PrefixOrExact(sql, "d.contract_number", contract);
            if (filter.CounterpartyInn is { Length: > 0 } inn) sql.Raw(" and d.counterparty_inn = ").P(inn);
            if (filter.DocType is { Length: > 0 } type) sql.Raw(" and d.doc_type = ").P(type);
            if (filter.Status is { Length: > 0 } status) sql.Raw(" and d.status = ").P(status);
            if (filter.Owner is { } owner) sql.Raw(" and d.owner = ").P(owner.ToString());
            if (filter.Period is { } period) PeriodFilter(sql, ctx, "d", period, filter.Basis);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (coalesce(d.document_date, '0001-01-01'::date), d.id) < (").P(DateOnly.Parse(c[0]!)).Raw(", ").P(Guid.Parse(c[1]!)).Raw(")");
            }
            sql.Raw(" order by coalesce(d.document_date, '0001-01-01'::date) desc, d.id desc limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<DocRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit, r => Map(r, null),
                r => Cursor.Encode(r.DocumentDate ?? DateOnly.MinValue, r.Id));
        }, ct);

    /// <summary>V6. Largest documents (counted statuses) of the period, by total sum descending.</summary>
    public Task<AnalyticsResult<Page<EdoDocumentItem>>> GetLargestAsync(
        DatePeriod period, DateBasis basis, EdoDocumentOwner? owner, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var sql = Select().Raw(" where d.tenant_id = ").P(ctx.TenantId)
                .Raw(" and d.status = any(").P(ctx.Options.Edo.CountedStatuses).Raw(")");
            if (owner is { } o) sql.Raw(" and d.owner = ").P(o.ToString());
            PeriodFilter(sql, ctx, "d", period, basis);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                var sum = long.Parse(c[0]!); var id = Guid.Parse(c[1]!);
                sql.Raw(" and (d.total_sum < ").P(sum).Raw(" or (d.total_sum = ").P(sum).Raw(" and d.id > ").P(id).Raw("))");
            }
            sql.Raw(" order by d.total_sum desc, d.id limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<DocRow>(sql, token);
            return AnalyticsContext.ToPage(rows, limit, r => Map(r, null), r => Cursor.Encode(r.TotalSum, r.Id));
        }, ct);

    /// <summary>V7. VAT and totals of incoming and outgoing documents (counted statuses) for the period.</summary>
    public Task<AnalyticsResult<IReadOnlyList<VatSummary>>> GetVatSummaryAsync(DatePeriod period, DateBasis basis, CancellationToken ct) =>
        ctx.RunAsync<IReadOnlyList<VatSummary>>(async token =>
        {
            var sql = new Sql()
                .Raw("select d.owner, count(*)::int as count, coalesce(sum(d.total_sum), 0)::bigint as total_sum,")
                .Raw(" coalesce(sum(d.total_vat_sum), 0)::bigint as total_vat_sum, coalesce(sum(d.total_delivery_sum), 0)::bigint as total_delivery_sum")
                .Raw(" from edo_documents d where d.tenant_id = ").P(ctx.TenantId)
                .Raw(" and d.status = any(").P(ctx.Options.Edo.CountedStatuses).Raw(")");
            PeriodFilter(sql, ctx, "d", period, basis);
            sql.Raw(" group by d.owner");
            var rows = (await ctx.QueryAsync<VatRow>(sql, token)).ToDictionary(r => r.Owner);
            return new[] { EdoDocumentOwner.Incoming, EdoDocumentOwner.Outgoing }.Select(o =>
                rows.TryGetValue(o.ToString(), out var r)
                    ? new VatSummary(o, ctx.Options.Edo.Currency, r.Count, r.TotalSum, r.TotalVatSum, r.TotalDeliverySum)
                    : new VatSummary(o, ctx.Options.Edo.Currency, 0, 0, 0, 0)).ToList();
        }, ct);

    /// <summary>Adds the period condition for the chosen date basis (local dates; timestamps compared in UTC).</summary>
    internal static void PeriodFilter(Sql sql, AnalyticsContext ctx, string alias, DatePeriod period, DateBasis basis)
    {
        switch (basis)
        {
            case DateBasis.DocumentDate:
                sql.Raw($" and {alias}.document_date between ").P(period.From).Raw(" and ").P(period.To);
                break;
            default:
                var column = basis == DateBasis.SourceCreated ? "source_created_at" : "source_updated_at";
                var (from, to) = ctx.UtcRange(period);
                sql.Raw($" and {alias}.{column} >= ").P(from).Raw($" and {alias}.{column} < ").P(to);
                break;
        }
    }

    private Task<AnalyticsResult<Page<EdoDocumentItem>>> AwaitingAsync(
        EdoDocumentOwner owner, string status, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var sql = Select().Raw(" where d.tenant_id = ").P(ctx.TenantId)
                .Raw(" and d.owner = ").P(owner.ToString()).Raw(" and d.status = ").P(status);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (coalesce(d.document_date, '9999-12-31'::date), d.id) > (").P(DateOnly.Parse(c[0]!)).Raw(", ").P(Guid.Parse(c[1]!)).Raw(")");
            }
            sql.Raw(" order by coalesce(d.document_date, '9999-12-31'::date), d.id limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<DocRow>(sql, token);
            var today = ctx.Today;
            return AnalyticsContext.ToPage(rows, limit, r => Map(r, today),
                r => Cursor.Encode(r.DocumentDate ?? DateOnly.MaxValue, r.Id));
        }, ct);

    private static Sql Select() => new Sql()
        .Raw("select d.id, d.external_id, d.doc_type, d.status, d.status_comment, d.owner, d.number, d.document_date,")
        .Raw(" d.counterparty_inn, d.counterparty_name, d.contract_number, d.contract_date, d.total_sum, d.total_vat_sum,")
        .Raw(" d.source_created_at, d.source_updated_at from edo_documents d");

    /// <summary>
    /// Exact or prefix match. "LIKE 'x%'" also matches x itself, and unlike "= x OR LIKE" it can use the
    /// text_pattern_ops index.
    /// </summary>
    private static void PrefixOrExact(Sql sql, string column, string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        sql.Raw($" and {column} like ").P(escaped + "%");
    }

    private EdoDocumentItem Map(DocRow r, DateOnly? today)
    {
        var date = r.DocumentDate ?? (r.SourceCreatedAt is { } created ? ctx.LocalDate(created) : null);
        return new EdoDocumentItem(r.Id, r.ExternalId, r.DocType, r.Status, StatusName(r.Status), r.StatusComment,
            Enum.Parse<EdoDocumentOwner>(r.Owner), r.Number, r.DocumentDate, r.CounterpartyInn, r.CounterpartyName,
            r.ContractNumber, r.ContractDate, r.TotalSum, r.TotalVatSum, ctx.Options.Edo.Currency,
            r.SourceCreatedAt, r.SourceUpdatedAt,
            today is { } t && date is { } d ? t.DayNumber - d.DayNumber : null);
    }

    /// <summary>Didox status names (see DidoxStatuses in Infrastructure; repeated here to keep Analytics free of adapters).</summary>
    public static string? StatusName(string code) => code switch
    {
        "0" => "draft",
        "1" => "awaiting partner signature",
        "2" => "awaiting our signature",
        "3" => "signed",
        "4" => "rejected",
        "6" => "awaiting agent signature",
        "8" => "signed by agent",
        "40" => "invalid",
        "60" => "awaiting agent signature (special scenario)",
        "140" => "accepted by responsible person",
        "160" => "delivered",
        "190" => "returned",
        "240" => "other acceptance stage",
        _ => null,
    };

    private sealed class DocRow
    {
        public Guid Id { get; set; }
        public string ExternalId { get; set; } = "";
        public string DocType { get; set; } = "";
        public string Status { get; set; } = "";
        public string? StatusComment { get; set; }
        public string Owner { get; set; } = "";
        public string? Number { get; set; }
        public DateOnly? DocumentDate { get; set; }
        public string? CounterpartyInn { get; set; }
        public string? CounterpartyName { get; set; }
        public string? ContractNumber { get; set; }
        public DateOnly? ContractDate { get; set; }
        public long TotalSum { get; set; }
        public long TotalVatSum { get; set; }
        public DateTimeOffset? SourceCreatedAt { get; set; }
        public DateTimeOffset? SourceUpdatedAt { get; set; }
    }

    private sealed class CountRow
    {
        public string Status { get; set; } = "";
        public string DocType { get; set; } = "";
        public string Owner { get; set; } = "";
        public int Count { get; set; }
        public long TotalSum { get; set; }
    }

    private sealed class VatRow
    {
        public string Owner { get; set; } = "";
        public int Count { get; set; }
        public long TotalSum { get; set; }
        public long TotalVatSum { get; set; }
        public long TotalDeliverySum { get; set; }
    }
}
