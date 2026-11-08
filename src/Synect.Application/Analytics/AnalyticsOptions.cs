namespace Synect.Application.Analytics;

/// <summary>Analytics.</summary>
public sealed class AnalyticsOptions
{
    /// <summary>Hard limit for one analytics call (also applied as statement_timeout on PostgreSQL).</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// max_parallel_workers_per_gather for analytics transactions (PostgreSQL). Analytics queries are short;
    /// starting parallel workers costs more than it saves (about 150 ms per query on Windows). Null keeps the server default.
    /// </summary>
    public int? MaxParallelWorkersPerGather { get; set; } = 0;

    public int DefaultPageSize { get; set; } = 100;
    public int MaxPageSize { get; set; } = 1000;

    /// <summary>Period dates are local dates of this offset (Uzbekistan, UTC+5).</summary>
    public TimeSpan UtcOffset { get; set; } = TimeSpan.FromHours(5);

    public EdoAnalyticsOptions Edo { get; set; } = new();
}

/// <summary>Analytics:Edo.</summary>
public sealed class EdoAnalyticsOptions
{
    /// <summary>
    /// Didox status codes counted in sums (VAT, tops, turnover). ASSUMPTION to be confirmed:
    /// 1, 2, 3, 6, 8, 60 (draft 0, rejected 4 and invalid 40 are excluded).
    /// </summary>
    public string[] CountedStatuses { get; set; } = ["1", "2", "3", "6", "8", "60"];

    /// <summary>Didox amounts are in sum; their currency for grouping.</summary>
    public string Currency { get; set; } = "UZS";
}
