using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Synect.Application.Abstractions;
using Synect.Application.Attention;
using Synect.Application.Freshness;
using Synect.Application.Queue;
using Synect.Application.Statements;
using Synect.Domain.Attention;
using Synect.Domain.Banking;
using Synect.Domain.Reconciliation;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Attention;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;
using static Synect.Application.Attention.AttentionRuleCodes;

namespace Synect.Tests.Statements;

internal static class StatementKit
{
    public static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    public static async Task<(Guid Tenant, Guid Connection)> FileTenantAsync(this TestHost host, string name)
    {
        var tenant = await host.CreateTenantAsync(name);
        var connection = await host.InTenantAsync(tenant, sp => sp.GetRequiredService<FileConnectionService>().CreateAsync("Test bank", null, CancellationToken.None));
        return (tenant, connection.Id);
    }

    public static Task<StatementImportResult> ImportAsync(this TestHost host, Guid tenant, Guid connection, byte[] content, StatementFormat format,
        StatementProfile? profile = null, bool force = false, bool create = true, bool dryRun = false, string file = "statement") =>
        host.InTenantAsync(tenant, sp => sp.GetRequiredService<BankStatementImportService>().ImportAsync(
            new StatementImportRequest(connection, file, format, profile ?? StatementProfiles.Default(format), force, create, dryRun),
            new MemoryStream(content), CancellationToken.None));

    public static Task<StatementImportResult> ImportFixtureAsync(this TestHost host, Guid tenant, Guid connection, string name,
        StatementProfile? profile = null, bool force = false, bool create = true, bool dryRun = false) =>
        host.ImportAsync(tenant, connection, StatementFiles.Read(name), name.EndsWith(".csv") ? StatementFormat.Csv : StatementFormat.Mt940,
            profile, force, create, dryRun, name);

    public static byte[] Mt940(string text) => Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));

    public static async Task<(int Ops, int Accounts, int Balances, int Raw, int Runs, int Imports, int Counterparties, int Aliases)> CountsAsync(this TestHost host, Guid tenant) => (
        await host.CountAsync(tenant, db => db.BankTransactions),
        await host.CountAsync(tenant, db => db.BankAccounts),
        await host.CountAsync(tenant, db => db.BankDailyBalances),
        await host.CountAsync(tenant, db => db.RawObjects),
        await host.CountAsync(tenant, db => db.SyncRuns),
        await host.CountAsync(tenant, db => db.StatementImports),
        await host.CountAsync(tenant, db => db.Counterparties),
        await host.CountAsync(tenant, db => db.CounterpartyAliases));
}

