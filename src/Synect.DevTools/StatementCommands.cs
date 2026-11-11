using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Queue;
using Synect.Application.Statements;
using Synect.Domain.Banking;
using Synect.Domain.Tenants;
using Synect.Infrastructure.Adapters.Fake;

namespace Synect.DevTools;

/// <summary>
/// create-file-connection, import-statement, list-imports, anonymize-statement and statement-bench. Console output never
/// shows full account numbers; the statement content is never logged.
/// </summary>
public sealed class StatementCommands(IServiceProvider services, TenantScopeRunner tenants, IEnumerable<IStatementParser> parsers, CommandCapture capture, TimeProvider time)
{
    public async Task<int> CreateConnectionAsync(Guid tenant, CliArgs options)
    {
        var bank = options.Get("bank-name") ?? throw new ArgumentException("--bank-name is required");
        var profile = options.Get("profile");
        if (profile is not null) LoadProfile(profile, null); // validate now
        var info = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<FileConnectionService>().CreateAsync(bank, profile, ct),
            CancellationToken.None);
        Console.WriteLine($"{(info.Created ? "Created" : "Exists")}: {info.Name} ({info.Id}), default profile: {info.DefaultProfile ?? "(by format)"}");
        return 0;
    }

    public async Task<int> ImportAsync(Guid tenant, CliArgs options)
    {
        var connectionKey = options.Get("connection") ?? throw new ArgumentException("--connection <name|id> is required");
        var path = options.Get("file") ?? throw new ArgumentException("--file <path> is required");
        var format = Format(options.Get("format") ?? throw new ArgumentException("--format mt940|csv is required"));
        var connection = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<FileConnectionService>().FindAsync(connectionKey, ct),
            CancellationToken.None);
        var profile = LoadProfile(options.Get("profile") ?? connection.DefaultProfile, format);
        var request = new StatementImportRequest(connection.Id, Path.GetFileName(path), format, profile,
            Force: options.Get("force") == "true", CreateAccounts: options.Get("create-accounts") == "true", DryRun: options.Get("dry-run") == "true");

        await using var file = File.OpenRead(path);
        var result = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<BankStatementImportService>().ImportAsync(request, file, ct),
            CancellationToken.None);
        Print(result, profile);
        return result.Status is StatementImportStatus.Imported or StatementImportStatus.DryRun or StatementImportStatus.AlreadyImported ? 0 : 2;
    }

    public async Task<int> ListAsync(Guid tenant, CliArgs options)
    {
        var rows = await tenants.InTenantAsync(tenant, async (sp, ct) =>
        {
            Guid? connection = options.Get("connection") is { } key ? (await sp.GetRequiredService<FileConnectionService>().FindAsync(key, ct)).Id : null;
            return await sp.GetRequiredService<BankStatementImportService>().ListAsync(connection, options.Int("limit", 50), ct);
        }, CancellationToken.None);
        if (rows.Count == 0)
        {
            Console.WriteLine("No statement imports.");
            return 0;
        }
        Console.WriteLine($"{"Imported (UTC)",-17} {"Format",-6} {"Account",-10} {"Period",-23} {"Total",7} {"New",7} {"Dupl.",7} {"Rej.",5} {"Balance",-8} {"Profile",-26} File");
        foreach (var r in rows)
            Console.WriteLine($"{r.ImportedAt.UtcDateTime:yyyy-MM-dd HH:mm} {r.Format,-6} {MaskList(r.Account),-10} {r.PeriodFrom:yyyy-MM-dd} - {r.PeriodTo:yyyy-MM-dd} " +
                              $"{r.TransactionsTotal,7} {r.TransactionsNew,7} {r.TransactionsDuplicate,7} {r.RowsRejected,5} {r.BalanceCheck,-8} {r.Profile,-26} {r.FileName} ({r.FileSha256[..12]})");
        return 0;
    }

    public int Anonymize(CliArgs options)
    {
        var path = options.Get("file") ?? throw new ArgumentException("--file <path> is required");
        var output = options.Get("out") ?? throw new ArgumentException("--out <path> is required");
        var format = Format(options.Get("format") ?? throw new ArgumentException("--format mt940|csv is required"));
        if (Path.GetFullPath(path) == Path.GetFullPath(output)) throw new ArgumentException("--out must differ from --file.");
        var profile = LoadProfile(options.Get("profile"), format);
        var result = StatementAnonymizer.Anonymize(File.ReadAllBytes(path), format, profile, parsers.Single(p => p.Format == format));
        File.WriteAllBytes(output, result.Content);
        Console.WriteLine($"Written {output} ({result.Encoding}): {result.Names} name(s), {result.Inns} INN(s), {result.Accounts} account number(s) replaced. " +
                          "Review the file before sharing it: people's names, phones or addresses inside payment texts are not recognized.");
        return 0;
    }

    /// <summary>
    /// Import time of synthetic MT940 files of the given sizes, each into a new tenant: the first import, the same file again
    /// (refused by SHA-256) and the same operations in a file with another hash (every operation already stored). Per run:
    /// database round trips by kind, database time and time per phase. Every import has a timeout, so nothing hangs.
    /// </summary>
    public async Task BenchAsync(IReadOnlyList<int> sizes, int days, TimeSpan timeout)
    {
        var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
        var rows = new List<string>();
        foreach (var operations in sizes)
        {
            var name = $"Statement bench {stamp} {operations}";
            Guid tenant;
            await using (var scope = services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
                var t = new Tenant { Name = name, Inn = "319999998", CreatedAt = time.GetUtcNow() };
                db.Tenants.Add(t);
                await db.SaveChangesAsync();
                tenant = t.Id;
            }
            var connection = await tenants.InTenantAsync(tenant, (sp, ct) => sp.GetRequiredService<FileConnectionService>().CreateAsync("Bench bank", null, ct),
                CancellationToken.None);
            var today = DateOnly.FromDateTime(time.GetUtcNow().ToOffset(TimeSpan.FromHours(5)).DateTime);
            var d = Math.Min(days, Math.Max(1, operations / 20));
            var bytes = Encoding.UTF8.GetBytes(SyntheticStatementGenerator.Mt940(
                new SyntheticStatementGenerator.Options("20208000900100000001", today.AddDays(-d), d, operations, Seed: 7)));
            Console.WriteLine($"== {operations} operations over {d} days ({bytes.Length / 1024} KB), tenant '{name}'");

            foreach (var (label, content) in new[] { ("first import", bytes), ("same file again", bytes), ("same operations, other file", bytes.Concat(new byte[] { 13, 10 }).ToArray()) })
            {
                capture.TakeAll();
                capture.Enabled = true;
                var sw = Stopwatch.StartNew();
                StatementImportResult? r = null;
                string outcome;
                try
                {
                    using var cts = new CancellationTokenSource(timeout);
                    r = await tenants.InTenantAsync(tenant, async (sp, ct) =>
                    {
                        var res = await sp.GetRequiredService<BankStatementImportService>().ImportAsync(
                            new StatementImportRequest(connection.Id, "bench.sta", StatementFormat.Mt940, StatementProfiles.GenericMt940, CreateAccounts: true),
                            new MemoryStream(content), ct);
                        return res;
                    }, cts.Token);
                    outcome = $"{r.Status} {r.TransactionsNew} new / {r.TransactionsDuplicate} dupl.";
                }
                catch (OperationCanceledException)
                {
                    outcome = $"TIMEOUT after {timeout.TotalSeconds:F0} s (rolled back)";
                }
                sw.Stop();
                capture.Enabled = false;
                var commands = capture.TakeAll();
                var kinds = commands.GroupBy(c => Kind(c.Text)).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
                Console.WriteLine($"  {label,-28} {sw.Elapsed.TotalSeconds,7:F2} s  {outcome}");
                Console.WriteLine($"    db round trips {commands.Count} ({string.Join(", ", kinds)}), db time {commands.Sum(c => c.Duration.TotalSeconds):F2} s");
                if (r?.Timings is { Count: > 0 } timings)
                    Console.WriteLine("    phases: " + string.Join(", ", timings.OrderByDescending(t => t.Value).Select(t => $"{t.Key} {t.Value.TotalSeconds:F2}")));
                rows.Add($"| {operations} | {label} | {sw.Elapsed.TotalSeconds:F2} s | {commands.Count} | {outcome} |");
            }
        }
        Console.WriteLine();
        Console.WriteLine("| Operations | Run | Time | DB round trips | Result |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (var row in rows) Console.WriteLine(row);
    }

    private static string Kind(string sql)
    {
        var text = sql.TrimStart();
        foreach (var k in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "WITH", "START", "COMMIT", "SET", "MERGE" })
            if (text.StartsWith(k, StringComparison.OrdinalIgnoreCase)) return k.ToLowerInvariant();
        return text.Split(' ', 2)[0].ToLowerInvariant();
    }

    private static void Print(StatementImportResult r, StatementProfile profile)
    {
        Console.WriteLine($"{r.Status}: {r.Message}");
        Console.WriteLine($"File sha256 {r.FileSha256[..16]}..., encoding {r.Encoding ?? "-"}, profile {profile.Name}");
        foreach (var s in r.Statements)
        {
            Console.WriteLine($"  Account {BankStatementImportService.Mask(s.AccountNumber)} {s.Currency}{(s.AccountExists ? "" : " (new account)")}, " +
                              $"{s.PeriodFrom:yyyy-MM-dd} - {s.PeriodTo:yyyy-MM-dd}, {s.Operations} operation(s)");
            Console.WriteLine($"    opening {Money(s.OpeningBalance)}, credit {StatementAmounts.Format(s.TotalCredit)}, debit {StatementAmounts.Format(s.TotalDebit)}, " +
                              $"closing {Money(s.ClosingBalance)}, computed {Money(s.ComputedClosing)}: balance check {s.BalanceCheck}");
        }
        if (r.Status != StatementImportStatus.AlreadyImported)
            Console.WriteLine($"Operations: {r.TransactionsTotal} accepted, {r.TransactionsNew} new, {r.TransactionsDuplicate} already stored, {r.RowsRejected} row(s) rejected");
        if (r.Warnings.Count > 0)
        {
            Console.WriteLine($"Warnings ({r.Warnings.Count}):");
            foreach (var w in r.Warnings.Take(30)) Console.WriteLine($"  [{w.Code}] {w}");
            if (r.Warnings.Count > 30) Console.WriteLine($"  ... {r.Warnings.Count - 30} more");
        }
        if (r.Preview is { Count: > 0 } preview)
        {
            Console.WriteLine("First operations (accounts masked):");
            foreach (var o in preview)
                Console.WriteLine($"  line {o.Line,5}: {o.PostingDate:yyyy-MM-dd} {(o.Direction == TransactionDirection.Incoming ? "in " : "out")} " +
                                  $"{StatementAmounts.Format(o.Amount),16}{(o.IsReversal ? " (reversal)" : "")} INN {o.CounterpartyInn ?? "-"} " +
                                  $"acc {(o.CounterpartyAccount is { } a ? BankStatementImportService.Mask(a) : "-")} doc {o.DocumentNumber ?? "-"} " +
                                  $"{Short(o.CounterpartyName, 30)} | {Short(o.Purpose, 60)}");
        }
    }

    private static string Money(long? minor) => minor is { } m ? StatementAmounts.Format(m) : "-";
    private static string Short(string? s, int max) => s is null ? "-" : s.Length <= max ? s : s[..(max - 1)] + "…";
    private static string MaskList(string accounts) => string.Join(",", accounts.Split(',').Select(BankStatementImportService.Mask));

    private static StatementFormat Format(string value) => value.Trim().ToLowerInvariant() switch
    {
        "mt940" => StatementFormat.Mt940,
        "csv" => StatementFormat.Csv,
        _ => throw new ArgumentException("--format must be mt940 or csv"),
    };

    /// <summary>A built-in profile name, a path to a profile JSON file, or (null) the default profile of the format.</summary>
    private static StatementProfile LoadProfile(string? nameOrPath, StatementFormat? format)
    {
        StatementProfile profile;
        if (string.IsNullOrWhiteSpace(nameOrPath))
            profile = StatementProfiles.Default(format ?? StatementFormat.Mt940);
        else if (StatementProfiles.Find(nameOrPath) is { } builtIn)
            profile = builtIn;
        else if (File.Exists(nameOrPath))
            profile = StatementProfile.FromJson(File.ReadAllText(nameOrPath));
        else
            throw new ArgumentException($"Profile '{nameOrPath}' is neither a built-in profile ({string.Join(", ", StatementProfiles.BuiltIn.Select(p => p.Name))}) nor a JSON file.");
        if (format is { } f && profile.Format != f)
            throw new ArgumentException($"Profile '{profile.Name}' is for {profile.Format}, not {f}.");
        return profile;
    }
}
