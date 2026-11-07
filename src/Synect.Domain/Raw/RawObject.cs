using Synect.Domain.Common;

namespace Synect.Domain.Raw;

/// <summary>
/// The latest raw payload received from a source for one external object.
/// Only the latest version is kept: a changed payload overwrites the row.
/// </summary>
public class RawObject : TenantEntity
{
    public Guid ConnectionId { get; set; }
    public string SourceCode { get; set; } = string.Empty;

    /// <summary>Source-specific object type, e.g. "kapitalbank.transaction", "didox.document".</summary>
    public string ObjectType { get; set; } = string.Empty;

    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Raw JSON as received (stored as jsonb).</summary>
    public string Payload { get; private set; } = "{}";

    /// <summary>SHA-256 (hex) of the canonical JSON form of the payload.</summary>
    public string Hash { get; private set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>When the payload last changed.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Replaces the payload when the hash differs. Returns true if the payload changed.</summary>
    public bool Apply(string payload, string hash, DateTimeOffset at)
    {
        LastSeenAt = at;
        if (Hash == hash) return false;
        Payload = payload;
        Hash = hash;
        UpdatedAt = at;
        return true;
    }
}
