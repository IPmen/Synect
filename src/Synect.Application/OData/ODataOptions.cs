namespace Synect.Application.OData;

/// <summary>OData section: the 1C:Fresh (Clobus) connector that reads an application over the standard OData interface.</summary>
public sealed class ODataOptions
{
    /// <summary>
    /// Hosts base_url may point to (a host itself or any of its subdomains). Empty (not configured) means clobus.uz only.
    /// No default element here on purpose: the configuration binder appends to an existing array instead of replacing it.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    public static readonly string[] DefaultAllowedHosts = ["clobus.uz"];

    /// <summary>The configured hosts, or clobus.uz when none is configured.</summary>
    public string[] EffectiveAllowedHosts =>
        AllowedHosts.Length == 0 ? DefaultAllowedHosts : AllowedHosts.Select(h => h.Trim().TrimEnd('.').ToLowerInvariant()).Where(h => h.Length > 0).ToArray();

    /// <summary>Requests per second to one application (5).</summary>
    public double RequestsPerSecond { get; set; } = 5;

    /// <summary>Time limit of one HTTP request (60 s).</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Retries of a request after a timeout, a network error, 429 or 5xx (3); 401/403 are never retried.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Pause before the first retry, doubled each time (1 s), at most <see cref="MaxRetryDelay"/>; Retry-After is respected.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Records per page ($top) when listing keys and versions (500).</summary>
    public int PageSize { get; set; } = 500;

    /// <summary>Refs per request when changed records are read by key with a $filter of "or" terms (20; the URL stays short).</summary>
    public int RecordsPerKeyRequest { get; set; } = 20;

    /// <summary>Safety limit of pages of one resource (10 000).</summary>
    public int MaxPages { get; set; } = 10_000;

    /// <summary>Delta of documents: documents dated within this many days back are compared by DataVersion (45).</summary>
    public int DeltaWindowDays { get; set; } = 45;

    /// <summary>A load is a full one when the last completed full load of the connection is older than this (24 h).</summary>
    public TimeSpan FullSyncInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Daytime delta loads every N hours between Sync:Schedule:DaytimeStart and DaytimeEnd (2; 0 = nightly only).</summary>
    public int DaytimeRefreshHours { get; set; } = 2;

    /// <summary>
    /// Folder with mapping profiles (&lt;name&gt;.json) that override the built-in ones without a rebuild; empty = built-in only.
    /// A connection can also carry its own profile in its settings (profile_json).
    /// </summary>
    public string? ProfilesPath { get; set; }

    /// <summary>Profile used when a connection names none.</summary>
    public string DefaultProfile { get; set; } = "clobus-bp-uz";

    /// <summary>
    /// OData:AllowHttpLocalhostInDevelopment (false): in the Development environment only, base_url may be http://localhost
    /// (a local test server). Ignored elsewhere; the host calls <see cref="AllowLocalDevelopment"/> after checking the environment.
    /// </summary>
    public bool AllowHttpLocalhostInDevelopment { get; set; }

    /// <summary>True when the host enabled http://localhost and loopback addresses (Development only). Not bound from configuration.</summary>
    public bool LocalDevelopmentAllowed { get; private set; }

    public void AllowLocalDevelopment() => LocalDevelopmentAllowed = true;
}
