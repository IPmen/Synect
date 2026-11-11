using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Ingest;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Infrastructure.Persistence;

namespace Synect.DevTools;

/// <summary>
/// Ingest keys and the 1C receiver (stage 8a): create-ingest-key, revoke-ingest-key, list-ingest-keys, list-ingest-batches,
/// send-1c-sample. Keys are printed once and never stored; send-1c-sample sends synthetic data to a running Synect.Api.
/// </summary>
public sealed class IngestCommands(IServiceProvider services)
{
    private async Task<T> InTenantAsync<T>(Guid tenantId, Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenantId);
        return await work(scope.ServiceProvider);
    }

    public async Task<int> CreateKeyAsync(Guid tenantId, CliArgs options)
    {
        var name = options.Get("name") ?? $"1C extension {DateTime.UtcNow:yyyy-MM-dd HH:mm}";
        var created = await InTenantAsync(tenantId, sp => sp.GetRequiredService<IngestKeyService>().CreateAsync(name, CancellationToken.None));
        Console.WriteLine($"Ingest key created for tenant {tenantId} (1C connection {created.ConnectionId})");
        Console.WriteLine($"  id:     {created.Id}");
        Console.WriteLine($"  name:   {created.Name}");
        Console.WriteLine($"  prefix: {created.KeyPrefix}");
        Console.WriteLine();
        Console.WriteLine("Key (shown only now; Synect stores only its SHA-256 hash). It works only on POST /ingest/1c/v1/batches, never on /mcp:");
        Console.WriteLine(created.Key);
        return 0;
    }

    public async Task<int> RevokeKeyAsync(Guid tenantId, string? idOrPrefix)
    {
        if (string.IsNullOrWhiteSpace(idOrPrefix)) throw new ArgumentException("--key <key id|key prefix> is required");
        var revoked = await InTenantAsync(tenantId, sp => sp.GetRequiredService<IngestKeyService>().RevokeAsync(idOrPrefix, CancellationToken.None));
        Console.WriteLine(revoked ? $"Ingest key {idOrPrefix} revoked." : $"No single ingest key '{idOrPrefix}' found for tenant {tenantId}.");
        return revoked ? 0 : 1;
    }

    public async Task<int> ListKeysAsync(Guid tenantId)
    {
        var keys = await InTenantAsync(tenantId, sp => sp.GetRequiredService<IngestKeyService>().ListAsync(CancellationToken.None));
        foreach (var k in keys)
            Console.WriteLine($"{k.Id}  {k.KeyPrefix}…  {k.Name}  connection {k.ConnectionId}  created {k.CreatedAt:u}" +
                              $"  last used {(k.LastUsedAt is { } u ? u.ToString("u") : "never")}{(k.RevokedAt is { } r ? $"  REVOKED {r:u}" : "")}");
        if (keys.Count == 0) Console.WriteLine("No ingest keys.");
        return 0;
    }

    public async Task<int> ListBatchesAsync(Guid tenantId, CliArgs options)
    {
        var limit = options.Int("limit", 50);
        var rows = await InTenantAsync(tenantId, async sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            return await db.InTransactionAsync(ct => db.IngestBatches.AsNoTracking().OrderByDescending(b => b.ReceivedAt).Take(limit).ToListAsync(ct), CancellationToken.None);
        });
        Console.WriteLine($"{"received (UTC)",-20} {"entity",-22} {"mode",-6} {"batch_id#seq",-44} {"status",-20} {"total",6} {"acc",6} {"unch",6} {"rej",6} {"del",5}  error");
        foreach (var b in rows)
            Console.WriteLine($"{b.ReceivedAt:yyyy-MM-dd HH:mm:ss} {b.Entity,-22} {b.Mode,-6} {Cut(b.BatchId + "#" + b.Sequence, 44),-44} {b.Status,-20} " +
                              $"{b.ItemsTotal,6} {b.Accepted,6} {b.Unchanged,6} {b.Rejected,6} {b.MarkedDeleted,5}  {Cut(b.Error ?? "", 80)}" +
                              (b.Attempts > 1 ? $" (received {b.Attempts} times)" : ""));
        if (rows.Count == 0) Console.WriteLine("No packages received.");
        return 0;
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>
    /// send-1c-sample --tenant X --scenario basic|messy|large [--url http://localhost:5299] [--key ik_...]. Without --key a
    /// temporary ingest key is created (never printed) and revoked at the end.
    /// </summary>
    public async Task<int> SendSampleAsync(Guid tenantId, CliArgs options)
    {
        var scenario = (options.Get("scenario") ?? "basic").ToLowerInvariant();
        if (scenario is not ("basic" or "messy" or "large")) throw new ArgumentException("--scenario must be basic, messy or large");
        var url = options.Get("url") ?? "http://localhost:5299";
        string? tin;
        await using (var scope = services.CreateAsyncScope())
            tin = await scope.ServiceProvider.GetRequiredService<SynectDbContext>().Tenants.Where(t => t.Id == tenantId).Select(t => t.Tin).SingleAsync();
        if (string.IsNullOrEmpty(tin)) throw new InvalidOperationException("The tenant has no TIN; set one first (add-organization --tin ...).");

        var key = options.Get("key");
        CreatedIngestKey? temporary = null;
        if (key is null)
        {
            temporary = await InTenantAsync(tenantId, sp => sp.GetRequiredService<IngestKeyService>().CreateAsync("send-1c-sample (temporary)", CancellationToken.None));
            key = temporary.Key;
        }
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(2) };
            var scenarios = new OneCSampleScenarios(new OneCSampleClient(http, key, tin));
            Console.WriteLine($"send-1c-sample {scenario} -> {url}{OneCSampleClient.Route} (tenant {tenantId}, TIN {tin})");
            if (scenario == "large")
            {
                var r = await scenarios.LargeAsync(options.Int("records", 20_000), options.Int("package", 500));
                Console.WriteLine($"| Step | Records | Packages | Time, s | Result |");
                Console.WriteLine($"|---|---|---|---|---|");
                Console.WriteLine($"| First receipt | {r.Records} | {r.Packages} | {S(r.First)} | accepted {r.Accepted}, rejected {r.Rejected} |");
                Console.WriteLine($"| Same packages again (same batch_id) | {r.Records} | {r.Packages} | {S(r.Duplicates)} | duplicate answers {r.DuplicateAnswers} |");
                Console.WriteLine($"| Same records, new batch_id | {r.Records} | {r.Packages} | {S(r.Unchanged)} | unchanged {r.UnchangedItems} |");
                return r.Rejected == 0 && r.Accepted == r.Records && r.UnchangedItems == r.Records ? 0 : 1;
            }
            if (scenario == "basic") await scenarios.BasicAsync();
            else await scenarios.MessyAsync();
            foreach (var s in scenarios.Steps)
            {
                Console.WriteLine($"[{(s.Ok ? "OK  " : "FAIL")}] {s.Name} ({s.Elapsed.TotalMilliseconds:0} ms)");
                Console.WriteLine($"       expected: {s.Expected}");
                Console.WriteLine($"       actual:   {s.Actual}");
            }
            var failed = scenarios.Steps.Count(s => !s.Ok);
            Console.WriteLine(failed == 0 ? $"All {scenarios.Steps.Count} steps as expected." : $"{failed} of {scenarios.Steps.Count} steps differ from the expectation.");
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            if (temporary is not null)
                await InTenantAsync(tenantId, sp => sp.GetRequiredService<IngestKeyService>().RevokeAsync(temporary.Id.ToString(), CancellationToken.None));
        }
    }

    private static string S(TimeSpan t) => t.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
}
