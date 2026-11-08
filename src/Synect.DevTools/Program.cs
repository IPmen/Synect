using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Synect.Application.Resilience;
using Synect.DevTools;
using Synect.Infrastructure;
using Synect.Infrastructure.Persistence;

// Synect developer tools (local database only; connection strings come from user-secrets).
//   seed-demo [--tenants 3]                       demo clients loaded through the real sync services and fake adapters
//   seed-load [--operations 200000] [--documents 30000]   load-test client (about 3 years of data)
//   rebuild-totals --tenant <name|id>             full rebuild of analytics_counterparty_monthly
//   query <name> --tenant <name|id> [--from yyyy-MM-dd --to yyyy-MM-dd --inn ... --date ... --limit ...]
//   list-queries
//   bench --tenant <name|id> [--runs 20]          p50/p95 of every analytics method
//   explain --tenant <name|id> [--query <name>]   EXPLAIN (ANALYZE, BUFFERS) of the SQL each method runs

var command = args.FirstOrDefault() ?? "help";
var options = CliArgs.Parse(args.Skip(1).ToArray());

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development", Args = [] });
builder.Configuration.AddUserSecrets<CliArgs>(optional: true);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var capture = new CommandCapture();
builder.Services.AddSynectInfrastructure(builder.Configuration);
builder.Services.ConfigureDbContext<SynectDbContext>(o => o.AddInterceptors(capture));
builder.Services.AddSingleton(capture);
// Loading demo data must not be throttled like production calls to real sources.
builder.Services.ConfigureAll<RateLimitOptions>(o =>
{
    o.PerConnection = new TokenBucketSettings { TokenLimit = 1_000_000, TokensPerPeriod = 1_000_000 };
    o.PerSource = new TokenBucketSettings { TokenLimit = 1_000_000, TokensPerPeriod = 1_000_000 };
});
builder.Services.AddSingleton<Seeder>();
builder.Services.AddSingleton<QueryCatalog>();

using var host = builder.Build();
var services = host.Services;
var json = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // console output only
};

try
{
    switch (command)
    {
        case "seed-demo":
            await services.GetRequiredService<Seeder>().SeedDemoAsync(options.Int("tenants", 3));
            break;
        case "seed-load":
            await services.GetRequiredService<Seeder>().SeedLoadAsync(options.Int("operations", 200_000), options.Int("documents", 30_000));
            break;
        case "rebuild-totals":
            Console.WriteLine($"Rows changed: {await services.GetRequiredService<Seeder>().RebuildTotalsAsync(await Tenant())}");
            break;
        case "list-queries":
            foreach (var q in services.GetRequiredService<QueryCatalog>().All)
                Console.WriteLine($"{q.Name,-34} {q.Category,-10} {q.Question}");
            break;
        case "query":
            var name = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? throw new ArgumentException("query <name> is required");
            var result = await services.GetRequiredService<QueryCatalog>().RunAsync(name, await Tenant(), options, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(result, result.GetType(), json));
            break;
        case "bench":
            await new Bench(services, capture).RunAsync(await Tenant(), options.Int("runs", 20), options.Get("query"));
            break;
        case "explain":
            await new Bench(services, capture).ExplainAsync(await Tenant(), options.Get("query"));
            break;
        default:
            Console.WriteLine("Commands: seed-demo, seed-load, rebuild-totals, list-queries, query <name>, bench, explain. See Program.cs.");
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}
return 0;

async Task<Guid> Tenant()
{
    var key = options.Get("tenant") ?? throw new ArgumentException("--tenant <name|id> is required");
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<SynectDbContext>();
    if (Guid.TryParse(key, out var id) && await db.Tenants.AnyAsync(t => t.Id == id)) return id;
    return await db.Tenants.Where(t => t.Name == key).Select(t => (Guid?)t.Id).SingleOrDefaultAsync()
        ?? throw new ArgumentException($"Tenant '{key}' not found.");
}
