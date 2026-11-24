using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Accounting;
using Synect.Domain.Sync;

namespace Synect.Application.Ingest;

/// <summary>The organization and 1C connection behind a presented ingest key, as found by <see cref="IIngestKeyResolver"/>.</summary>
/// <param name="TenantTin">TIN of the organization of the key (null when the organization has none).</param>
public sealed record IngestKeyIdentity(Guid IngestKeyId, Guid TenantId, Guid ConnectionId, string? TenantTin);

/// <summary>
/// Finds a non-revoked ingest key by the SHA-256 of the presented key, before any tenant context exists. The PostgreSQL
/// implementation calls the SECURITY DEFINER function synect_resolve_ingest_key, so the application role never reads
/// ingest_keys outside a tenant context.
/// </summary>
public interface IIngestKeyResolver
{
    Task<IngestKeyIdentity?> ResolveAsync(byte[] keyHash, CancellationToken ct);
}

/// <summary>Ingest key format: "ik_synect_" + 32 random bytes (base64url). MCP keys start with "sk_synect_".</summary>
public static class IngestKeyFormat
{
    public const string Prefix = "ik_synect_";
    public const int RandomBytes = 32;

    /// <summary>Characters kept in key_prefix: the prefix plus 6 random characters.</summary>
    public const int StoredPrefixLength = 16;

    private const string Base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    public static string Generate() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(RandomBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Shape check before hashing: anything else cannot be an ingest key we issued (an MCP key never passes).</summary>
    public static bool LooksLikeKey(string? value) =>
        value is { Length: >= 50 and <= 128 } && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value.AsSpan(Prefix.Length).IndexOfAnyExcept(Base64UrlAlphabet) < 0;
}

/// <summary>The key is present only here, right after creation; it is never stored.</summary>
public sealed record CreatedIngestKey(Guid Id, Guid ConnectionId, string Name, string KeyPrefix, string Key)
{
    /// <summary>Never prints the key (records print every property by default).</summary>
    public override string ToString() => $"IngestKey {KeyPrefix}… ({Name})";
}

public sealed record IngestKeyInfo(Guid Id, Guid ConnectionId, string Name, string KeyPrefix, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

public sealed record Accounting1CConnectionInfo(Guid Id, string Name, bool Created);

/// <summary>Creates, lists and revokes ingest keys of the current organization (tenant context required).</summary>
public sealed class IngestKeyService(ISynectDbContext db, ITenantContext tenant, TimeProvider time)
{
    public const string DefaultConnectionName = "1C";

    /// <summary>The organization's 1C connection; created (Active, never loaded) when it does not exist.</summary>
    public Task<Accounting1CConnectionInfo> EnsureConnectionAsync(CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            RequireTenant();
            var existing = await db.Connections.AsNoTracking()
                .Where(c => c.AdapterKind == AdapterKind.Accounting1C && c.SourceCode != Connection.OneCFreshODataSourceCode)
                .OrderBy(c => c.Status == ConnectionStatus.Disabled).ThenBy(c => c.CreatedAt).FirstOrDefaultAsync(token);
            if (existing is not null) return new Accounting1CConnectionInfo(existing.Id, existing.Name, false);
            // An active 1C:Fresh OData connection is the source of 1C data: the extension gets no connection next to it.
            await Synect.Application.Sync.OneCConnectionRule.EnsureNoOtherActiveAsync(db, null, token);
            var connection = new Connection
            {
                Name = DefaultConnectionName,
                SourceCode = Connection.Accounting1CSourceCode,
                AdapterKind = AdapterKind.Accounting1C,
                CreatedAt = time.GetUtcNow(),
            };
            db.Connections.Add(connection);
            await db.SaveChangesAsync(token);
            return new Accounting1CConnectionInfo(connection.Id, connection.Name, true);
        }, ct);

    public Task<CreatedIngestKey> CreateAsync(string name, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            RequireTenant();
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Key name is required.", nameof(name));
            var connection = await EnsureConnectionAsync(token);
            var key = IngestKeyFormat.Generate();
            var entity = new IngestKey
            {
                ConnectionId = connection.Id,
                Name = name.Trim().Length > 256 ? name.Trim()[..256] : name.Trim(),
                KeyHash = IngestKeyFormat.Hash(key),
                KeyPrefix = key[..IngestKeyFormat.StoredPrefixLength],
                CreatedAt = time.GetUtcNow(),
            };
            db.IngestKeys.Add(entity);
            await db.SaveChangesAsync(token);
            return new CreatedIngestKey(entity.Id, connection.Id, entity.Name, entity.KeyPrefix, key);
        }, ct);

    public Task<IReadOnlyList<IngestKeyInfo>> ListAsync(CancellationToken ct) =>
        db.InTransactionAsync<IReadOnlyList<IngestKeyInfo>>(async token =>
            (await db.IngestKeys.AsNoTracking().OrderBy(k => k.CreatedAt).ToListAsync(token))
            .Select(k => new IngestKeyInfo(k.Id, k.ConnectionId, k.Name, k.KeyPrefix, k.CreatedAt, k.LastUsedAt, k.RevokedAt)).ToList(), ct);

    /// <summary>Revokes by id or key prefix. Returns false when the organization has no single such key.</summary>
    public Task<bool> RevokeAsync(string idOrPrefix, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            RequireTenant();
            var matches = Guid.TryParse(idOrPrefix, out var id)
                ? await db.IngestKeys.Where(k => k.Id == id).ToListAsync(token)
                : await db.IngestKeys.Where(k => k.KeyPrefix == idOrPrefix).ToListAsync(token);
            if (matches.Count != 1) return false;
            matches[0].RevokedAt ??= time.GetUtcNow();
            await db.SaveChangesAsync(token);
            return true;
        }, ct);

    private void RequireTenant()
    {
        if (tenant.TenantId is null) throw new InvalidOperationException("Ingest keys are managed in a tenant context.");
    }
}
