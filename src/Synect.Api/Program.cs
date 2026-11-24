using Microsoft.AspNetCore.HttpOverrides;
using Synect.Api.Hosting;
using Synect.Api.Ingest;
using Synect.Infrastructure;
using Synect.Mcp.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Under systemd: journald log format (no colours, priorities) and Type=notify; does nothing elsewhere.
builder.Host.UseSystemd();
// Graceful stop: in-flight requests get up to Hosting:ShutdownTimeout (60 s; systemd TimeoutStopSec is longer).
builder.Services.Configure<HostOptions>(o =>
    o.ShutdownTimeout = builder.Configuration.GetValue("Hosting:ShutdownTimeout", TimeSpan.FromSeconds(60)));

// The connection string comes from user-secrets (Development) or ConnectionStrings__Synect (environment / systemd
// EnvironmentFile in Production). Migrations are never applied here: see `Synect.DevTools migrate`.
// Infrastructure is referenced here only to register dependencies; MCP tools see Application services only.
builder.Services.AddSynectInfrastructure(builder.Configuration);
builder.Services.AddODataEnvironment(builder.Environment.IsEnvironment("Development"));
builder.Services.AddSynectMcp(builder.Configuration);
// Receiver of the 1C extension (stage 8a): its own ingest keys, never MCP keys.
builder.Services.AddSynectIngest();
builder.Services.AddSynectReadiness(builder.Configuration);

var app = builder.Build();

// Behind the local reverse proxy (Caddy on 127.0.0.1): the real client IP for the per-IP limits. Only loopback
// proxies are trusted (the ASP.NET Core default), so a client cannot spoof X-Forwarded-For.
if (app.Configuration.GetValue<bool>("Hosting:ForwardedHeaders"))
    app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });

app.UseAuthentication();
// HTTP limits of /mcp (per key, per IP, failed authentications); every request counts, tools/list and initialize too.
app.UseSynectMcpHttpLimits();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
// /ready (database and schema, 503 while stopping) and /version (build and schema).
app.MapSynectReadiness();

// MCP (Streamable HTTP): POST /mcp with "Authorization: Bearer <client key>".
app.MapSynectMcp();

// 1C packages: POST /ingest/1c/v1/batches with "Authorization: Bearer <ingest key>".
app.MapSynectIngest();

app.Run();

/// <summary>Entry point type, visible to integration tests (WebApplicationFactory).</summary>
public partial class Program;
