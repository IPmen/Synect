using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Ingest;

namespace Synect.Api.Ingest;

/// <summary>
/// POST /ingest/1c/v1/batches: the receiver of the 1C extension (stage 8a). Not MCP and not an ASP.NET authentication
/// scheme: the ingest key is checked here, so an MCP key never authenticates on this route and an ingest key never on /mcp.
/// Order: failed-authentication limit per IP (429), key (401, no details), packages per key (429), Content-Type (415),
/// size (413, also after gzip decompression), then <see cref="IngestService"/> in the key's organization.
/// </summary>
public static class IngestEndpoint
{
    public const string Route = "/ingest/1c/v1/batches";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    public static IServiceCollection AddSynectIngest(this IServiceCollection services)
    {
        // The Ingest section itself is bound with the other options (AddSynectInfrastructure).
        services.AddSingleton<IngestHttpLimiter>();
        services.AddSingleton<IPostConfigureOptions<IngestOptions>, IngestTinLengthPolicy>();
        return services;
    }

    public static IEndpointConventionBuilder MapSynectIngest(this IEndpointRouteBuilder endpoints)
    {
        // Resolved at startup so that the warning about Ingest:AllowAnyTinLengthInDevelopment comes before the first package.
        _ = endpoints.ServiceProvider.GetRequiredService<IOptions<IngestOptions>>().Value;
        return endpoints.MapPost(Route, HandleAsync).DisableAntiforgery();
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var limiter = services.GetRequiredService<IngestHttpLimiter>();
        var options = services.GetRequiredService<IOptions<IngestOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Synect.Api.Ingest");
        var ct = context.RequestAborted;
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (limiter.IsBlockedForFailedAuth(ip, out var retry))
        {
            logger.LogWarning("Ingest request refused: too many failed authentications from {ClientIp}", ip);
            await TooManyAsync(context, retry, "too_many_failed_authentications");
            return;
        }

        // 401 without details for a missing, malformed, unknown or revoked key.
        string? header = context.Request.Headers.Authorization;
        var key = header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        IngestKeyIdentity? identity = null;
        if (IngestKeyFormat.LooksLikeKey(key))
            identity = await services.GetRequiredService<IIngestKeyResolver>().ResolveAsync(IngestKeyFormat.Hash(key!), ct);
        if (identity is null)
        {
            limiter.RecordFailedAuth(ip);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }

        if (!limiter.TryAcquireKey(identity.IngestKeyId, out retry))
        {
            await TooManyAsync(context, retry, "rate_limited");
            return;
        }

        var contentType = context.Request.ContentType;
        if (contentType is null || !contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(context, 415, new { status = "rejected", error = "unsupported_media_type", message = "Content-Type must be application/json." });
            return;
        }
        var encoding = context.Request.Headers.ContentEncoding.ToString().Trim();
        var gzip = encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase);
        if (encoding.Length > 0 && !gzip && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(context, 415, new { status = "rejected", error = "unsupported_content_encoding", message = "Content-Encoding must be gzip or absent." });
            return;
        }

        var tooLarge = new { status = "rejected", error = IngestEnvelopeCodes.PayloadTooLarge,
            message = $"The package is larger than {options.MaxBytes} bytes (Ingest:MaxBytes, counted after decompression). Split it into smaller packages." };
        if (context.Request.ContentLength > options.MaxBytes)
        {
            await WriteAsync(context, 413, tooLarge);
            return;
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = options.MaxBytes + 1L;

        byte[]? body;
        try
        {
            body = await ReadLimitedAsync(context.Request.Body, gzip, options.MaxBytes, ct);
        }
        catch (InvalidDataException)
        {
            await WriteAsync(context, 400, new { status = "rejected", error = "invalid_gzip", message = "The body is not valid gzip data." });
            return;
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            body = null;
        }
        if (body is null)
        {
            await WriteAsync(context, 413, tooLarge);
            return;
        }

        // The organization comes from the key, never from the package.
        services.GetRequiredService<TenantContext>().Set(identity.TenantId);
        var response = await services.GetRequiredService<IngestService>().ProcessAsync(identity, body, ct);
        await WriteAsync(context, response.HttpStatus, response.Body);
    }

