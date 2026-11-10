using Synect.Domain.Common;

namespace Synect.Domain.Access;

/// <summary>
/// A client API key for the MCP endpoint (api_keys). Only the SHA-256 hash of the key is stored; the key itself
/// is shown once at creation and never stored or logged.
/// </summary>
public class ApiKey : TenantEntity
{
    public const string ScopeRead = "read";
    public const string ScopeSyncRequest = "sync_request";

    /// <summary>Confirming and rejecting reconciliation matches (changes only Synect's own reconciliation data).</summary>
    public const string ScopeReview = "review";

    public static readonly string[] KnownScopes = [ScopeRead, ScopeSyncRequest, ScopeReview];

    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 of the UTF-8 key string (32 bytes).</summary>
    public byte[] KeyHash { get; set; } = [];

    /// <summary>First characters of the key, to recognise it in lists (not enough to use it).</summary>
    public string KeyPrefix { get; set; } = string.Empty;

    public string[] Scopes { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsRevoked => RevokedAt is not null;
}
