using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Synect.Application.Adapters;

namespace Synect.Infrastructure.Adapters.Didox;

/// <summary>Adapters:Didox:Token.</summary>
public sealed class DidoxTokenOptions
{
    /// <summary>Lifetime of a Didox user token.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(360);

    /// <summary>A new token is requested this long before the current one expires.</summary>
    public TimeSpan RefreshMargin { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>In-memory cache of Didox user tokens per connection, refreshed ahead of expiry.</summary>
public sealed class DidoxTokenCache(TimeProvider time, IOptions<DidoxTokenOptions> options)
{
    private readonly ConcurrentDictionary<Guid, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();

    public async Task<string> GetTokenAsync(Guid connectionId, Func<CancellationToken, Task<string>> login, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_tokens.TryGetValue(connectionId, out var cached) && now < cached.ExpiresAt - options.Value.RefreshMargin)
            return cached.Token;

        var token = await login(ct);
        _tokens[connectionId] = (token, now + options.Value.TokenLifetime);
        return token;
    }

    public void Invalidate(Guid connectionId) => _tokens.TryRemove(connectionId, out _);

    /// <summary>
    /// Parses a Didox login response. Format (taken from the documentation description, NOT verified on
    /// real responses): { "token": "...", "related_companies": null, "related_branches": null }; when a
    /// person logs into a company, "permissions": { "tin": "...", "roles": [...] } is added.
    /// Anything without a token (including an error object) is an authentication failure.
    /// </summary>
    public static DidoxLoginResult ParseLoginResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("token", out var token)
                && token.ValueKind == JsonValueKind.String
                && token.GetString() is { Length: > 0 } value)
            {
                DidoxPermissions? permissions = null;
                if (root.TryGetProperty("permissions", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    var roles = p.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array
                        ? r.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()).ToList()
                        : [];
                    permissions = new DidoxPermissions(SourceJson.Str(p, "tin"), roles);
                }
                return new DidoxLoginResult(value, permissions);
            }
        }
        catch (JsonException)
        {
        }
        throw new AdapterAuthenticationException("Didox login failed: no token in the response.");
    }
}

/// <summary>Permissions returned when a person logs into a company.</summary>
public sealed record DidoxPermissions(string? Tin, IReadOnlyList<string> Roles);

/// <summary>Parsed Didox login result. The token is a secret: never log it.</summary>
public sealed record DidoxLoginResult(string Token, DidoxPermissions? Permissions)
{
    public override string ToString() => $"DidoxLoginResult {{ Permissions = {Permissions} }}";
}
