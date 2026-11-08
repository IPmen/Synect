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
    /// Reads the token from a Didox login response. ASSUMPTION: { "token": "..." }; anything else
    /// (including an error object) is an authentication failure.
    /// </summary>
    public static string ParseLoginResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("token", out var token)
                && token.ValueKind == JsonValueKind.String
                && token.GetString() is { Length: > 0 } value)
                return value;
        }
        catch (JsonException)
        {
        }
        throw new AdapterAuthenticationException("Didox login failed: no token in the response.");
    }
}
