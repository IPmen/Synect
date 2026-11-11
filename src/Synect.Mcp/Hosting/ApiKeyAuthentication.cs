using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Access;

namespace Synect.Mcp.Hosting;

public static class SynectClaims
{
    public const string TenantId = "synect:tenant_id";
    public const string AccountId = "synect:account_id";
    public const string ApiKeyId = "synect:api_key_id";
    public const string Scope = "scope";

    /// <summary>The caller of the request, or null when it is not authenticated with a Synect key.</summary>
    public static CallerIdentity? Identity(ClaimsPrincipal? user)
    {
        if (user?.FindFirst(ApiKeyId)?.Value is not { } k || !Guid.TryParse(k, out var key)) return null;
        Guid? tenant = user.FindFirst(TenantId)?.Value is { } t && Guid.TryParse(t, out var tv) ? tv : null;
        Guid? account = user.FindFirst(AccountId)?.Value is { } a && Guid.TryParse(a, out var av) ? av : null;
        if ((tenant is null) == (account is null)) return null;
        var hash = user.Identities.OfType<ApiKeyClaimsIdentity>().FirstOrDefault()?.KeyHash;
        return hash is null ? null : new CallerIdentity(key, account, tenant, hash);
    }

    public static bool HasScope(ClaimsPrincipal? user, string scope) =>
        user?.FindAll(Scope).Any(c => c.Value == scope) == true;
}

/// <summary>The authenticated key: a single-organization key (TenantId) or an account key (AccountId).</summary>
/// <param name="KeyHash">SHA-256 of the presented key, kept in memory for the SECURITY DEFINER functions that take it;
/// never a claim, never logged.</param>
public sealed record CallerIdentity(Guid ApiKeyId, Guid? AccountId, Guid? TenantId, byte[] KeyHash)
{
    /// <summary>Never prints the hash.</summary>
    public override string ToString() => $"ApiKey {ApiKeyId}";
}

/// <summary>
/// Claims identity of a Synect key that also carries the key hash as a plain property (not a claim, so it never appears
/// in claim lists or logs). The organization list of an account key is read with it, per call.
/// </summary>
public sealed class ApiKeyClaimsIdentity : ClaimsIdentity
{
    public ApiKeyClaimsIdentity(IEnumerable<Claim> claims, string authenticationType, byte[] keyHash) : base(claims, authenticationType) =>
        KeyHash = keyHash;

    private ApiKeyClaimsIdentity(ApiKeyClaimsIdentity other) : base(other) => KeyHash = other.KeyHash;

    public byte[] KeyHash { get; }

    public override ClaimsIdentity Clone() => new ApiKeyClaimsIdentity(this);
}

/// <summary>
/// "Authorization: Bearer &lt;key&gt;" with a Synect client key. The organizations come from the key (one organization,
/// or the organizations of an account the key may see), never from the arguments alone: a tool's 'organization'
/// argument only chooses among them (ToolCallFilter). Any failure gives 401 with "WWW-Authenticate: Bearer" and no details. The key is hashed at once and
/// never logged; failure messages do not contain it.
/// This is one ASP.NET Core authentication scheme; OAuth can be added later as another scheme on the same endpoint.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyResolver resolver)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SynectApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header)) return AuthenticateResult.NoResult();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.Fail("Unsupported authorization scheme.");

        var key = header["Bearer ".Length..].Trim();
        if (!ApiKeyFormat.LooksLikeKey(key)) return AuthenticateResult.Fail("Invalid API key.");

        var hash = ApiKeyFormat.Hash(key);
        var identity = await resolver.ResolveAsync(hash, Context.RequestAborted);
        if (identity is null) return AuthenticateResult.Fail("Invalid API key.");

        var claims = new List<Claim>
        {
            new(SynectClaims.ApiKeyId, identity.ApiKeyId.ToString()),
            new(ClaimTypes.NameIdentifier, identity.ApiKeyId.ToString()),
        };
        if (identity.TenantId is { } tenant) claims.Add(new Claim(SynectClaims.TenantId, tenant.ToString()));
        if (identity.AccountId is { } account) claims.Add(new Claim(SynectClaims.AccountId, account.ToString()));
        claims.AddRange(identity.Scopes.Select(s => new Claim(SynectClaims.Scope, s)));
        var principal = new ClaimsPrincipal(new ApiKeyClaimsIdentity(claims, SchemeName, hash));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
