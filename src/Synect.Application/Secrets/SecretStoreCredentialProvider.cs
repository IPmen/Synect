using Synect.Application.Adapters;
using Synect.Domain.Sync;

namespace Synect.Application.Secrets;

/// <summary>Resolves <see cref="AdapterContext.CredentialsRef"/> through the secret store.</summary>
public sealed class SecretStoreCredentialProvider(ISecretStore store) : ICredentialProvider
{
    public async Task<SecretValue> GetCredentialsAsync(AdapterContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(context.CredentialsRef))
            throw new AdapterAuthenticationException("No credentials are configured for this connection.");

        try
        {
            return await store.GetAsync(context.ConnectionId, ConnectionSecret.CredentialsKind, ct)
                ?? throw new AdapterAuthenticationException("Credentials referenced by the connection were not found.");
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            // Keys were lost or changed: the user has to enter the credentials again.
            throw new AdapterAuthenticationException("Stored credentials cannot be decrypted.", ex);
        }
    }
}
