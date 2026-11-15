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

    public static readonly TimeSpan DefaultAccountingStaleAfter = TimeSpan.FromHours(48);
    public static readonly TimeSpan DefaultCatalogStaleAfter = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Accounting data of 1C (sent about once an hour) counts as stale when its last accepted package is older than this
    /// (Ingest:AccountingStaleAfter). Not set: the former name Ingest:StaleAfter, then 48 hours. Read through
    /// <see cref="AccountingStaleAfterOrDefault"/>. Deliberately absent from appsettings.json, so that a value configured
    /// under the former name elsewhere (user-secrets, environment, appsettings.{Environment}.json) still applies.
    /// </summary>
    public TimeSpan? AccountingStaleAfter { get; set; }

    /// <summary>Former name of <see cref="AccountingStaleAfter"/> (stage 8a); still read when the new name is not set.</summary>
    public TimeSpan? StaleAfter { get; set; }

    /// <summary>The accounting threshold in effect: AccountingStaleAfter, else StaleAfter, else 48 hours.</summary>
    public TimeSpan AccountingStaleAfterOrDefault => AccountingStaleAfter ?? StaleAfter ?? DefaultAccountingStaleAfter;

    /// <summary>
    /// The catalog of 1C (changes every minute, a full export once a day) counts as stale when its last accepted package is
    /// older than this (Ingest:CatalogStaleAfter, 60 minutes).
    /// </summary>
    public TimeSpan CatalogStaleAfter { get; set; } = DefaultCatalogStaleAfter;

    /// <summary>Threshold of a data group.</summary>
    public TimeSpan StaleAfterOf(Synect.Domain.Accounting.IngestDataGroup group) =>
        group == Synect.Domain.Accounting.IngestDataGroup.Catalog ? CatalogStaleAfter : AccountingStaleAfterOrDefault;

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
    public const int DefaultPackagesPerMinute = 120;
    public const int DefaultFailedAuthPerMinutePerIp = 20;

    /// <summary>
    /// Packages per window and ingest key (120: catalog changes every minute plus the hourly accounting export, which is
    /// several packages per entity); over the limit the answer is 429 with Retry-After. It counts only packages of a VALID
    /// key, so it does not affect guessing keys: that is limited by <see cref="FailedAuthPerMinutePerIp"/>, checked first.
    /// </summary>
    public int PackagesPerMinute { get; set; } = DefaultPackagesPerMinute;

    /// <summary>
    /// Failed authentications (401) per window and client IP; further requests from that IP get 429 until the window ends,
    /// even with a valid key.
    /// </summary>
    public int FailedAuthPerMinutePerIp { get; set; } = DefaultFailedAuthPerMinutePerIp;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
