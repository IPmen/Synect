using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Adapters;
using Synect.Domain.Edo;
using Synect.Domain.Sync;

namespace Synect.Infrastructure.Adapters.Didox;

/// <summary>
/// Adapters:Didox:Api. The Didox partner API is NOT described by public documentation available to us: the host names,
/// paths and header names below come from an unofficial community client (github.com/mirzaevoff/didox) and must be
/// checked against the documentation Didox gives to partners before the first real load. They are settings, so a
/// correction needs no code change. The partner token is a secret: set it with user-secrets, never in appsettings.
/// </summary>
public sealed class DidoxApiOptions
{
    /// <summary>Production host (stage: https://stage.goodsign.biz). Unconfirmed.</summary>
    public string BaseUrl { get; set; } = "https://api-partners.didox.uz";

    /// <summary>Partner token issued by Didox to Synect (Adapters:Didox:Api:PartnerToken, user-secrets only).</summary>
    public string? PartnerToken { get; set; }

    /// <summary>Header carrying the partner token. Unconfirmed.</summary>
    public string PartnerTokenHeader { get; set; } = "Partner-Authorization";

    /// <summary>Header carrying the user token returned by the login. Unconfirmed.</summary>
    public string UserTokenHeader { get; set; } = "user-key";

    /// <summary>Login of a legal entity: {tin} and {locale} are replaced; body {"password": "..."}. Unconfirmed.</summary>
    public string LoginPath { get; set; } = "/v1/auth/{tin}/password/{locale}";

    /// <summary>ru or uz.</summary>
    public string Locale { get; set; } = "ru";

    /// <summary>Document list with query owner (0 incoming, 1 outgoing), page (from 1), limit, dateFromUpdated (yyyy-MM-dd). Unconfirmed.</summary>
    public string ListPath { get; set; } = "/v2/documents";

    /// <summary>One document: {id} is replaced. Unconfirmed.</summary>
    public string DocumentPath { get; set; } = "/v1/documents/{id}";

    /// <summary>Documents per page (the community client allows 1..100).</summary>
    public int PageSize { get; set; } = 100;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Sends the HTTP requests of <see cref="DidoxEdoAdapter"/>; replaced by a stub in tests (no real calls).</summary>
public sealed class DidoxHttp(HttpClient client)
{
    public HttpClient Client => client;

    /// <summary>The production transport: pooled connections recycled every 5 minutes (DNS changes), no logging of URLs.</summary>
    public static DidoxHttp CreateDefault() =>
        new(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan });
}

