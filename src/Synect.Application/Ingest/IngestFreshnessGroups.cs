using Synect.Domain.Accounting;

namespace Synect.Application.Ingest;

/// <summary>
/// Freshness of one data group of a 1C connection: accounting (Ingest:AccountingStaleAfter, 48 h) or catalog
/// (Ingest:CatalogStaleAfter, 60 min). <see cref="State"/> is "fresh", "stale" or "never_received".
/// </summary>
public sealed record IngestGroupFreshness(string Group, DateTimeOffset? LastAcceptedAt, TimeSpan StaleAfter, string State)
{
    public const string Fresh = "fresh";
    public const string Stale = "stale";
    public const string NeverReceived = "never_received";
}

/// <summary>Evaluates ingest_freshness rows of one connection against the thresholds of <see cref="IngestOptions"/>.</summary>
public static class IngestFreshnessGroups
{
    public static string Name(IngestDataGroup group) => group == IngestDataGroup.Catalog ? "catalog" : "accounting";

    /// <summary>Both groups, always (a group that never received a package is "never_received").</summary>
    public static IReadOnlyList<IngestGroupFreshness> Evaluate(IReadOnlyDictionary<IngestDataGroup, DateTimeOffset> lastAccepted, IngestOptions options,
        DateTimeOffset now) =>
        [.. new[] { IngestDataGroup.Accounting, IngestDataGroup.Catalog }.Select(g =>
        {
            var threshold = options.StaleAfterOf(g);
            if (!lastAccepted.TryGetValue(g, out var at)) return new IngestGroupFreshness(Name(g), null, threshold, IngestGroupFreshness.NeverReceived);
            return new IngestGroupFreshness(Name(g), at, threshold, now - at > threshold ? IngestGroupFreshness.Stale : IngestGroupFreshness.Fresh);
        })];

    /// <summary>
    /// The 1C connection is stale when a group it receives is stale, or when it never received anything. A group the
    /// organization does not send (e.g. no catalog without the Mailo bot) does not make it stale.
    /// </summary>
    public static bool IsStale(IReadOnlyList<IngestGroupFreshness> groups) =>
        groups.Any(g => g.State == IngestGroupFreshness.Stale) || groups.All(g => g.State == IngestGroupFreshness.NeverReceived);
}
