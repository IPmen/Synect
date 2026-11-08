using Synect.Domain.Common;

namespace Synect.Domain.Sync;

/// <summary>
/// An encrypted secret of a connection (credentials, keys). Only ciphertext is stored;
/// encryption and decryption happen in the secret store.
/// </summary>
public class ConnectionSecret : TenantEntity
{
    public const string CredentialsKind = "credentials";

    public Guid ConnectionId { get; set; }

    /// <summary>What the secret is, e.g. "credentials".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Data Protection ciphertext.</summary>
    public string EncryptedValue { get; private set; } = string.Empty;

    /// <summary>Starts at 1, incremented on every rotation.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; private set; }

    public string Reference => $"connection_secrets:{Id}";

    public void SetInitial(string encryptedValue)
    {
        EncryptedValue = encryptedValue;
        Version = 1;
    }

    public void Rotate(string encryptedValue, DateTimeOffset at)
    {
        EncryptedValue = encryptedValue;
        Version++;
        RotatedAt = at;
    }
}
