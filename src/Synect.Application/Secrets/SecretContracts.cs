using Synect.Application.Adapters;

namespace Synect.Application.Secrets;

/// <summary>
/// A secret in memory. <see cref="ToString"/> is redacted so that the value cannot leak into logs or
/// exception messages by accident; call <see cref="Reveal"/> only where the plain value is needed.
/// </summary>
public sealed class SecretValue(string value)
{
    private readonly string _value = value ?? throw new ArgumentNullException(nameof(value));

    public string Reveal() => _value;

    public override string ToString() => "***";
}

/// <summary>Encrypted per-connection secret storage (connection_secrets). Works under the current tenant.</summary>
public interface ISecretStore
{
    /// <summary>Decrypted secret, or null when the connection has no secret of this kind.</summary>
    Task<SecretValue?> GetAsync(Guid connectionId, string kind, CancellationToken ct);

    /// <summary>
    /// Creates the secret (version 1) or replaces it (as a rotation). For the credentials kind the
    /// connection's credentials_ref is pointed at the secret. Returns the reference.
    /// </summary>
    Task<string> SetAsync(Guid connectionId, string kind, SecretValue value, CancellationToken ct);

    /// <summary>Replaces an existing secret, increments its version and sets rotated_at.</summary>
    Task RotateAsync(Guid connectionId, string kind, SecretValue newValue, CancellationToken ct);
}

/// <summary>Gives adapters the credentials of the connection they are working for.</summary>
public interface ICredentialProvider
{
    /// <exception cref="AdapterAuthenticationException">No credentials are configured.</exception>
    Task<SecretValue> GetCredentialsAsync(AdapterContext context, CancellationToken ct);
}