/// <summary>Statement file imports end to end on PostgreSQL (row-level security, the real pipeline and attention rules).</summary>
public class StatementImportTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private TestHost Host => fixture.Host;

    private void Start()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        Host.Time.Set(StatementKit.Now);
    }

    [Fact]
    public async Task Mt940_import_writes_operations_day_balances_counterparties_and_a_sync_run()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Import single");
        var r = await Host.ImportFixtureAsync(tenant, connection, "mt940_single_utf8.sta");

        Assert.Equal(StatementImportStatus.Imported, r.Status);
        Assert.Equal((3, 3, 0, 0, BalanceCheckResult.Passed), (r.TransactionsTotal, r.TransactionsNew, r.TransactionsDuplicate, r.RowsRejected, r.BalanceCheck));
        var ops = await Host.QueryAsync(tenant, db => db.BankTransactions.AsNoTracking().OrderBy(t => t.PostingDate).ThenByDescending(t => t.Amount).ToListAsync());
        Assert.Equal(3, ops.Count);
        Assert.All(ops, t =>
        {
            Assert.Equal(OperationIdSource.FileHash, t.IdSource);
            Assert.StartsWith("fh:", t.OperationId);
            Assert.True(t.IsFinal);
            Assert.Equal("UZS", t.Currency);
            using var details = JsonDocument.Parse(t.Details!);
            Assert.True(details.RootElement.GetProperty("weak_operation_id").GetBoolean());
        });
        Assert.Equal(("301234567", "145", "20208000100200300401"), (ops[0].CounterpartyInn, ops[0].DocumentNumber, ops[0].CounterpartyAccount));

        var days = await Host.QueryAsync(tenant, db => db.BankDailyBalances.AsNoTracking().OrderBy(b => b.Date).ToListAsync());
        Assert.Equal([(new DateOnly(2026, 11, 2), 10_000_000_00L, 11_249_999_50L, 250_000_50L, 1_500_000_00L, true),
                      (new DateOnly(2026, 11, 3), 11_249_999_50L, 10_249_999_50L, 1_000_000_00L, 0L, true)],
            days.Select(d => (d.Date, d.OpeningBalance, d.ClosingBalance, d.TotalDebit, d.TotalCredit, d.IsDayClosed)));

        var account = await Host.QueryAsync(tenant, db => db.BankAccounts.AsNoTracking().SingleAsync());
        Assert.Equal(("20208000900100001001", "UZS", connection), (account.AccountNumber, account.Currency, account.ConnectionId));
        Assert.Equal(["301234567", "302345678"], await Host.QueryAsync(tenant, db => db.Counterparties.OrderBy(c => c.Inn).Select(c => c.Inn).ToListAsync()));
        Assert.True(await Host.CountAsync(tenant, db => db.CounterpartyMonthly) >= 2);
        Assert.Equal(1 + 3 + 2, await Host.CountAsync(tenant, db => db.RawObjects)); // account, operations, days

        var run = await Host.LastRunAsync(tenant, connection);
        Assert.Equal((SyncRunResult.Succeeded, true, 3, (DateOnly?)new DateOnly(2026, 11, 2), (DateOnly?)new DateOnly(2026, 11, 3)),
            (run.Result, run.IsComplete, run.RecordCount, run.RangeFrom, run.RangeTo));
        var import = await Host.QueryAsync(tenant, db => db.StatementImports.AsNoTracking().SingleAsync());
        Assert.Equal((r.ImportId, run.Id, "mt940_single_utf8.sta", StatementFormat.Mt940, "generic_mt940", "20208000900100001001", BalanceCheckResult.Passed),
            ((Guid?)import.Id, import.SyncRunId, import.FileName, import.Format, import.Profile, import.Account, import.BalanceCheck));
        Assert.Equal(r.FileSha256, import.FileSha256);
        Assert.Null(import.ImportedByKey);

        // Freshness of the file source = time of the last import.
        var freshness = await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<ISourceFreshnessQuery>().GetAsync(tenant, CancellationToken.None));
        var source = Assert.Single(freshness);
        Assert.Equal((AdapterKind.File, Connection.FileImportSourceCode, (DateTimeOffset?)StatementKit.Now, (bool?)true, (SyncRunResult?)SyncRunResult.Succeeded),
            (source.Kind, source.SourceCode, source.LastSuccessfulSyncAt, source.LastSuccessfulRunIsComplete, source.LastRunResult));
        // The file content is not logged.
        Assert.DoesNotContain(Host.Logs.Lines, l => l.Contains("Ромашка") || l.Contains("20208000100200300401") || l.Contains("301234567"));
    }

    [Fact]
    public async Task Same_file_is_refused_by_sha256_and_the_same_operations_in_another_file_are_not_duplicated()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Import twice");
        var first = await Host.ImportFixtureAsync(tenant, connection, "mt940_single_utf8.sta");
        var before = await Host.CountsAsync(tenant);
        var stamps = await Host.QueryAsync(tenant, db => db.BankTransactions.AsNoTracking().OrderBy(t => t.Id).Select(t => new { t.Id, t.UpdatedAt }).ToListAsync());

        Host.Time.Advance(TimeSpan.FromHours(1));
        var again = await Host.ImportFixtureAsync(tenant, connection, "mt940_single_utf8.sta");
        Assert.Equal(StatementImportStatus.AlreadyImported, again.Status);
        Assert.Contains("already imported", again.Message);
        Assert.Equal(first.ImportId, again.Existing!.Id);
        Assert.Equal(before, await Host.CountsAsync(tenant)); // not even a sync run

        // Same operations, different bytes (another export of the same period): every operation is a duplicate.
        var copy = StatementFiles.Read("mt940_single_utf8.sta").Concat("\r\n"u8.ToArray()).ToArray();
        var other = await Host.ImportAsync(tenant, connection, copy, StatementFormat.Mt940, file: "copy.sta");
        Assert.Equal(StatementImportStatus.Imported, other.Status);
        Assert.Equal((3, 0, 3), (other.TransactionsTotal, other.TransactionsNew, other.TransactionsDuplicate));
        var after = await Host.CountsAsync(tenant);
        Assert.Equal((before.Ops, before.Balances, before.Raw), (after.Ops, after.Balances, after.Raw));
        Assert.Equal(before.Imports + 1, after.Imports);
        Assert.Equal(stamps.Select(s => (s.Id, s.UpdatedAt)),
            (await Host.QueryAsync(tenant, db => db.BankTransactions.AsNoTracking().OrderBy(t => t.Id).Select(t => new { t.Id, t.UpdatedAt }).ToListAsync()))
            .Select(s => (s.Id, s.UpdatedAt))); // nothing rewritten
    }

    [Fact]
    public async Task Overlapping_periods_keep_one_row_per_operation_and_identical_payments_stay_separate()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Import overlap");
        const string day3 = """
            :61:2611031103D100,00NTRFNONREF
            :86:Оплата за воду ИНН 301234567
            :61:2611031103D100,00NTRFNONREF
            :86:Оплата за воду ИНН 301234567
            :61:2611031103C40,00NTRFNONREF
            :86:Возврат ИНН 302345678
            """;
        var a = StatementKit.Mt940($"""
            :20:A
            :25:20208000900100012012
            :60F:C261102UZS1000,00
            :61:2611021102C500,00NTRFNONREF
            :86:Поступление ИНН 303333333 № 1
            {day3}
            :62F:C261103UZS1340,00
            """);
        var b = StatementKit.Mt940($"""
            :20:B
            :25:20208000900100012012
            :60F:C261103UZS1500,00
            {day3}
            :61:2611041104D10,00NCHGNONREF
            :86:Комиссия
            :62F:C261104UZS1330,00
            """);
        var ra = await Host.ImportAsync(tenant, connection, a, StatementFormat.Mt940, file: "a.sta");
        var rb = await Host.ImportAsync(tenant, connection, b, StatementFormat.Mt940, file: "b.sta");
        Assert.Equal((4, 4, 0), (ra.TransactionsTotal, ra.TransactionsNew, ra.TransactionsDuplicate));
        Assert.Equal((4, 1, 3), (rb.TransactionsTotal, rb.TransactionsNew, rb.TransactionsDuplicate));
        Assert.Equal(5, await Host.CountAsync(tenant, db => db.BankTransactions));
        Assert.Equal(2, await Host.CountAsync(tenant, db => db.BankTransactions.Where(t => t.Amount == 100_00))); // two identical payments
        var days = await Host.QueryAsync(tenant, db => db.BankDailyBalances.AsNoTracking().OrderBy(d => d.Date).Select(d => new { d.Date, d.OpeningBalance, d.ClosingBalance }).ToListAsync());
        Assert.Equal([(new DateOnly(2026, 11, 2), 1000_00L, 1500_00L), (new DateOnly(2026, 11, 3), 1500_00L, 1340_00L), (new DateOnly(2026, 11, 4), 1340_00L, 1330_00L)],
            days.Select(d => (d.Date, d.OpeningBalance, d.ClosingBalance)));
    }

    [Fact]
    public async Task Operation_with_a_file_id_and_changed_content_is_updated_in_place()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Import changed");
        var profile = StatementProfiles.KapitalBankMt940Assumed;
        profile.OperationId = "bank_reference";
        var first = await Host.ImportFixtureAsync(tenant, connection, "mt940_kapitalbank_assumed.sta", profile);
        Assert.Equal((2, 2), (first.TransactionsTotal, first.TransactionsNew));
        var before = await Host.QueryAsync(tenant, db => db.BankTransactions.AsNoTracking().SingleAsync(t => t.OperationId == "f:KB0000124"));
        Assert.Equal(OperationIdSource.File, before.IdSource);

        Host.Time.Advance(TimeSpan.FromMinutes(5));
        var text = Encoding.UTF8.GetString(StatementFiles.Read("mt940_kapitalbank_assumed.sta")).Replace("Оплата по договору 15/2026", "Оплата по договору 15/2026 (уточнено)");
        var second = await Host.ImportAsync(tenant, connection, Encoding.UTF8.GetBytes(text), StatementFormat.Mt940, profile, file: "changed.sta");
        Assert.Equal((2, 0, 2), (second.TransactionsTotal, second.TransactionsNew, second.TransactionsDuplicate));
        var after = await Host.QueryAsync(tenant, db => db.BankTransactions.AsNoTracking().SingleAsync(t => t.OperationId == "f:KB0000124"));
        Assert.Equal((before.Id, before.CreatedAt, true), (after.Id, after.CreatedAt, after.IsFinal));
        Assert.Equal("Оплата по договору 15/2026 (уточнено)", after.Purpose);
        Assert.NotEqual(before.SourceHash, after.SourceHash);
        Assert.Equal(Host.Time.Now, after.UpdatedAt);
        Assert.Equal(2, await Host.CountAsync(tenant, db => db.BankTransactions));
        var raw = await Host.QueryAsync(tenant, db => db.RawObjects.AsNoTracking().SingleAsync(r => r.ExternalId == "20208000900100004004:f:KB0000124"));
        Assert.Equal(after.SourceHash, raw.Hash);
        Assert.Contains("уточнено", raw.Payload);
    }

    [Fact]
    public async Task Dry_run_reads_and_checks_but_writes_nothing()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Import dry run");
        var before = await Host.CountsAsync(tenant);
        var r = await Host.ImportFixtureAsync(tenant, connection, "mt940_multi_cp1251.sta", dryRun: true);
        Assert.Equal(StatementImportStatus.DryRun, r.Status);
        Assert.Equal(3, r.Statements.Count);
        Assert.Equal((5, 5, BalanceCheckResult.Passed), (r.TransactionsTotal, r.TransactionsNew, r.BalanceCheck));
        Assert.All(r.Statements, s => Assert.False(s.AccountExists));
        Assert.Equal(5, r.Preview!.Count);
        Assert.Equal(before, await Host.CountsAsync(tenant));
        Assert.Null((await Host.ConnectionAsync(tenant, connection)).LastSuccessfulSyncAt);

        // A dry run of a bad file writes no failed run either.
        var bad = await Host.ImportFixtureAsync(tenant, connection, "mt940_bad_balance.sta", dryRun: true);
        Assert.Equal(StatementImportStatus.Rejected, bad.Status);
        Assert.Equal(before, await Host.CountsAsync(tenant));
    }

    [Fact]
    public async Task Unknown_account_needs_create_accounts_and_an_account_of_another_connection_is_refused()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Import accounts");
        var refused = await Host.ImportFixtureAsync(tenant, connection, "mt940_single_utf8.sta", create: false);
        Assert.Equal(StatementImportStatus.Rejected, refused.Status);
        Assert.Contains("--create-accounts", refused.Message);
        Assert.Contains("****1001", refused.Message);
        Assert.DoesNotContain("20208000900100001001", refused.Message);
        Assert.Equal(0, await Host.CountAsync(tenant, db => db.BankAccounts));
        var failed = await Host.LastRunAsync(tenant, connection);
        Assert.Equal(SyncRunResult.Failed, failed.Result);
        Assert.Contains("--create-accounts", failed.Error);
        var state = await Host.ConnectionAsync(tenant, connection);
        Assert.Equal((ConnectionStatus.Active, 0), (state.Status, state.ConsecutiveFailures)); // a bad file says nothing about the connection

        var created = await Host.ImportFixtureAsync(tenant, connection, "mt940_single_utf8.sta", create: true);
        Assert.Equal(StatementImportStatus.Imported, created.Status);
        Assert.False(Assert.Single(created.Statements).AccountExists);
        Assert.Equal(1, await Host.CountAsync(tenant, db => db.BankAccounts));
        // A later file of the known account needs no flag; a file that also names an unknown account (USD) is refused as a whole.
        var later = StatementFiles.Read("mt940_single_utf8.sta").Concat("\r\n"u8.ToArray()).ToArray();
        Assert.Equal(StatementImportStatus.Imported, (await Host.ImportAsync(tenant, connection, later, StatementFormat.Mt940, create: false, file: "later.sta")).Status);
        var mixed = await Host.ImportFixtureAsync(tenant, connection, "mt940_multi_cp1251.sta", create: false);
        Assert.Equal(StatementImportStatus.Rejected, mixed.Status);
        Assert.Contains("****2002", mixed.Message);
        Assert.Equal(3, await Host.CountAsync(tenant, db => db.BankTransactions));

        // The same account loaded by a bank API connection: a file for it would count its operations twice.
        var bank = await Host.CreateConnectionAsync(tenant, AdapterKind.Bank);
        await Host.InTenantAsync(tenant, sp =>
        {
            var db = sp.GetRequiredService<ISynectDbContext>();
            return db.InTransactionAsync(async ct =>
            {
                db.BankAccounts.Add(new BankAccount { ConnectionId = bank, AccountNumber = "20208000900100003003", Mfo = "00440", Currency = "UZS", CreatedAt = StatementKit.Now, UpdatedAt = StatementKit.Now });
                await db.SaveChangesAsync(ct);
                return true;
            }, CancellationToken.None);
        });
        var twice = await Host.ImportFixtureAsync(tenant, connection, "mt940_uzbek_utf8bom.sta");
        Assert.Equal(StatementImportStatus.Rejected, twice.Status);
        Assert.Contains("already loaded by connection", twice.Message);
    }

    [Fact]
    public async Task Balances_that_do_not_add_up_are_refused_unless_forced_and_then_the_balance_rule_reports_them()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Import bad balance");
        var refused = await Host.ImportFixtureAsync(tenant, connection, "mt940_bad_balance.sta");
        Assert.Equal(StatementImportStatus.Rejected, refused.Status);
        Assert.Contains("do not add up", refused.Message);
        Assert.Contains("difference 0,50", refused.Message);
        Assert.Equal(0, await Host.CountAsync(tenant, db => db.BankTransactions));

        var forced = await Host.ImportFixtureAsync(tenant, connection, "mt940_bad_balance.sta", force: true);
        Assert.Equal((StatementImportStatus.Imported, BalanceCheckResult.Failed), (forced.Status, forced.BalanceCheck));
        Assert.Contains(forced.Warnings, w => w.Code == "balance_mismatch_forced");
        Assert.Equal(BalanceCheckResult.Failed, (await Host.QueryAsync(tenant, db => db.StatementImports.AsNoTracking().SingleAsync())).BalanceCheck);
        var item = await Host.ItemAsync(tenant, BankBalanceMismatch); // evaluated after the import
        Assert.Equal(AttentionSeverity.High, item.Severity);
    }

    [Fact]
    public async Task Fraction_of_a_tiyin_rejects_only_that_row_and_bad_files_are_rejected_clearly()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Import fractional");
        var r = await Host.ImportFixtureAsync(tenant, connection, "mt940_fractional_tiyin.sta");
        Assert.Equal((StatementImportStatus.Imported, 1, 1, BalanceCheckResult.Passed), (r.Status, r.TransactionsTotal, r.RowsRejected, r.BalanceCheck));
        Assert.Contains(r.Warnings, w => w.Code == "fractional_minor_units");
        Assert.Equal(200_00, await Host.QueryAsync(tenant, db => db.BankTransactions.Select(t => t.Amount).SingleAsync()));

        foreach (var name in new[] { "empty.sta", "mt940_truncated.sta", "damaged_binary.sta" })
        {
            var bad = await Host.ImportFixtureAsync(tenant, connection, name);
            Assert.Equal(StatementImportStatus.Rejected, bad.Status);
            Assert.Contains("not a readable MT940 statement", bad.Message);
        }
        Assert.Equal(1, await Host.CountAsync(tenant, db => db.StatementImports));
    }

    [Fact]
    public async Task Csv_imports_with_builtin_and_custom_profiles()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Import CSV");
        var semicolon = await Host.ImportFixtureAsync(tenant, connection, "csv_semicolon_cp1251.csv");
        Assert.Equal((StatementImportStatus.Imported, 3, BalanceCheckResult.Passed), (semicolon.Status, semicolon.TransactionsTotal, semicolon.BalanceCheck));
        var custom = await Host.ImportFixtureAsync(tenant, connection, "csv_pipe_cp866.csv", StatementFiles.CustomCsvProfile());
        Assert.Equal((StatementImportStatus.Imported, 2, BalanceCheckResult.Skipped), (custom.Status, custom.TransactionsTotal, custom.BalanceCheck));
        Assert.Contains(custom.Warnings, w => w.Code == "no_balances");
        var signed = await Host.ImportFixtureAsync(tenant, connection, "csv_comma_utf8bom_signed.csv");
        Assert.Equal(StatementImportStatus.Imported, signed.Status);
        Assert.Equal("test_bank_cp866", await Host.QueryAsync(tenant, db => db.StatementImports.Where(i => i.Format == StatementFormat.Csv && i.TransactionsTotal == 2).Select(i => i.Profile).SingleAsync()));
        // Day balances only where the file had balances; INN read from the purpose of a CSV row.
        Assert.Equal(2, await Host.CountAsync(tenant, db => db.BankDailyBalances));
        Assert.Equal("17", await Host.QueryAsync(tenant, db => db.BankTransactions.Where(t => t.CounterpartyInn == "302345678" && t.Amount == 1200_00).Select(t => t.DocumentNumber).SingleAsync()));
        Assert.Equal(3, await Host.CountAsync(tenant, db => db.BankAccounts));
    }

    [Fact]
    public async Task Inn_and_names_from_the_payment_text_feed_counterparties_and_name_spellings()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Import uzbek");
        var r = await Host.ImportFixtureAsync(tenant, connection, "mt940_uzbek_utf8bom.sta");
        Assert.Equal(StatementImportStatus.Imported, r.Status);
        var reversal = await Host.QueryAsync(tenant, db => db.BankTransactions.AsNoTracking().SingleAsync(t => t.SourceState == "reversal"));
        Assert.Equal((TransactionDirection.Incoming, "306789012"), (reversal.Direction, reversal.CounterpartyInn));
        using (var details = JsonDocument.Parse(reversal.Details!))
            Assert.True(details.RootElement.GetProperty("reversal").GetBoolean());
        var aliases = await Host.QueryAsync(tenant, db => db.CounterpartyAliases.AsNoTracking().OrderBy(a => a.CounterpartyInn).Select(a => new { a.CounterpartyInn, a.Name }).ToListAsync());
        Assert.Equal([("305678901", "Bog'bon Savdo MCHJ"), ("306789012", "«Боғбон» МЧЖ")], aliases.Select(a => (a.CounterpartyInn, a.Name)));
        var account = await Host.QueryAsync(tenant, db => db.BankAccounts.AsNoTracking().SingleAsync());
        Assert.Equal("00440", account.Mfo); // bank code from :25:
    }

    [Fact]
    public async Task Imports_are_isolated_between_tenants()
    {
        Start();
        var (x, cx) = await Host.FileTenantAsync("Import tenant X");
        var (y, cy) = await Host.FileTenantAsync("Import tenant Y");
        await Host.ImportFixtureAsync(x, cx, "mt940_single_utf8.sta");

        Assert.Equal(0, await Host.CountAsync(y, db => db.BankTransactions));
        Assert.Equal(0, await Host.CountAsync(y, db => db.StatementImports));
        Assert.Empty(await Host.InTenantAsync(y, sp => sp.GetRequiredService<BankStatementImportService>().ListAsync(null, 10, CancellationToken.None)));
        // Y cannot import into X's connection (not visible under row-level security).
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Host.ImportFixtureAsync(y, cx, "mt940_multi_cp1251.sta"));
        // The SHA-256 check is per client: Y imports the same file into its own connection and account.
        var own = await Host.ImportFixtureAsync(y, cy, "mt940_single_utf8.sta");
        Assert.Equal((StatementImportStatus.Imported, 3), (own.Status, own.TransactionsNew));
        Assert.Equal(3, await Host.CountAsync(x, db => db.BankTransactions));
        Assert.Equal(3, await Host.CountAsync(y, db => db.BankTransactions));
    }

    [Fact]
    public async Task File_connection_is_never_queued_and_has_its_own_staleness_threshold()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Import queue");
        await Host.ImportFixtureAsync(tenant, connection, "mt940_single_utf8.sta");

        var request = await Host.RequestSyncAsync(tenant, connection);
        Assert.Equal(SyncRequestStatus.NotLoadable, request.Status);
        await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<SyncPlanner>().PlanAsync(CancellationToken.None));
        Assert.Empty(await Host.JobsAsync(tenant, connection));

        // Two days without a new statement are fine for a file source (36 h would flag an API source) ...
        Host.Time.Advance(TimeSpan.FromDays(2));
        await Host.EvaluateAsync(tenant, AttentionCategory.Connection);
        Assert.DoesNotContain(await Host.ItemsAsync(tenant), i => i.RuleCode == ConnectionStaleData);
        // ... more than Attention:FileStaleAfter (7 days) is not.
        Host.Time.Advance(TimeSpan.FromDays(6));
        await Host.EvaluateAsync(tenant, AttentionCategory.Connection);
        var stale = await Host.ItemAsync(tenant, ConnectionStaleData);
        using var details = JsonDocument.Parse(stale.Details);
        Assert.Equal("file_import", details.RootElement.GetProperty("kind").GetString());
        Assert.Equal(168, details.RootElement.GetProperty("threshold_hours").GetDouble());
    }

    [Fact]
    public async Task Dismissed_item_whose_condition_is_gone_is_archived_after_the_retention_and_reopened_when_it_comes_back()
    {
        Start();
        var (tenant, connection) = await Host.FileTenantAsync("Attention archive");
        await Host.ImportFixtureAsync(tenant, connection, "mt940_bad_balance.sta", force: true);
        var item = await Host.ItemAsync(tenant, BankBalanceMismatch);
        Assert.Equal(AttentionUpdateOutcome.Changed, (await Host.UpdateAsync(tenant, item.Id, AttentionAction.Dismiss, comment: "Known bank error")).Outcome);

        // A corrected statement of the same period: the condition is gone, the item stays dismissed for now.
        Host.Time.Advance(TimeSpan.FromHours(1));
        var corrected = Encoding.UTF8.GetString(StatementFiles.Read("mt940_bad_balance.sta")).Replace(":62F:C261103UZS10249999,00", ":62F:C261103UZS10249999,50");
        Assert.Equal(BalanceCheckResult.Passed, (await Host.ImportAsync(tenant, connection, Encoding.UTF8.GetBytes(corrected), StatementFormat.Mt940, file: "fixed.sta")).BalanceCheck);
        Assert.Equal(AttentionStatus.Dismissed, (await Host.ItemAsync(tenant, BankBalanceMismatch)).Status);
        Host.Time.Advance(TimeSpan.FromDays(89));
        await Host.EvaluateAsync(tenant, AttentionCategory.Bank);
        Assert.Equal(AttentionStatus.Dismissed, (await Host.ItemAsync(tenant, BankBalanceMismatch)).Status);

        // 90 days after it was last found: archived, hidden from the default list, history kept, no person changes.
        Host.Time.Advance(TimeSpan.FromDays(2));
        var run = await Host.EvaluateAsync(tenant, AttentionCategory.Bank);
        Assert.Equal(1, run.Archived);
        var archived = await Host.ItemAsync(tenant, BankBalanceMismatch);
        Assert.Equal(AttentionStatus.Archived, archived.Status);
        Assert.Equal([AttentionEventKind.Created, AttentionEventKind.Dismissed, AttentionEventKind.Archived], await Host.EventsAsync(tenant, item.Id));
        Assert.DoesNotContain((await Host.ListAsync(tenant)).Data!.Items, i => i.Id == item.Id);
        Assert.DoesNotContain((await Host.ListAsync(tenant, new AttentionFilter(Statuses: [AttentionStatus.Dismissed]))).Data!.Items, i => i.Id == item.Id);
        Assert.Contains((await Host.ListAsync(tenant, new AttentionFilter(Statuses: [AttentionStatus.Archived]))).Data!.Items, i => i.Id == item.Id);
        Assert.Equal(AttentionUpdateOutcome.NotAllowed, (await Host.UpdateAsync(tenant, item.Id, AttentionAction.Reopen)).Outcome);
        Assert.Equal(0, (await Host.InTenantAsync(tenant, sp => sp.GetRequiredService<AttentionService>().SummaryAsync(CancellationToken.None))).Data!.Dismissed);

        // The condition comes back on the same account: the same row opens again.
        var today = DateOnly.FromDateTime(Host.Time.Now.AddHours(5).Date).AddDays(-1);
        var bad = StatementKit.Mt940($"""
            :20:AGAIN
            :25:20208000900100005005
            :60F:C{today:yyMMdd}UZS10249999,50
            :61:{today:yyMMdd}{today:MMdd}D100,00NTRFNONREF
            :86:Комиссия
            :62F:C{today:yyMMdd}UZS10249999,50
            """);
        Assert.Equal(StatementImportStatus.Imported, (await Host.ImportAsync(tenant, connection, bad, StatementFormat.Mt940, force: true, file: "again.sta")).Status);
        var reopened = await Host.ItemAsync(tenant, BankBalanceMismatch);
        Assert.Equal((item.Id, AttentionStatus.New), (reopened.Id, reopened.Status));
        Assert.Equal([AttentionEventKind.Created, AttentionEventKind.Dismissed, AttentionEventKind.Archived, AttentionEventKind.Reopened],
            await Host.EventsAsync(tenant, item.Id));
    }
}