    /// <summary>Reads at most <paramref name="max"/> bytes (decompressed); null when the body is larger.</summary>
    private static async Task<byte[]?> ReadLimitedAsync(Stream body, bool gzip, int max, CancellationToken ct)
    {
        await using var source = gzip ? new GZipStream(body, CompressionMode.Decompress, leaveOpen: true) : null;
        var input = (Stream?)source ?? body;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static Task TooManyAsync(HttpContext context, TimeSpan retryAfter, string code)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return WriteAsync(context, 429, new { status = "rejected", error = code, retry_after_seconds = seconds });
    }

    private static Task WriteAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(body, body.GetType(), Json, "application/json; charset=utf-8", context.RequestAborted);
    }
}

/// <summary>
/// Limits of the ingest route (fixed windows, in memory, per process): packages per ingest key
/// (Ingest:RateLimit:PackagesPerMinute) and failed authentications per client IP (FailedAuthPerMinutePerIp). Behind a
/// reverse proxy configure forwarded headers first, so that the client IP is real.
/// </summary>
public sealed class IngestHttpLimiter(IOptions<IngestOptions> options, TimeProvider time)
{
    private sealed class Entry
    {
        public DateTimeOffset Start;
        public int Count;
    }

    private readonly ConcurrentDictionary<string, Entry> _failed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Entry> _packages = new(StringComparer.Ordinal);
    private int _operations;

    private IngestRateLimitOptions O => options.Value.RateLimit;
    private TimeSpan Window => O.Window > TimeSpan.Zero ? O.Window : TimeSpan.FromMinutes(1);

    public bool IsBlockedForFailedAuth(string ip, out TimeSpan retryAfter)
    {
        var entry = Current(_failed, ip, out var now);
        lock (entry)
        {
            retryAfter = entry.Start + Window - now;
            return entry.Count >= Math.Max(1, O.FailedAuthPerMinutePerIp);
        }
    }

    public void RecordFailedAuth(string ip)
    {
        var entry = Current(_failed, ip, out _);
        lock (entry) entry.Count++;
    }

    public bool TryAcquireKey(Guid keyId, out TimeSpan retryAfter)
    {
        var entry = Current(_packages, keyId.ToString("N"), out var now);
        lock (entry)
        {
            if (entry.Count >= Math.Max(1, O.PackagesPerMinute))
            {
                retryAfter = entry.Start + Window - now;
                return false;
            }
            entry.Count++;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private Entry Current(ConcurrentDictionary<string, Entry> entries, string key, out DateTimeOffset now)
    {
        now = time.GetUtcNow();
        var at = now;
        var window = Window;
        if (Interlocked.Increment(ref _operations) % 1024 == 0)
            foreach (var (k, e) in entries)
                if (at - e.Start >= window) entries.TryRemove(k, out _);
        var entry = entries.GetOrAdd(key, _ => new Entry { Start = at });
        lock (entry)
        {
            if (now - entry.Start >= window)
            {
                entry.Start = now;
                entry.Count = 0;
            }
        }
        return entry;
    }
}

/// <summary>
/// Ingest:AllowAnyTinLengthInDevelopment takes effect only in the Development environment; anywhere else (Production
/// above all) it is ignored and a warning is logged.
/// </summary>
public sealed class IngestTinLengthPolicy(IHostEnvironment environment, ILogger<IngestTinLengthPolicy> logger) : IPostConfigureOptions<IngestOptions>
{
    public void PostConfigure(string? name, IngestOptions options)
    {
        if (!options.AllowAnyTinLengthInDevelopment) return;
        if (environment.IsDevelopment())
        {
            options.AcceptExtraTinLengths();
            logger.LogWarning("Ingest:AllowAnyTinLengthInDevelopment is on: organization_tin and counterparty_tin also accept 10 and 12 digits (Development only)");
        }
        else
        {
            logger.LogWarning("Ingest:AllowAnyTinLengthInDevelopment is set but ignored in the {Environment} environment: TINs must have 9 or 14 digits",
                environment.EnvironmentName);
        }
    }
}
