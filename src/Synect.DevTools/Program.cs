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
//   rebuild-counterparty-aliases --tenant <name|id>|--all   refills counterparty_aliases from loaded data
//   create-api-key <tenant> [--scopes read,sync_request] [--name ...]   prints the new key ONCE
//   revoke-api-key <tenant> <key id|key prefix>
//   list-api-keys <tenant>                        id, prefix, scopes, created/last used/revoked (never the key)
//   test-db-leftovers [--drop]                    temporary databases/roles left by PostgreSQL tests
//   reconcile --tenant <name|id> [--inn 301000001,302000002]   recalculates the reconciliation (full, or these INNs)
//   rebuild-reconciliation --tenant <name|id>     drops Proposed matches and findings, recalculates everything
//   reconcile-eval                                golden set: precision, recall and errors of automatic matches
//   reconcile-bench --tenant <name|id> [--runs 5] time of a full and of single-INN recalculations
//   normalize-currency --tenant <name|id>|--all   numeric ISO 4217 codes (860, 840, 978, 643) in loaded data -> letters
//   attention --tenant <name|id>                  evaluates every attention rule now and prints the open items
//   attention-eval                                attention golden set (fixed clock): created items, severities, what is not created
//   attention-bench --tenant <name|id> [--runs 5] time of a full evaluation and of the evaluations after a load
//   create-file-connection --tenant <name|id> --bank-name <bank> [--profile <name|path.json>]   file import connection
//   import-statement --tenant <name|id> --connection <name|id> --file <path> --format mt940|csv [--profile <name|path.json>]
//                    [--force] [--create-accounts] [--dry-run]   imports a bank statement file (dry run: checks only)
//   list-imports --tenant <name|id> [--connection <name|id>]   imported statement files
//   anonymize-statement --file <path> --format mt940|csv [--profile <name|path.json>] --out <path>   pseudonymizes names, INNs, accounts
//   statement-bench [--sizes 500,2000,20000] [--days 250] [--timeout 120]   import time of synthetic MT940 files (new tenant per size)
//   list-tenants                                  name, id, TIN and account of every tenant
//   delete-tenant --name <name> --confirm <name again> [--id <id> when names repeat] [--force]   deletes a tenant and all its data (owner, one transaction)
// Accounts (stage 6.5; administration runs as the owner, ConnectionStrings:SynectOwner):
//   create-account --name <name>
//   add-organization --account <name|id> --name <name> --tin <9|14 digits> [--create-tenant] [--display-name ...]
//   list-accounts [--account <name|id>]             accounts, or one account's organizations and keys
//   disable-organization --account <name|id> --organization <tin|name|id>   (data is kept)
//   create-account-key --account <name|id> [--organizations <tin or name list>] [--scopes read,review] [--name ...]   prints the key ONCE
//   revoke-api-key --account <name|id> --key <key id|key prefix>   (revoke-api-key <tenant> <key> for single-organization keys)
//   set-onboarding --tenant <name|id> --source <connection name|id|source code> --status NotStarted|WaitingForAccess|Connected|NeedsAttention [--note ...]
//   import-organizations --account <name|id> --file <csv with columns name,tin>   idempotent, errors per row
//   seed-account --name <name> [--organizations 50]   account with N organizations loaded through the real services
//   account-bench --account <name|id> [--runs 20] [--url http://localhost:5299/mcp]   p50/p95 of the portfolio tools (running Api)
// 1C ingest (stage 8a):
//   create-ingest-key --tenant <name|id> [--name ...]   creates the 1C connection if needed, prints the ingest key ONCE
//   revoke-ingest-key --tenant <name|id> --key <key id|key prefix>
//   list-ingest-keys --tenant <name|id>             id, prefix, connection, created/last used/revoked (never the key)
//   list-ingest-batches --tenant <name|id> [--limit 50]   received 1C packages (journal, counters only)
//   send-1c-sample --tenant <name|id> --scenario basic|messy|large [--url http://localhost:5299] [--key ik_...]
//                  fake 1C extension over HTTP against a running Api (without --key a temporary key is created and revoked)

