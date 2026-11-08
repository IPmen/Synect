using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Application.Secrets;
using Synect.Domain.Sync;

namespace Synect.Infrastructure.Secrets;

/// <summary>
/// Stores connection secrets encrypted with ASP.NET Core Data Protection. The protector purpose chain
/// includes tenant, connection and kind, so a ciphertext copied to another row (or tenant) cannot be
/// decrypted. The database only ever sees ciphertext; keys live outside the repository (Secrets:KeysPath).
/// </summary>
public sealed class DataProtectionSecretStore(ISynectDbContext db, IDataProtectionProvider protection, TimeProvider time) : ISecretStore
{
    private const string Purpose = "Synect.ConnectionSecrets.v1";

    public Task<SecretValue?> GetAsync(Guid connectionId, string kind, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var secret = await db.ConnectionSecrets.AsNoTracking()
                .SingleOrDefaultAsync(s => s.ConnectionId == connectionId && s.Kind == kind, token);
            return secret is null
                ? null
                : new SecretValue(Protector(secret.TenantId, connectionId, kind).Unprotect(secret.EncryptedValue));
        }, ct);

    public Task<string> SetAsync(Guid connectionId, string kind, SecretValue value, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var connection = await db.Connections.SingleOrDefaultAsync(c => c.Id == connectionId, token)
                ?? throw new KeyNotFoundException($"Connection {connectionId} not found for the current tenant.");
            var cipher = Protector(connection.TenantId, connectionId, kind).Protect(value.Reveal());
            var now = time.GetUtcNow();

            var secret = await db.ConnectionSecrets.SingleOrDefaultAsync(s => s.ConnectionId == connectionId && s.Kind == kind, token);
            if (secret is null)
            {
                secret = new ConnectionSecret { TenantId = connection.TenantId, ConnectionId = connectionId, Kind = kind, CreatedAt = now };
                secret.SetInitial(cipher);
                db.ConnectionSecrets.Add(secret);
            }
            else
            {
                secret.Rotate(cipher, now);
            }

            if (kind == ConnectionSecret.CredentialsKind) connection.CredentialsRef = secret.Reference;
            await db.SaveChangesAsync(token);
            return secret.Reference;
        }, ct);

    public Task RotateAsync(Guid connectionId, string kind, SecretValue newValue, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var secret = await db.ConnectionSecrets.SingleOrDefaultAsync(s => s.ConnectionId == connectionId && s.Kind == kind, token)
                ?? throw new KeyNotFoundException($"Connection {connectionId} has no '{kind}' secret to rotate.");
            secret.Rotate(Protector(secret.TenantId, connectionId, kind).Protect(newValue.Reveal()), time.GetUtcNow());
            await db.SaveChangesAsync(token);
        }, ct);

    private IDataProtector Protector(Guid tenantId, Guid connectionId, string kind) =>
        protection.CreateProtector(Purpose, tenantId.ToString("N"), connectionId.ToString("N"), kind);
}
