using System.Reflection;
using Microsoft.Extensions.Options;
using Synect.Infrastructure.Persistence;

namespace Synect.Api.Hosting;

/// <summary>Hosting:* settings of the Api.</summary>
public sealed class ReadinessOptions
{
    /// <summary>How long a readiness answer is reused (protects the database from a flood of /ready calls).</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Timeout of the database check behind /ready.</summary>
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Readiness of the Api: the database answers and the schema is compatible with this build (every migration this
/// build knows is applied). During a graceful stop the answer is 503 at once, so the reverse proxy and the deploy
/// script stop treating the process as healthy while in-flight requests are drained.
/// </summary>
public sealed class ReadinessProbe(IServiceScopeFactory scopes, IOptions<ReadinessOptions> options, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset At, SchemaStatus Status)? _cached;
    private volatile bool _stopping;

    public bool Stopping => _stopping;

    public void MarkStopping() => _stopping = true;

    public async Task<SchemaStatus> SchemaAsync(CancellationToken ct, bool useCache = true)
    {
        var duration = options.Value.CacheDuration;
        if (useCache && _cached is { } c && time.GetUtcNow() - c.At < duration) return c.Status;
        await _gate.WaitAsync(ct);
        try
        {
            if (useCache && _cached is { } again && time.GetUtcNow() - again.At < duration) return again.Status;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Value.CheckTimeout);
            SchemaStatus status;
            await using (var scope = scopes.CreateAsyncScope())
            {
                try { status = await scope.ServiceProvider.GetRequiredService<SchemaCompatibility>().CheckAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    status = new SchemaStatus(false, false, null, null, [], [], "Timeout");
                }
            }
            _cached = (time.GetUtcNow(), status);
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }
}

public static class ReadinessEndpoints
{
    public static IServiceCollection AddSynectReadiness(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ReadinessOptions>(configuration.GetSection("Hosting:Readiness"));
        services.AddSingleton<ReadinessProbe>();
        return services;
    }

    /// <summary>
    /// /ready: 200 {"status":"ready"} or 503 {"status":"not_ready","reason":"stopping|database_unavailable|schema_incompatible"};
    /// no migration names or errors (it is reachable from outside). /version: build and schema details (local only:
    /// the reverse proxy does not publish it).
    /// </summary>
    public static WebApplication MapSynectReadiness(this WebApplication app)
    {
        var probe = app.Services.GetRequiredService<ReadinessProbe>();
        app.Lifetime.ApplicationStopping.Register(probe.MarkStopping);

        app.MapGet("/ready", async (ReadinessProbe p, CancellationToken ct) =>
        {
            if (p.Stopping) return NotReady("stopping");
            var schema = await p.SchemaAsync(ct);
            if (p.Stopping) return NotReady("stopping");
            if (!schema.DatabaseAvailable) return NotReady("database_unavailable");
            if (!schema.Compatible) return NotReady("schema_incompatible");
            return Results.Ok(new { status = "ready" });
        });

        app.MapGet("/version", async (ReadinessProbe p, CancellationToken ct) =>
        {
            var schema = await p.SchemaAsync(ct, useCache: false);
            return Results.Ok(new
            {
                version = BuildInfo.Version,
                commit = BuildInfo.Commit,
                schema = new
                {
                    expected = schema.ExpectedLatest,
                    applied = schema.AppliedLatest,
                    compatible = schema.Compatible,
                    database_available = schema.DatabaseAvailable,
                    pending = schema.Pending,
                    unknown = schema.Unknown,
                },
            });
        });
        return app;
    }

    private static IResult NotReady(string reason) =>
        Results.Json(new { status = "not_ready", reason }, statusCode: StatusCodes.Status503ServiceUnavailable);
}

/// <summary>Build version: InformationalVersion is "&lt;version&gt;+&lt;git commit&gt;" (SourceRevisionId, set by the SDK or by deploy/build.sh).</summary>
public static class BuildInfo
{
    private static readonly string Informational =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string Version => Informational.Split('+')[0];
    public static string? Commit => Informational.Split('+') is { Length: > 1 } parts ? parts[1] : null;
}
