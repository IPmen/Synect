using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using Synect.Domain.Access;
using Synect.Infrastructure.Accounts;

namespace Synect.DevTools;

/// <summary>
/// account-bench --account X [--runs 20] [--url http://localhost:5299/mcp]: p50 / p95 of the portfolio tools over a
/// RUNNING Synect.Api (real HTTP, MCP client, authentication, filter, journal). Creates two temporary keys of the account
/// (all organizations; one organization only), never prints them, and revokes them at the end. Start the Api with tool
/// limits raised for the benchmark (Mcp__RateLimit__RequestsPerMinutePerKey, Mcp__HttpRateLimit__RequestsPerMinutePerKey).
/// </summary>
public static class AccountBench
{
    public static async Task<int> RunAsync(IConfiguration configuration, CliArgs o)
    {
        var owner = configuration.GetConnectionString("SynectOwner") ?? throw new InvalidOperationException("ConnectionStrings:SynectOwner is not set.");
        var runs = o.Int("runs", 20);
        var url = new Uri(o.Get("url") ?? "http://localhost:5299/mcp");
        var admin = AccountAdmin.Create(owner);
        await using var _ = admin.Db;
        var account = await admin.FindAccountAsync(o.Get("account") ?? throw new ArgumentException("--account is required"));
        var orgs = (await admin.OrganizationsAsync(account.Id)).Where(x => x.DisabledAt is null && x.Tin is not null).ToList();
        if (orgs.Count == 0) throw new InvalidOperationException("The account has no enabled organizations with a TIN.");
        var first = orgs[0];

        var all = await admin.CreateAccountKeyAsync(account.Id, [ApiKey.ScopeRead], "account-bench (temporary, all)");
        var one = await admin.CreateAccountKeyAsync(account.Id, [ApiKey.ScopeRead], "account-bench (temporary, one)", [first.Tin!]);
        try
        {
            await using var clientAll = await ClientAsync(url, all.Key.Key);
            await using var clientOne = await ClientAsync(url, one.Key.Key);
            var half = string.Join(",", orgs.Take(25).Select(x => x.Tin));
            var cases = new (string Name, McpClient Client, string Tool, Dictionary<string, object?> Args)[]
            {
                ("list_organizations", clientAll, "list_organizations", new()),
                ($"get_portfolio_overview ({Math.Min(25, orgs.Count)} organizations)", clientAll, "get_portfolio_overview", new() { ["organizations"] = half, ["limit"] = 25 }),
                ($"get_portfolio_overview ({orgs.Count} organizations)", clientAll, "get_portfolio_overview", new() { ["limit"] = 100 }),
                ("get_connections_overview", clientAll, "get_connections_overview", new() { ["limit"] = 200 }),
                ("get_attention_items organization=all", clientAll, "get_attention_items", new() { ["organization"] = "all" }),
                ("get_account_balances organization=<TIN>", clientAll, "get_account_balances", new() { ["organization"] = first.Tin }),
                ("get_account_balances without organization (one-org key)", clientOne, "get_account_balances", new()),
            };
            Console.WriteLine($"Account {account.Name}: {orgs.Count} organizations, {runs} runs per tool (after 2 warm-up calls), {url}");
            Console.WriteLine($"{"Tool",-58} {"p50 ms",8} {"p95 ms",8} {"max ms",8}  status");
            foreach (var c in cases)
            {
                for (var i = 0; i < 2; i++) await c.Client.CallToolAsync(c.Tool, c.Args);
                var times = new List<double>();
                string status = "ok";
                for (var i = 0; i < runs; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var result = await c.Client.CallToolAsync(c.Tool, c.Args);
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    if (result.IsError == true)
                        status = result.StructuredContent is { } body && JsonSerializer.SerializeToElement(body).TryGetProperty("error", out var e)
                            ? "error " + e.GetProperty("code").GetString()
                            : "error";
                }
                times.Sort();
                Console.WriteLine($"{c.Name,-58} {Percentile(times, 0.50),8:F0} {Percentile(times, 0.95),8:F0} {times[^1],8:F0}  {status}");
            }
        }
        finally
        {
            await admin.RevokeAccountKeyAsync(account.Id, all.Key.Id.ToString());
            await admin.RevokeAccountKeyAsync(account.Id, one.Key.Id.ToString());
            Console.WriteLine("Temporary keys revoked.");
        }
        return 0;
    }

    private static Task<McpClient> ClientAsync(Uri url, string key)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = url,
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
        });
        return McpClient.CreateAsync(transport);
    }

    /// <summary>Nearest-rank percentile of sorted values.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    public static string Ms(double value) => value.ToString("F0", CultureInfo.InvariantCulture);
}
