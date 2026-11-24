using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Adapters;
using Synect.Application.OData;
using Synect.Application.Secrets;

namespace Synect.Infrastructure.Adapters.OData;

/// <summary>
/// The HTTP transport of the OData connector, shared by all connections: redirects are never followed, no proxy, and in
/// production the connection is refused when the host name resolves to an internal address (loopback, private,
/// link-local, ...), checked on every connect (a DNS answer cannot redirect us inside). Tests replace it with an
/// in-process handler.
/// </summary>
public sealed class ODataTransport(HttpMessageHandler handler)
{
    public HttpMessageHandler Handler => handler;

    public static ODataTransport CreateDefault(IOptions<ODataOptions> options) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = async ValueTask<Stream> (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var local = options.Value.LocalDevelopmentAllowed && ODataEndpoint.IsLocalHostName(host.ToLowerInvariant());
                var addresses = IPAddress.TryParse(host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, ct);
                var allowed = addresses.Where(a => local || !ODataEndpoint.IsInternalAddress(a)).ToList();
                if (allowed.Count == 0)
                    throw new HttpRequestException("The OData host resolves only to internal addresses; the connection was refused.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed.ToArray(), context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        });
}

public sealed class ODataHttpClientFactory(ODataTransport transport, IOptions<ODataOptions> options, TimeProvider time, ILoggerFactory loggers)
    : IODataClientFactory
{
    public IODataClient Create(Uri serviceRoot, string login, SecretValue password)
    {
        // Checked again here, so that no caller can reach an address the settings do not allow.
        var root = ODataEndpoint.Normalize(serviceRoot.ToString(), options.Value);
        return new ODataHttpClient(transport.Handler, root, login, password, options.Value, time, loggers.CreateLogger<ODataHttpClient>());
    }
}

/// <summary>
/// Read-only OData client of one application (see <see cref="IODataClient"/>). Every request: GET only, Basic
/// authentication built per request (never logged), at most OData:RequestsPerSecond, its own timeout, retries with a
/// growing pause after a timeout, a network error, 429 (Retry-After respected) or 5xx; 401/403 end at once as an access
/// error. Logs and messages carry the resource name, the HTTP status and timings only: never the URL, the host, the login
/// or the password.
/// </summary>
public sealed class ODataHttpClient : IODataClient
{
    public const long MaxResponseBytes = 64L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _authorization;
    private readonly ODataOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextRequestTicks;

    public ODataClientStats Stats { get; } = new();

    public ODataHttpClient(HttpMessageHandler handler, Uri serviceRoot, string login, SecretValue password, ODataOptions options, TimeProvider time,
        ILogger logger)
    {
        // disposeHandler: false — the shared transport handler (inside the guard) is not ours to dispose.
        _http = new HttpClient(new GetOnlyHandler(handler), disposeHandler: false) { BaseAddress = serviceRoot, Timeout = Timeout.InfiniteTimeSpan };
        _authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes(login + ":" + password.Reveal()));
        _options = options;
        _time = time;
        _logger = logger;
    }

    public async Task<string> GetMetadataAsync(CancellationToken ct)
    {
        var (_, body) = await GetAsync("$metadata", ODataUrl.Metadata, accept: "application/xml", ct);
        return Encoding.UTF8.GetString(body);
    }

    public async Task<IReadOnlyList<string>> ListResourcesAsync(CancellationToken ct)
    {
        var (_, body) = await GetAsync("service document", ODataUrl.ServiceDocument, accept: "application/json", ct);
        using var doc = Parse(body, "service document");
        // TODO(verify-real): the 1C service document in JSON is assumed to be {"odata.metadata": ..., "value": [{"name", "url"}]}
        // (OData v3 JSON light); the older verbose form {"d": {"EntitySets": [...]}} is read too.
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in value.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && (e.TryGetProperty("name", out var n) || e.TryGetProperty("url", out n)) && n.GetString() is { } s)
                    names.Add(s);
        }
        else if (doc.RootElement.TryGetProperty("d", out var d) && d.TryGetProperty("EntitySets", out var sets) && sets.ValueKind == JsonValueKind.Array)
        {
            names.AddRange(sets.EnumerateArray().Select(x => x.GetString()).OfType<string>());
        }
        return names;
    }

    public async Task<long> CountAsync(string resource, string? filter, CancellationToken ct)
    {
        var (_, body) = await GetAsync(resource, ODataUrl.Count(resource, filter), accept: "text/plain", ct);
        var text = Encoding.UTF8.GetString(body).Trim().Trim('﻿');
        // The documented answer is a plain number ("29"). TODO(verify-real): no BOM/XML wrapper on a real application.
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new ODataProtocolException($"OData {resource}/$count did not return a number.");
    }

    public async Task<ODataPage> GetAsync(ODataQuery query, CancellationToken ct)
    {
        var (_, body) = await GetAsync(query.Resource, ODataUrl.Build(query), accept: "application/json", ct);
        using var doc = Parse(body, query.Resource);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ODataProtocolException($"OData {query.Resource}: the answer is not a JSON object.");
        long? count = null;
        if (root.TryGetProperty("odata.count", out var c))
        {
            // Documented as a string ("28"); a number is accepted too. TODO(verify-real): on large result sets as well.
            if (c.ValueKind == JsonValueKind.String && long.TryParse(c.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) count = n;
            else if (c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var m)) count = m;
        }
        if (root.TryGetProperty("value", out var value))
        {
            if (value.ValueKind != JsonValueKind.Array) throw new ODataProtocolException($"OData {query.Resource}: 'value' is not an array.");
            return new ODataPage(value.EnumerateArray().Select(e => e.Clone()).ToList(), count);
        }
        // One record by key: the fields are at the top level.
        return query.Key is not null
            ? new ODataPage([root.Clone()], count)
            : throw new ODataProtocolException($"OData {query.Resource}: the answer has no 'value' array.");
    }

    /// <summary>
    /// The only way a request is sent. Any method other than GET throws <see cref="ODataMethodNotAllowedException"/>
    /// before anything leaves the process (the handler pipeline checks it a second time).
    /// </summary>
    public async Task<(HttpStatusCode Status, byte[] Body)> SendAsync(HttpMethod method, string what, string relativeUrl, string accept, CancellationToken ct)
    {
        if (method != HttpMethod.Get) throw new ODataMethodNotAllowedException(method.Method);
        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.RequestTimeout);
            var watch = Stopwatch.StartNew();
            TimeSpan? retryAfter = null;
            string failure;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authorization);
                request.Headers.Accept.ParseAdd(accept);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                Interlocked.Increment(ref Stats.Requests);
                if (response.IsSuccessStatusCode)
                {
                    var body = await ReadLimitedAsync(response, what, timeout.Token);
                    Record(what, status, watch, attempt);
                    return (response.StatusCode, body);
                }
                Record(what, status, watch, attempt);
                switch (status)
                {
                    case 401 or 403:
                        throw new AdapterAuthenticationException(
                            $"OData access denied (HTTP {status}) for {what}: check the login and password of the OData user and its role (УдаленныйДоступOData).");
                    case >= 300 and < 400:
                        throw new ODataProtocolException($"OData {what}: HTTP {status} redirect refused (redirects are never followed).");
                    case 429:
                        retryAfter = response.Headers.RetryAfter?.Delta
                                     ?? (response.Headers.RetryAfter?.Date is { } date ? date - _time.GetUtcNow() : null);
                        failure = $"OData {what}: HTTP 429 (too many requests)";
                        break;
                    case >= 500:
                        failure = $"OData {what}: HTTP {status}";
                        break;
                    case 404:
                        throw new ODataProtocolException($"OData {what}: HTTP 404 (not published: check the «Состав» tab of the OData settings).");
                    default:
                        throw new ODataProtocolException($"OData {what}: HTTP {status}.");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref Stats.Requests);
                Record(what, 0, watch, attempt);
                failure = $"OData {what}: no answer within {_options.RequestTimeout.TotalSeconds:0} s";
            }
            catch (HttpRequestException ex)
            {
                Interlocked.Increment(ref Stats.Requests);
                Record(what, 0, watch, attempt);
                // Never ex.Message: it may name the host.
                failure = $"OData {what}: network error ({ex.HttpRequestError})";
            }

            if (attempt >= _options.MaxRetries)
                throw failure.Contains("429", StringComparison.Ordinal)
                    ? new AdapterRateLimitException(failure + $" after {attempt + 1} attempts.", retryAfter)
                    : new AdapterTransientException(failure + $" after {attempt + 1} attempts.");
            Interlocked.Increment(ref Stats.Retries);
            var delay = TimeSpan.FromTicks(Math.Min(_options.MaxRetryDelay.Ticks, _options.RetryBaseDelay.Ticks * (1L << Math.Min(attempt, 16))));
            if (retryAfter is { } ra && ra > delay) delay = ra > _options.MaxRetryDelay ? _options.MaxRetryDelay : ra;
            _logger.LogInformation("OData {What}: retry {Attempt} of {MaxRetries} in {DelayMs} ms", what, attempt + 1, _options.MaxRetries, (long)delay.TotalMilliseconds);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, _time, ct);
        }
    }

    private Task<(HttpStatusCode Status, byte[] Body)> GetAsync(string what, string relativeUrl, string accept, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, what, relativeUrl, accept, ct);

    private void Record(string what, int status, Stopwatch watch, int attempt)
    {
        Interlocked.Exchange(ref Stats.LastMilliseconds, watch.ElapsedMilliseconds);
        Interlocked.Add(ref Stats.TotalMilliseconds, watch.ElapsedMilliseconds);
        _logger.LogDebug("OData GET {What}: HTTP {Status} in {ElapsedMs} ms (attempt {Attempt})", what, status, watch.ElapsedMilliseconds, attempt + 1);
    }

    /// <summary>At most RequestsPerSecond requests: each one waits until 1/RPS after the previous start.</summary>
    private async Task ThrottleAsync(CancellationToken ct)
    {
        if (_options.RequestsPerSecond <= 0) return;
        var interval = TimeSpan.FromSeconds(1 / _options.RequestsPerSecond);
        TimeSpan wait;
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow().UtcTicks;
            var start = Math.Max(now, _nextRequestTicks);
            _nextRequestTicks = start + interval.Ticks;
            wait = TimeSpan.FromTicks(start - now);
        }
        finally
        {
            _gate.Release();
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new ODataProtocolException($"OData {what}: the answer is larger than {MaxResponseBytes / 1024 / 1024} MB; use a smaller page.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
                throw new ODataProtocolException($"OData {what}: the answer is larger than {MaxResponseBytes / 1024 / 1024} MB; use a smaller page.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static JsonDocument Parse(byte[] body, string what)
    {
        try
        {
            return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            // Never the parser message: it may quote content.
            throw new ODataProtocolException($"OData {what}: the answer is not valid JSON.");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    /// <summary>Second guard of the read-only rule, in the handler pipeline: a non-GET request never reaches the network.</summary>
    private sealed class GetOnlyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.Method == HttpMethod.Get ? base.SendAsync(request, ct) : throw new ODataMethodNotAllowedException(request.Method.Method);
    }
}