var command = args.FirstOrDefault() ?? "help";
var options = CliArgs.Parse(args.Skip(1).ToArray());

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development", Args = [] });
builder.Configuration.AddUserSecrets<CliArgs>(optional: true);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// attention-eval runs on a fixed, movable clock so that its output never depends on the day it is run.
if (command == "attention-eval") builder.Services.AddSingleton<TimeProvider>(new DevClock(Synect.Infrastructure.Adapters.Fake.AttentionGoldenSet.EvaluateAt));

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
builder.Services.AddSingleton<ReconciliationCommands>();
builder.Services.AddSingleton<AttentionCommands>();
builder.Services.AddSingleton<StatementCommands>();
builder.Services.AddSingleton<AccountCommands>();
builder.Services.AddSingleton<IngestCommands>();

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
        case "rebuild-counterparty-aliases":
            foreach (var tenantId in options.Get("all") == "true" ? await AllTenants() : [await Tenant()])
                Console.WriteLine($"{tenantId}: {await services.GetRequiredService<Seeder>().RebuildAliasesAsync(tenantId)} name spellings");
            break;
        case "list-tenants":
            return await services.GetRequiredService<AccountCommands>().ListTenantsAsync();
        case "delete-tenant":
            return await services.GetRequiredService<AccountCommands>().DeleteTenantAsync(options);
        case "create-account":
            return await services.GetRequiredService<AccountCommands>().CreateAccountAsync(options);
        case "add-organization":
            return await services.GetRequiredService<AccountCommands>().AddOrganizationAsync(options);
        case "list-accounts":
            return await services.GetRequiredService<AccountCommands>().ListAccountsAsync(options);
        case "disable-organization":
            return await services.GetRequiredService<AccountCommands>().DisableOrganizationAsync(options);
        case "create-account-key":
            return await services.GetRequiredService<AccountCommands>().CreateAccountKeyAsync(options);
        case "set-onboarding":
            return await services.GetRequiredService<AccountCommands>().SetOnboardingAsync(await Tenant(), options);
        case "import-organizations":
            return await services.GetRequiredService<AccountCommands>().ImportOrganizationsAsync(options);
        case "seed-account":
            return await services.GetRequiredService<AccountCommands>().SeedAccountAsync(options);
        case "account-bench":
            return await AccountBench.RunAsync(builder.Configuration, options);
        case "revoke-api-key" when options.Get("account") is not null:
            return await services.GetRequiredService<AccountCommands>().RevokeAccountKeyAsync(options, Positional(1));
        case "create-api-key":
            return await ApiKeyCommands.CreateAsync(services, await Tenant(), options);
        case "revoke-api-key":
            return await ApiKeyCommands.RevokeAsync(services, await Tenant(), Positional(2) ?? throw new ArgumentException("revoke-api-key <tenant> <key id|key prefix>"));
        case "list-api-keys":
            return await ApiKeyCommands.ListAsync(services, await Tenant());
        case "reconcile":
            await services.GetRequiredService<ReconciliationCommands>().ReconcileAsync(await Tenant(), options.Get("inn"));
            break;
        case "rebuild-reconciliation":
            await services.GetRequiredService<ReconciliationCommands>().RebuildAsync(await Tenant());
            break;
        case "reconcile-eval":
            return await services.GetRequiredService<ReconciliationCommands>().EvalAsync();
        case "reconcile-bench":
            await services.GetRequiredService<ReconciliationCommands>().BenchAsync(await Tenant(), options.Int("runs", 5));
            break;
        case "normalize-currency":
            await services.GetRequiredService<ReconciliationCommands>().NormalizeCurrencyAsync(options.Get("all") == "true" ? await AllTenants() : [await Tenant()]);
            break;
        case "attention":
            await services.GetRequiredService<AttentionCommands>().EvaluateAsync(await Tenant());
            break;
        case "attention-eval":
            return await services.GetRequiredService<AttentionCommands>().EvalAsync();
        case "attention-bench":
            await services.GetRequiredService<AttentionCommands>().BenchAsync(await Tenant(), options.Int("runs", 5));
            break;
        case "create-file-connection":
            return await services.GetRequiredService<StatementCommands>().CreateConnectionAsync(await Tenant(), options);
        case "import-statement":
            return await services.GetRequiredService<StatementCommands>().ImportAsync(await Tenant(), options);
        case "list-imports":
            return await services.GetRequiredService<StatementCommands>().ListAsync(await Tenant(), options);
        case "anonymize-statement":
            return services.GetRequiredService<StatementCommands>().Anonymize(options);
        case "statement-bench":
            await services.GetRequiredService<StatementCommands>().BenchAsync(
                (options.Get("sizes") ?? options.Get("operations") ?? "20000").Split(',').Select(x => int.Parse(x.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToList(),
                options.Int("days", 250), TimeSpan.FromSeconds(options.Int("timeout", 120)));
            break;
        case "create-ingest-key":
            return await services.GetRequiredService<IngestCommands>().CreateKeyAsync(await Tenant(), options);
        case "revoke-ingest-key":
            return await services.GetRequiredService<IngestCommands>().RevokeKeyAsync(await Tenant(), options.Get("key"));
        case "list-ingest-keys":
            return await services.GetRequiredService<IngestCommands>().ListKeysAsync(await Tenant());
        case "list-ingest-batches":
            return await services.GetRequiredService<IngestCommands>().ListBatchesAsync(await Tenant(), options);
        case "send-1c-sample":
            return await services.GetRequiredService<IngestCommands>().SendSampleAsync(await Tenant(), options);
        case "test-db-leftovers":
            return await TestDbLeftovers.RunAsync(builder.Configuration, options.Get("drop") == "true");
        default:
            Console.WriteLine("Commands: seed-demo, seed-load, rebuild-totals, rebuild-counterparty-aliases, list-queries, query <name>, bench, explain, " +
                              "create-api-key, revoke-api-key, list-api-keys, test-db-leftovers, reconcile, rebuild-reconciliation, reconcile-eval, " +
                              "reconcile-bench, normalize-currency, attention, attention-eval, attention-bench, create-file-connection, " +
                              "import-statement, list-imports, anonymize-statement, statement-bench, delete-tenant, create-account, add-organization, " +
                              "list-accounts, disable-organization, create-account-key, set-onboarding, import-organizations, seed-account, account-bench, create-ingest-key, revoke-ingest-key, list-ingest-keys, list-ingest-batches, send-1c-sample. See Program.cs.");
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}
return 0;

// Positional arguments come right after the command, before any --option.
string? Positional(int index) => args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ElementAtOrDefault(index - 1);

async Task<List<Guid>> AllTenants()
{
    await using var scope = services.CreateAsyncScope();
    return await scope.ServiceProvider.GetRequiredService<SynectDbContext>().Tenants.OrderBy(t => t.Name).Select(t => t.Id).ToListAsync();
}

async Task<Guid> Tenant()
{
    var key = options.Get("tenant") ?? (command.EndsWith("-api-key") || command == "list-api-keys" ? Positional(1) : null)
        ?? throw new ArgumentException("--tenant <name|id> is required");
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<SynectDbContext>();
    if (Guid.TryParse(key, out var id) && await db.Tenants.AnyAsync(t => t.Id == id)) return id;
    return await db.Tenants.Where(t => t.Name == key).Select(t => (Guid?)t.Id).SingleOrDefaultAsync()
        ?? throw new ArgumentException($"Tenant '{key}' not found.");
}