/// <summary>
/// The real Didox adapter (source code "didox"). One organization logs in with its TIN and password (the connection's
/// credentials: login = TIN); the user token is cached for its lifetime by <see cref="DidoxTokenCache"/> and requested
/// again once after an HTTP 401. Documents are mapped by the existing <see cref="DidoxMapper"/>. Errors: 401/403 and a
/// login without a token are authentication failures (the connection needs attention); 429 is a rate limit (Retry-After
/// respected); 5xx, timeouts and network errors are transient (retried by the call policy); a missing partner token is a
/// configuration error. Messages carry the operation and HTTP status only, never a token, password, TIN or body.
/// </summary>
public sealed class DidoxEdoAdapter(
    DidoxHttp http,
    DidoxMapper mapper,
    DidoxTokenCache tokens,
    IOptions<DidoxApiOptions> options,
    ILogger<DidoxEdoAdapter> logger) : IEdoAdapter
{
    public const string Code = "didox";
    public const string Provider = "Didox";

    private DidoxApiOptions O => options.Value;

    public string SourceCode => Code;
    public string ProviderName => Provider;
    public AdapterKind Kind => AdapterKind.Edo;

    /// <summary>The list filter is by date (dateFromUpdated), so the sync overlap (Sync:Edo:UpdatedSinceOverlap) must be at least a day.</summary>
    public EdoAdapterCapabilities Capabilities => new(SupportsUpdatedSince: true, PageSize: Math.Clamp(O.PageSize, 1, 100));

    public async Task<ConnectionTestResult> TestConnectionAsync(AdapterContext context, CancellationToken ct)
    {
        try
        {
            await SendAsync(context, "TestConnection", () => ListUri(EdoDocumentOwner.Incoming, null, 1, 1), ct);
            return ConnectionTestResult.Ok();
        }
        catch (AdapterException ex) when (ex is not AdapterTransientException)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
    }

    public async Task<EdoDocumentPage> ListDocumentsAsync(
        AdapterContext context, EdoDocumentOwner owner, DateTimeOffset? updatedSince, int page, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        var size = Capabilities.PageSize;
        var json = await SendAsync(context, "ListDocuments", () => ListUri(owner, updatedSince, page, size), ct);
        DidoxDocumentList list;
        try
        {
            list = mapper.MapDocumentList(json!);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            throw new AdapterException($"Didox ListDocuments: the response could not be read ({ex.GetType().Name}).");
        }
        var hasMore = list.Total is { } total ? page * size < total : list.Items.Count == size;
        return new EdoDocumentPage(list.Items, page, hasMore);
    }

    public async Task<EdoDocumentInfo?> GetDocumentAsync(AdapterContext context, string externalId, CancellationToken ct)
    {
        var json = await SendAsync(context, "GetDocument", () => Path(O.DocumentPath.Replace("{id}", Uri.EscapeDataString(externalId), StringComparison.Ordinal)), ct,
            notFoundIsNull: true);
        if (json is null) return null;
        try
        {
            return mapper.MapSingleDocument(json);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            throw new AdapterException($"Didox GetDocument: the response could not be read ({ex.GetType().Name}).");
        }
    }

    private Uri ListUri(EdoDocumentOwner owner, DateTimeOffset? updatedSince, int page, int limit)
    {
        var query = new StringBuilder()
            .Append("owner=").Append(owner == EdoDocumentOwner.Outgoing ? "1" : "0")
            .Append("&page=").Append(page.ToString(CultureInfo.InvariantCulture))
            .Append("&limit=").Append(limit.ToString(CultureInfo.InvariantCulture));
        // Didox filters by date only (local Tashkent date); the sync re-reads a day of overlap.
        if (updatedSince is { } since)
            query.Append("&dateFromUpdated=").Append(DateOnly.FromDateTime(since.ToOffset(TimeSpan.FromHours(5)).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return new Uri(Path(O.ListPath) + "?" + query, UriKind.Absolute);
    }

    private Uri Path(string path) => new(O.BaseUrl.TrimEnd('/') + "/" + path.TrimStart('/'), UriKind.Absolute);

    /// <summary>GET with the partner and user tokens; after an HTTP 401 the token is requested again once.</summary>
    private async Task<string?> SendAsync(AdapterContext context, string operation, Func<Uri> uri, CancellationToken ct, bool notFoundIsNull = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            var token = await TokenAsync(context, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri());
            request.Headers.TryAddWithoutValidation(O.UserTokenHeader, token);
            using var response = await SendRawAsync(request, operation, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
            {
                // The token expired earlier than expected (or was revoked): log in again once.
                tokens.Invalidate(context.ConnectionId);
                logger.LogInformation("Didox {Operation} for connection {ConnectionId}: HTTP 401, logging in again", operation, context.ConnectionId);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsNull) return null;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                tokens.Invalidate(context.ConnectionId);
                throw new AdapterAuthenticationException($"Didox {operation}: HTTP {(int)response.StatusCode}, the access was refused.");
            }
            ThrowOnError(response, operation);
            return await response.Content.ReadAsStringAsync(ct);
        }
    }

    private Task<string> TokenAsync(AdapterContext context, CancellationToken ct) =>
        tokens.GetTokenAsync(context.ConnectionId, async token =>
        {
            var credentials = LoginPasswordCredentials.Parse(await context.Credentials.GetCredentialsAsync(context, token));
            var path = O.LoginPath.Replace("{tin}", Uri.EscapeDataString(credentials.Login.Trim()), StringComparison.Ordinal)
                .Replace("{locale}", Uri.EscapeDataString(O.Locale), StringComparison.Ordinal);
            using var request = new HttpRequestMessage(HttpMethod.Post, Path(path))
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string> { ["password"] = credentials.Password.Reveal() }),
                    Encoding.UTF8, "application/json"),
            };
            using var response = await SendRawAsync(request, "Login", token);
            // 401, 403 and 422 (wrong TIN or password, per the community client) mean the client's access is wrong.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity)
                throw new AdapterAuthenticationException($"Didox Login: HTTP {(int)response.StatusCode}, the TIN or password was not accepted.");
            ThrowOnError(response, "Login");
            return DidoxTokenCache.ParseLoginResponse(await response.Content.ReadAsStringAsync(token)).Token;
        }, ct);

    /// <summary>Adds the partner token and sends with the timeout; network failures and timeouts become transient errors.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, string operation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(O.PartnerToken))
            throw new AdapterException("Didox partner token is not configured (Adapters:Didox:Api:PartnerToken in user-secrets); no request was sent.");
        request.Headers.TryAddWithoutValidation(O.PartnerTokenHeader, O.PartnerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(O.Timeout);
        try
        {
            return await http.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AdapterTransientException($"Didox {operation}: no answer within {O.Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            // The inner message may name the host; the request URI (with the TIN in the login path) is never included.
            throw new AdapterTransientException($"Didox {operation}: network error ({ex.HttpRequestError}).");
        }
    }

    private static void ThrowOnError(HttpResponseMessage response, string operation)
    {
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new AdapterRateLimitException($"Didox {operation}: HTTP 429, too many requests.", RetryAfter(response));
        if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            throw new AdapterTransientException($"Didox {operation}: HTTP {status}.");
        throw new AdapterException($"Didox {operation}: HTTP {status}.");
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter is { } r
            ? r.Delta ?? (r.Date is { } at ? at - DateTimeOffset.UtcNow : null)
            : null;
}
