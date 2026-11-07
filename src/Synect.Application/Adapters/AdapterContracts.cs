using Synect.Domain.Sync;

namespace Synect.Application.Adapters;

/// <summary>Everything an adapter needs to talk to a source for one connection. Contains no secrets.</summary>
public sealed record AdapterContext(
    Guid TenantId,
    Guid ConnectionId,
    string SourceCode,
    IReadOnlyDictionary<string, string> Settings,
    string? CredentialsRef);

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

    AdapterKind Kind { get; }
}

/// <summary>Base adapter failure. The message must never contain credentials.</summary>
public class AdapterException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Credentials are missing, expired or rejected. The connection needs user attention.</summary>
public sealed class AdapterAuthenticationException(string message, Exception? inner = null)
    : AdapterException(message, inner);
