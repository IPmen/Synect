using Synect.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// The connection string comes from user-secrets (Development) or ConnectionStrings__Synect.
builder.Services.AddSynectInfrastructure(builder.Configuration);

var app = builder.Build();

// Read endpoints over the Synect database are added in the next stage, together with
// authentication that resolves the tenant (ITenantContext) for each request.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
