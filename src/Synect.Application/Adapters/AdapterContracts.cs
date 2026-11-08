using Synect.Application.Secrets;
using Synect.Domain.Sync;

namespace Synect.Application.Adapters;

/// <summary>
/// Everything an adapter needs to talk to a source for one connection. Contains no secret values:
/// credentials are fetched on demand through <see cref="Credentials"/>.
/// </summary>
public sealed record AdapterContext(
    Guid TenantId,
    Guid ConnectionId,
    string SourceCode,
    IReadOnlyDictionary<string, string> Settings,
    string? CredentialsRef,
    ICredentialProvider Credentials);

/// <summary>
/// The raw source payload a common model was built from. Stored in the raw layer as-is.
/// </summary>
public sealed record RawRecord(string ObjectType, string ExternalId, string Json);

public sealed record ConnectionTestResult(bool Success, string? Error)
{
    public static ConnectionTestResult Ok() => new(true, null);
    public static ConnectionTestResult Failed(string error) => new(false, error);
}

/// <summary>Common base of all source adapters.</summary>
public interface ISourceAdapter
{
    /// <summary>Registry key, matches <see cref="Connection.SourceCode"/>.</summary>
    string SourceCode { get; }

    /// <summary>
    /// Provider family ("KapitalBank", "Didox"). Rate limits (Adapters:{Provider}:RateLimit) and
    /// per-source concurrency limits are shared by all connections of the same provider.
    /// </summary>
    string ProviderName { get; }

    AdapterKind Kind { get; }
}

/// <summary>Base adapter failure. The message must never contain credentials.</summary>
public class AdapterException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Credentials are missing, expired or rejected. The connection needs user attention. Never retried.</summary>
public sealed class AdapterAuthenticationException(string message, Exception? inner = null)
    : AdapterException(message, inner);

/// <summary>Temporary failure (network error, HTTP 5xx, timeout). Retried with backoff.</summary>
public class AdapterTransientException(string message, Exception? inner = null)
    : AdapterException(message, inner);

/// <summary>The source answered HTTP 429. Retried, waiting at least <see cref="RetryAfter"/> when given.</summary>
public sealed class AdapterRateLimitException(string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : AdapterTransientException(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