/// <summary>
/// Synthetic statement + Didox documents: the reconciliation finds the expected payments and the attention rules create the
/// expected items, exactly as for bank API loads.
/// </summary>
public class StatementReconciliationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private TestHost Host => fixture.Host;

    private static FakeDidoxDocument Doc(string id, int owner, string number, DateOnly date, string inn, decimal total, string? contract = null, string? seller = null) => new()
    {
        DocId = id, DocType = "002", Owner = owner, Status = 3, Name = number, DocDate = date, PartnerTin = inn, PartnerCompany = "PARTNER " + inn,
        ContractNumber = contract, ContractDate = contract is null ? null : new DateOnly(2026, 1, 15), TotalSum = total, TotalVatSum = Math.Round(total * 12m / 112m, 2),
        Created = new DateTimeOffset(date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(5)),
        Updated = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(5)), SellerAccount = seller,
    };

    [Fact]
    public async Task Imported_statement_is_reconciled_with_didox_documents_and_attention_items_are_created()
    {
        Assert.SkipUnless(fixture.Available, PostgresFixture.SkipReason);
        Host.Time.Set(StatementKit.Now);
        var (tenant, connection) = await Host.FileTenantAsync("Statement reconciliation");
        var edo = await Host.CreateConnectionAsync(tenant, AdapterKind.Edo);
        Host.FakeEdo.For(edo).UseDocuments(
        [
            Doc("d901", 1, "СФ-901", new DateOnly(2026, 10, 30), "301111111", 1_200_000m),
            Doc("d902", 0, "Б-901", new DateOnly(2026, 10, 28), "302222222", 2_000_000m, contract: "15/2026", seller: "20208000800000000007"),
            Doc("d909", 0, "D-909", new DateOnly(2026, 10, 29), "304444444", 800_000m, seller: "20208000900000000001"),
            Doc("d904", 1, "СФ-904", new DateOnly(2026, 11, 1), "301111111", 250_000m),
        ]);
        Assert.Equal(SyncRunResult.Succeeded, (await Host.SyncEdoAsync(tenant, edo)).Result);

        var r = await Host.ImportFixtureAsync(tenant, connection, "mt940_reconciliation.sta");
        Assert.Equal((StatementImportStatus.Imported, 6, BalanceCheckResult.Passed), (r.Status, r.TransactionsTotal, r.BalanceCheck));

        var matched = await Host.QueryAsync(tenant, async db =>
            await (from i in db.ReconciliationMatchItems.AsNoTracking()
                   join m in db.ReconciliationMatches.AsNoTracking() on i.MatchId equals m.Id
                   where i.Kind == MatchItemKind.BankTransaction && m.Status != MatchStatus.Rejected
                   join t in db.BankTransactions.AsNoTracking() on i.RefId equals t.Id
                   join d in db.ReconciliationMatchItems.AsNoTracking() on i.MatchId equals d.MatchId
                   where d.Kind == MatchItemKind.EdoDocument
                   join e in db.EdoDocuments.AsNoTracking() on d.RefId equals e.Id
                   select new { t.Amount, t.PostingDate, e.ExternalId }).ToListAsync());
        Assert.Contains(matched, x => x.Amount == 1_200_000_00 && x.ExternalId == "d901");
        Assert.Contains(matched, x => x.Amount == 2_000_000_00 && x.ExternalId == "d902");
        Assert.Contains(matched, x => x.Amount == 800_000_00 && x.ExternalId == "d909");
        Assert.Single(matched, x => x.ExternalId == "d904"); // one of the two equal payments
        Assert.Equal(1, await Host.CountAsync(tenant, db => db.ReconciliationFindings.Where(f => f.Kind == FindingKind.PossibleDuplicate)));

        var items = await Host.ItemsAsync(tenant);
        Assert.Equal(AttentionSeverity.High, Assert.Single(items, i => i.RuleCode == PossibleDuplicatePayment).Severity);
        var mismatch = Assert.Single(items, i => i.RuleCode == PaymentAccountMismatch);
        Assert.Contains("****0099", mismatch.Title);
        Assert.DoesNotContain(items, i => i.RuleCode == BankBalanceMismatch);
    }
}
