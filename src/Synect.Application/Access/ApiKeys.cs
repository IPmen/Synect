using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Synect.Application.Abstractions;
using Synect.Domain.Access;

namespace Synect.Application.Access;

/// <summary>The identity behind a presented API key, as found by <see cref="IApiKeyResolver"/>.</summary>
public sealed record ApiKeyIdentity(Guid ApiKeyId, Guid TenantId, IReadOnlyList<string> Scopes);

/// <summary>
/// Finds a non-revoked key by the SHA-256 of the presented key, before any tenant context exists.
/// The PostgreSQL implementation calls the SECURITY DEFINER function synect_resolve_api_key, so the
/// application role never reads api_keys outside a tenant context.
/// </summary>
public interface IApiKeyResolver
{
    Task<ApiKeyIdentity?> ResolveAsync(byte[] keyHash, CancellationToken ct);
}

/// <summary>Key format and hashing. Keys carry 256 bits of randomness.</summary>
public static class ApiKeyFormat
{
    public const string Prefix = "sk_synect_";
    public const int RandomBytes = 32;

    /// <summary>Characters of the key kept in key_prefix: "sk_synect_" plus 6 random characters.</summary>
    public const int StoredPrefixLength = 16;

    private const string Base64UrlAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    public static string Generate() => Prefix + Base64Url(RandomNumberGenerator.GetBytes(RandomBytes));

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Cheap shape check before hashing: anything else cannot be a key we issued.</summary>
    public static bool LooksLikeKey(string? value) =>
        value is { Length: > 16 and <= 128 } && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value.AsSpan(Prefix.Length).IndexOfAnyExcept(Base64UrlAlphabet) < 0;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The key is present only here, right after creation; it is never stored.</summary>
public sealed record CreatedApiKey(Guid Id, string Name, string KeyPrefix, IReadOnlyList<string> Scopes, string Key)
{
    /// <summary>Never prints the key (records print every property by default).</summary>
    public override string ToString() => $"ApiKey {KeyPrefix}… ({Name})";
}

public sealed record ApiKeyInfo(Guid Id, string Name, string KeyPrefix, IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <summary>Creates, lists and revokes keys of the current tenant (tenant context required).</summary>
public sealed class ApiKeyService(ISynectDbContext db, ITenantContext tenant, TimeProvider time)
{
    public Task<CreatedApiKey> CreateAsync(string name, IReadOnlyCollection<string> scopes, CancellationToken ct)
    {
        if (tenant.TenantId is null) throw new InvalidOperationException("API keys are created in a tenant context.");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Key name is required.", nameof(name));
        if (scopes.Count == 0) throw new ArgumentException("At least one scope is required.", nameof(scopes));
        var unknown = scopes.Except(ApiKey.KnownScopes).ToList();
        if (unknown.Count > 0)
            throw new ArgumentException($"Unknown scopes: {string.Join(", ", unknown)}. Known: {string.Join(", ", ApiKey.KnownScopes)}.");

        var key = ApiKeyFormat.Generate();
        var entity = new ApiKey
        {
            Name = name.Trim(),
            KeyHash = ApiKeyFormat.Hash(key),
            KeyPrefix = key[..ApiKeyFormat.StoredPrefixLength],
            Scopes = scopes.Distinct().Order(StringComparer.Ordinal).ToArray(),
            CreatedAt = time.GetUtcNow(),
        };
        return db.InTransactionAsync(async token =>
        {
            db.ApiKeys.Add(entity);
            await db.SaveChangesAsync(token);
            return new CreatedApiKey(entity.Id, entity.Name, entity.KeyPrefix, entity.Scopes, key);
        }, ct);
    }

    public Task<IReadOnlyList<ApiKeyInfo>> ListAsync(CancellationToken ct) =>
        db.InTransactionAsync<IReadOnlyList<ApiKeyInfo>>(async token =>
            (await db.ApiKeys.AsNoTracking().OrderBy(k => k.CreatedAt).ToListAsync(token))
            .Select(k => new ApiKeyInfo(k.Id, k.Name, k.KeyPrefix, k.Scopes, k.CreatedAt, k.LastUsedAt, k.RevokedAt)).ToList(), ct);

    /// <summary>Revokes by id or by key prefix. Returns false when the tenant has no such key.</summary>
    public Task<bool> RevokeAsync(string idOrPrefix, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var matches = Guid.TryParse(idOrPrefix, out var id)
                ? await db.ApiKeys.Where(k => k.Id == id).ToListAsync(token)
                : await db.ApiKeys.Where(k => k.KeyPrefix == idOrPrefix).ToListAsync(token);
            if (matches.Count != 1) return false;
            matches[0].RevokedAt ??= time.GetUtcNow();
            await db.SaveChangesAsync(token);
            return true;
        }, ct);
}
