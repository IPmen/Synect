using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synect.Application.Abstractions;
using Synect.Application.Queue;

namespace Synect.DevTools;

/// <summary>Records the SQL commands EF executes (text, parameters, duration) while enabled.</summary>
public sealed class CommandCapture : DbCommandInterceptor
{
    public sealed record Captured(string Text, IReadOnlyList<(string Name, object? Value)> Parameters, TimeSpan Duration);

    private readonly List<Captured> _commands = [];
    public bool Enabled { get; set; }

    public IReadOnlyList<Captured> TakeAll()
    {
        lock (_commands)
        {
            var copy = _commands.ToList();
            _commands.Clear();
            return copy;
        }
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Record(command, eventData.Duration);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Record(command, eventData.Duration);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command, TimeSpan duration)
    {
        if (!Enabled) return;
        var parameters = command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, (object?)p.Value)).ToList();
        lock (_commands) _commands.Add(new Captured(command.CommandText, parameters, duration));
    }
}

/// <summary>Timing (p50/p95) and query plans of the analytics methods. Measurements only, never assertions.</summary>
public sealed class Bench(IServiceProvider services, CommandCapture capture)
{
    private static readonly Dictionary<string, double> Targets = new() { ["list"] = 0.3, ["aggregate"] = 1.0, ["heavy"] = 3.0 };

    public async Task RunAsync(Guid tenant, int runs, string? only)
    {
        var catalog = services.GetRequiredService<QueryCatalog>();
        var args = CliArgs.Parse([]);
        Console.WriteLine($"| Method | Question | Category | p50, s | p95, s | Target p95, s | OK |");
        Console.WriteLine($"|---|---|---|---:|---:|---:|:-:|");
        foreach (var query in catalog.All.Where(q => only is null || q.Name == only))
        {
            await catalog.RunAsync(query.Name, tenant, args, CancellationToken.None); // warm-up (plans, connections)
            var times = new List<double>();
            for (var i = 0; i < runs; i++)
            {
                var sw = Stopwatch.StartNew();
                await catalog.RunAsync(query.Name, tenant, args, CancellationToken.None);
                times.Add(sw.Elapsed.TotalSeconds);
            }
            times.Sort();
            var p50 = Percentile(times, 0.50);
            var p95 = Percentile(times, 0.95);
            var target = Targets[query.Category];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {query.Name} | {query.Question} | {query.Category} | {p50:F3} | {p95:F3} | {target:F1} | {(p95 <= target ? "yes" : "NO")} |"));
        }
    }

    /// <summary>Runs each method once, then EXPLAIN (ANALYZE, BUFFERS) on its slowest SQL statement, under the tenant (RLS on).</summary>
    public async Task ExplainAsync(Guid tenant, string? only)
    {
        var catalog = services.GetRequiredService<QueryCatalog>();
        var tenants = services.GetRequiredService<TenantScopeRunner>();
        foreach (var query in catalog.All.Where(q => only is null || q.Name == only))
        {
            await catalog.RunAsync(query.Name, tenant, CliArgs.Parse([]), CancellationToken.None);
            capture.TakeAll();
            capture.Enabled = true;
            await catalog.RunAsync(query.Name, tenant, CliArgs.Parse([]), CancellationToken.None);
            capture.Enabled = false;
            var slowest = capture.TakeAll().Where(c => c.Text.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase)
                    || c.Text.TrimStart().StartsWith("with", StringComparison.OrdinalIgnoreCase)
                    || c.Text.Contains("FROM", StringComparison.Ordinal))
                .Where(c => !c.Text.Contains("set_config", StringComparison.Ordinal))
                .MaxBy(c => c.Duration);
            if (slowest is null) continue;

            var plan = await tenants.InTenantAsync(tenant, async (sp, ct) =>
            {
                var db = sp.GetRequiredService<ISynectDbContext>();
                return await db.InTransactionAsync(async t =>
                {
                    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
                    await using var cmd = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS) " + slowest.Text, connection,
                        (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction());
                    foreach (var (name, value) in slowest.Parameters) cmd.Parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
                    var lines = new List<string>();
                    await using var reader = await cmd.ExecuteReaderAsync(t);
                    while (await reader.ReadAsync(t)) lines.Add(reader.GetString(0));
                    return lines;
                }, ct);
            }, CancellationToken.None);

            Console.WriteLine($"===== {query.Name} ({query.Question}), statement {slowest.Duration.TotalMilliseconds:F0} ms =====");
            foreach (var line in plan) Console.WriteLine(line);
            Console.WriteLine();
        }
    }

    private static double Percentile(List<double> sorted, double p)
    {
        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
