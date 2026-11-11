namespace Synect.Domain.Access;

/// <summary>
/// A client API key for the MCP endpoint (api_keys). Only the SHA-256 hash of the key is stored; the key itself
/// is shown once at creation and never stored or logged.
/// A key belongs either to one organization (<see cref="TenantId"/>, as before stage 6.5) or to an account
/// (<see cref="AccountId"/>): an account key sees the organizations of its account (all of them, or those listed in
/// api_key_tenants). Account keys are invisible to the application role (row-level security on tenant_id).
/// </summary>
public class ApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The organization of a single-organization key; null for an account key.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>The account of an account key; null for a single-organization key.</summary>
    public Guid? AccountId { get; set; }

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
