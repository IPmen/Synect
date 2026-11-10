namespace Synect.Mcp;

/// <summary>Mcp section: limits and response rules of the MCP tools.</summary>
public sealed class McpOptions
{
    /// <summary>Rows per answer when the caller gives no limit (smaller than Analytics, to keep model context small).</summary>
    public int DefaultLimit { get; set; } = 50;

    /// <summary>Largest accepted limit.</summary>
    public int MaxLimit { get; set; } = 200;

    /// <summary>Longest accepted period (5 years).</summary>
    public int MaxPeriodDays { get; set; } = 1827;

    /// <summary>Hard limit for one tool call (Analytics applies the same 5 s to its queries).</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Account numbers are returned as ****1234 unless this is false.</summary>
    public bool MaskAccountNumbers { get; set; } = true;

    /// <summary>Third-party text (payment purpose, names, status comments) is cut to this many characters.</summary>
    public int UntrustedTextMaxLength { get; set; } = 300;

    /// <summary>A source whose last successful load is older than this gets a freshness note.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromHours(36);

    public McpRateLimitOptions RateLimit { get; set; } = new();

    public McpHttpRateLimitOptions HttpRateLimit { get; set; } = new();

    public McpNotesOptions Notes { get; set; } = new();
}

/// <summary>Mcp:RateLimit.</summary>
public sealed class McpRateLimitOptions
{
    /// <summary>Tool calls per minute and API key.</summary>
    public int RequestsPerMinutePerKey { get; set; } = 60;

    /// <summary>Tool calls running at the same time per tenant.</summary>
    public int MaxConcurrentPerTenant { get; set; } = 4;

    /// <summary>Window of the per-key limit.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Mcp:HttpRateLimit. Every HTTP request to /mcp counts (initialize and tools/list too).</summary>
public sealed class McpHttpRateLimitOptions
{
    /// <summary>Requests per window and API key (authenticated requests).</summary>
    public int RequestsPerMinutePerKey { get; set; } = 300;

    /// <summary>Requests per window and client IP (any request).</summary>
    public int RequestsPerMinutePerIp { get; set; } = 300;

    /// <summary>Failed authentications (HTTP 401) per window and client IP; further requests get 429 until the window ends.</summary>
    public int FailedAuthPerMinutePerIp { get; set; } = 20;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>Mcp:Notes. Warnings added to the notes of every answer of the matching tool group.</summary>
public sealed class McpNotesOptions
{
    public string[] Bank { get; set; } =
    [
        "Kapital Bank amount unit (sum or tiyin) is NOT confirmed yet (Adapters:KapitalBank:AmountUnit); bank amounts may be off by a factor of 100.",
        "Bank operations rejected by the bank (has_error) are always excluded.",
    ];

    public string[] Documents { get; set; } =
    [
        "Didox amounts are treated as UZS.",
    ];

    public string[] Counterparties { get; set; } =
    [
        "Top, comparison, new/lost and concentration answers use whole calendar months touched by the period.",
    ];

    public string[] Reconciliation { get; set; } =
    [
        "Results are possible matches with a confidence score (0-100) and their reasons; the accountant makes the decision.",
        "Matching rules and weights (Reconciliation:*) are assumptions still to be checked on real data.",
        "Only UZS payments are matched with Didox documents (documents are in UZS); payments in other currencies are listed as excluded, never converted.",
        "Payments without a counterparty INN are never matched automatically.",
    ];

    public string[] Attention { get; set; } =
    [
        "Attention items come from Synect's rules over loaded data; their thresholds (Attention:*) are assumptions still to be checked on real data. Present each item as a possible discrepancy with its basis (body, details), not as a confirmed violation.",
    ];

    /// <summary>Added to tools that compare documents and payments by INN without the reconciliation.</summary>
    public string NotReconciliation { get; set; } =
        "This is a check whether Didox documents exist for the INN, not a matching of payments with documents (Это проверка наличия документов по ИНН, а не сопоставление платежей с документами). For matches use the reconciliation tools (get_paid_documents, get_unpaid_documents, get_unmatched_payments, get_counterparty_balance_estimate).";
}
