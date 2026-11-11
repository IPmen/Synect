namespace Synect.Application.Ingest;

/// <summary>Ingest section: the receiver of 1C packages (POST /ingest/1c/v1/batches).</summary>
public sealed class IngestOptions
{
    /// <summary>Most items in one package; more gives HTTP 413.</summary>
    public int MaxItems { get; set; } = 500;

    /// <summary>Largest request body in bytes, after gzip decompression; more gives HTTP 413.</summary>
    public int MaxBytes { get; set; } = 2 * 1024 * 1024;

    public const int DefaultSchemaVersion = 1;

    /// <summary>
    /// Accepted schema_version values of the export contract (Ingest:SupportedSchemaVersions); other versions are refused
    /// with a clear error. Empty (not configured) means version 1 only. No default element here on purpose: the
    /// configuration binder appends to an existing array instead of replacing it.
    /// </summary>
    public int[] SupportedSchemaVersions { get; set; } = [];

    /// <summary>The configured versions, or [1] when none is configured.</summary>
    public int[] AcceptedSchemaVersions =>
        SupportedSchemaVersions.Length == 0 ? [DefaultSchemaVersion] : SupportedSchemaVersions.Distinct().Order().ToArray();

    /// <summary>The 1C source counts as stale when its last accepted package is older than this.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromHours(48);

    /// <summary>Currency of an item that has none (the regulated accounting currency of Uzbekistan).</summary>
    public string DefaultCurrency { get; set; } = "UZS";

    /// <summary>Offset of date-times sent without one (modified_at, exported_at): Tashkent, UTC+5.</summary>
    public TimeSpan SourceUtcOffset { get; set; } = TimeSpan.FromHours(5);

    /// <summary>Longest accepted free text (names, purpose, comment) in characters.</summary>
    public int MaxTextLength { get; set; } = 4000;

    /// <summary>Key lists of full exports that never got their last package are removed after this time.</summary>
    public TimeSpan FullSyncRetention { get; set; } = TimeSpan.FromDays(7);

    public IngestRateLimitOptions RateLimit { get; set; } = new();
}

/// <summary>Ingest:RateLimit (in memory, per process).</summary>
public sealed class IngestRateLimitOptions
{
    /// <summary>Packages per window and ingest key; over the limit the answer is 429 with Retry-After.</summary>
    public int PackagesPerMinute { get; set; } = 60;

    /// <summary>Failed authentications (401) per window and client IP; further requests get 429 until the window ends.</summary>
    public int FailedAuthPerMinutePerIp { get; set; } = 20;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
