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
    public const string ApiKeyId = "synect:api_key_id";
    public const string Scope = "scope";

    public static (Guid TenantId, Guid ApiKeyId)? Identity(ClaimsPrincipal? user) =>
        user?.FindFirst(TenantId)?.Value is { } t && user.FindFirst(ApiKeyId)?.Value is { } k
        && Guid.TryParse(t, out var tenant) && Guid.TryParse(k, out var key)
            ? (tenant, key)
            : null;

    public static bool HasScope(ClaimsPrincipal? user, string scope) =>
        user?.FindAll(Scope).Any(c => c.Value == scope) == true;
}

/// <summary>
/// "Authorization: Bearer &lt;key&gt;" with a Synect client key. The tenant comes from the key, never from tool
/// arguments. Any failure gives 401 with "WWW-Authenticate: Bearer" and no details. The key is hashed at once and
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

        var identity = await resolver.ResolveAsync(ApiKeyFormat.Hash(key), Context.RequestAborted);
        if (identity is null) return AuthenticateResult.Fail("Invalid API key.");

        var claims = new List<Claim>
        {
            new(SynectClaims.TenantId, identity.TenantId.ToString()),
            new(SynectClaims.ApiKeyId, identity.ApiKeyId.ToString()),
            new(ClaimTypes.NameIdentifier, identity.ApiKeyId.ToString()),
        };
        claims.AddRange(identity.Scopes.Select(s => new Claim(SynectClaims.Scope, s)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
