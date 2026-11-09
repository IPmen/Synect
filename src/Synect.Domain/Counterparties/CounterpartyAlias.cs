using Synect.Domain.Common;

namespace Synect.Domain.Counterparties;

public enum CounterpartyNameSource
{
    Bank,
    Edo,
}

/// <summary>
/// One spelling of a counterparty's name as it appeared in a source (counterparty_aliases). Every variant per INN is
/// kept; <see cref="NormalizedName"/> is the search key (see <see cref="CounterpartyNameNormalizer"/>).
/// </summary>
public class CounterpartyAlias : TenantEntity
{
    public const int MaxNameLength = 500;

    public string CounterpartyInn { get; set; } = string.Empty;

    /// <summary>The name as the source wrote it (trimmed, cut to <see cref="MaxNameLength"/>).</summary>
    public string Name { get; set; } = string.Empty;

    public string NormalizedName { get; set; } = string.Empty;
    public CounterpartyNameSource Source { get; set; }

    /// <summary>Number of operations / documents with this spelling (exact after a rebuild; may overcount slightly
    /// between rebuilds when a source changes the name of an already loaded row).</summary>
    public int Occurrences { get; set; }

    /// <summary>Activity time of the first and last operation / document with this spelling.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
