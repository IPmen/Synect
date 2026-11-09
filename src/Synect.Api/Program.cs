using Synect.Infrastructure;
using Synect.Mcp.Hosting;

var builder = WebApplication.CreateBuilder(args);

// The connection string comes from user-secrets (Development) or ConnectionStrings__Synect.
// Infrastructure is referenced here only to register dependencies; MCP tools see Application services only.
builder.Services.AddSynectInfrastructure(builder.Configuration);
builder.Services.AddSynectMcp(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// MCP (Streamable HTTP): POST /mcp with "Authorization: Bearer <client key>".
app.MapSynectMcp();

app.Run();

/// <summary>Entry point type, visible to integration tests (WebApplicationFactory).</summary>
public partial class Program;
