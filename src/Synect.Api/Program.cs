using Synect.Api.Ingest;
using Synect.Infrastructure;
using Synect.Mcp.Hosting;

var builder = WebApplication.CreateBuilder(args);

// The connection string comes from user-secrets (Development) or ConnectionStrings__Synect.
// Infrastructure is referenced here only to register dependencies; MCP tools see Application services only.
builder.Services.AddSynectInfrastructure(builder.Configuration);
builder.Services.AddSynectMcp(builder.Configuration);
// Receiver of the 1C extension (stage 8a): its own ingest keys, never MCP keys.
builder.Services.AddSynectIngest();

var app = builder.Build();

app.UseAuthentication();
// HTTP limits of /mcp (per key, per IP, failed authentications); every request counts, tools/list and initialize too.
app.UseSynectMcpHttpLimits();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// MCP (Streamable HTTP): POST /mcp with "Authorization: Bearer <client key>".
app.MapSynectMcp();

// 1C packages: POST /ingest/1c/v1/batches with "Authorization: Bearer <ingest key>".
app.MapSynectIngest();

app.Run();

/// <summary>Entry point type, visible to integration tests (WebApplicationFactory).</summary>
public partial class Program;
