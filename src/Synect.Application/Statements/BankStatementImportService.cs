using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Attention;
using Synect.Application.Common;
using Synect.Application.Sync;
using Synect.Domain.Banking;
using Synect.Domain.Common;
using Synect.Domain.Sync;

namespace Synect.Application.Statements;

/// <summary>Statements section.</summary>
public sealed class StatementImportOptions
{
    /// <summary>Largest accepted file.</summary>
    public int MaxFileBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Operations returned as a preview (dry run).</summary>
    public int PreviewRows { get; set; } = 5;

    /// <summary>Longest period one statement may cover.</summary>
    public int MaxPeriodDays { get; set; } = 3700;
}

/// <param name="Profile">How to read the file; see <see cref="StatementProfiles"/> and docs/import-profiles.md.</param>
/// <param name="Force">Import even when the balances do not add up (balance_check = Failed, with a warning).</param>
/// <param name="CreateAccounts">Create accounts the tenant does not have yet; without it such a file is rejected.</param>
/// <param name="DryRun">Read and check only; nothing is written.</param>
/// <param name="ImportedByKey">API key of the person importing (future API); null for developer tools.</param>
public sealed record StatementImportRequest(
    Guid ConnectionId,
    string FileName,
    StatementFormat Format,
    StatementProfile Profile,
    bool Force = false,
    bool CreateAccounts = false,
    bool DryRun = false,
    Guid? ImportedByKey = null);

public enum StatementImportStatus
{
    Imported,

    /// <summary>Read and checked, nothing written (<see cref="StatementImportRequest.DryRun"/>).</summary>
    DryRun,

    /// <summary>The same file (SHA-256) was imported before; nothing written.</summary>
    AlreadyImported,

    /// <summary>The file was not imported (see the message). A failed sync run is recorded unless it was a dry run.</summary>
    Rejected,
}

/// <summary>One statement of the file after the checks. Amounts in minor units.</summary>
/// <param name="ComputedClosing">Opening + credit - debit of the accepted operations (null without an opening balance).</param>
public sealed record StatementCheck(
    string AccountNumber,
    string? BankCode,
    string Currency,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    long? OpeningBalance,
    long? ClosingBalance,
    long TotalCredit,
    long TotalDebit,
    long? ComputedClosing,
    int Operations,
    BalanceCheckResult BalanceCheck,
    bool AccountExists);

public sealed record StatementImportView(
    Guid Id,
    Guid ConnectionId,
    Guid? SyncRunId,
    string FileName,
    string FileSha256,
    StatementFormat Format,
    string Profile,
    DateTimeOffset ImportedAt,
    Guid? ImportedByKey,
    string Account,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    int TransactionsTotal,
    int TransactionsNew,
    int TransactionsDuplicate,
    int RowsRejected,
    int WarningCount,
    BalanceCheckResult BalanceCheck);

/// <param name="Message">Short English explanation of the outcome (why a file was rejected, what was imported).</param>
/// <param name="Preview">First operations of the file (dry run), not masked: callers mask account numbers when printing.</param>
/// <param name="Existing">The earlier import of the same file (AlreadyImported).</param>
public sealed record StatementImportResult(
    StatementImportStatus Status,
    string Message,
    string FileSha256,
    string? Encoding,
    IReadOnlyList<StatementCheck> Statements,
    IReadOnlyList<StatementWarning> Warnings,
    int TransactionsTotal,
    int TransactionsNew,
    int TransactionsDuplicate,
    int RowsRejected,
    BalanceCheckResult? BalanceCheck,
    Guid? ImportId = null,
    Guid? SyncRunId = null,
    IReadOnlyList<ParsedOperation>? Preview = null,
    StatementImportView? Existing = null)
{
    /// <summary>Time per phase of this import (parse, plan, raw layer, operations, save, totals, reconciliation, attention ...).</summary>
    public IReadOnlyDictionary<string, TimeSpan> Timings { get; init; } = new Dictionary<string, TimeSpan>();
}

/// <summary>
/// Imports a bank statement file (MT940, CSV) into the same model and pipeline as the bank API load: raw layer, upsert of
/// operations by (account, operation id), day balances (days of the file are closed), counterparties and name spellings,
/// monthly totals, reconciliation of the touched INNs, then the attention rules. One database transaction per file.
/// The file content is never logged. Ready to be called from a future HTTP API (no dependency on Infrastructure).
/// </summary>
public sealed class BankStatementImportService(
    ISynectDbContext db,
    ITenantContext tenant,
    IEnumerable<IStatementParser> parsers,
    BankLoadWriter writer,
    AttentionTrigger attention,
    LoadProfile profile,
    TimeProvider time,
    IOptions<StatementImportOptions> options,
    ILogger<BankStatementImportService> logger)
{
    public const string AccountObjectType = "statement.account";
    public const string TransactionObjectType = "statement.transaction";
    public const string DayBalanceObjectType = "statement.day_balance";

    private static readonly JsonSerializerOptions RawJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<StatementImportResult> ImportAsync(StatementImportRequest request, Stream content, CancellationToken ct)
    {
        _ = tenant.TenantId ?? throw new InvalidOperationException("Statement import requires a tenant context.");
        var bytes = await ReadAsync(content, options.Value.MaxFileBytes, ct);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var fileName = Path.GetFileName(request.FileName ?? "");
        if (fileName.Length > 256) fileName = fileName[^256..];

        var (connection, existing) = await db.InTransactionAsync(async token =>
        {
            var c = await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ConnectionId, token)
                ?? throw new KeyNotFoundException($"Connection {request.ConnectionId} not found for the current tenant.");
            var e = await db.StatementImports.AsNoTracking().Where(i => i.FileSha256 == sha).OrderBy(i => i.ImportedAt).FirstOrDefaultAsync(token);
            return (c, e);
        }, ct);
        if (connection.AdapterKind != AdapterKind.File)
            throw new InvalidOperationException($"Connection '{connection.Name}' is not a file import connection (create one with create-file-connection).");
        if (connection.Status == ConnectionStatus.Disabled)
            throw new InvalidOperationException($"Connection '{connection.Name}' is disabled.");

        if (existing is not null)
            return new StatementImportResult(StatementImportStatus.AlreadyImported,
                $"This file was already imported on {existing.ImportedAt:yyyy-MM-dd HH:mm} UTC (import {existing.Id}, {existing.TransactionsTotal} operations, " +
                $"period {existing.PeriodFrom:yyyy-MM-dd} - {existing.PeriodTo:yyyy-MM-dd}); nothing was changed.",
                sha, null, [], [], 0, 0, 0, 0, existing.BalanceCheck, existing.Id, existing.SyncRunId, Existing: View(existing));

        var parser = parsers.FirstOrDefault(p => p.Format == request.Format)
            ?? throw new NotSupportedException($"No parser for format {request.Format}.");
        if (request.Profile.Format != request.Format)
            throw new ArgumentException($"Profile '{request.Profile.Name}' is for {request.Profile.Format}, not {request.Format}.");

        ParsedStatementFile parsed;
        try
        {
            using (profile.Measure("parse"))
                parsed = parser.Parse(bytes, request.Profile);
        }
        catch (StatementFormatException ex)
        {
            return await RejectAsync(request, connection, sha, null, [], [], 0,
                $"The file is not a readable {Name(request.Format)} statement: {ex.Message}", ct);
        }

        var warnings = parsed.Warnings.ToList();
        ImportPlan plan;
        using (profile.Measure("plan"))
            plan = await PlanAsync(request, connection, parsed, warnings, ct);
        var checks = plan.Statements.Select(p => p.Check).ToList();
        var total = plan.Statements.Sum(p => p.Infos.Count);
        var balanceCheck = Overall(checks);

        if (plan.Errors.Count > 0)
            return await RejectAsync(request, connection, sha, parsed.Encoding, checks, warnings, parsed.RowsRejected,
                string.Join(" ", plan.Errors), ct);

        if (request.DryRun)
        {
            var preview = parsed.Statements.SelectMany(s => s.Operations).Take(options.Value.PreviewRows).ToList();
            var existingCount = await CountExistingAsync(plan, ct);
            return new StatementImportResult(StatementImportStatus.DryRun,
                $"Dry run: {total} operation(s) in {checks.Count} statement(s) would be imported ({total - existingCount} new, {existingCount} already stored); nothing was written.",
                sha, parsed.Encoding, checks, warnings, total, total - existingCount, existingCount, parsed.RowsRejected, balanceCheck, Preview: preview);
        }

        writer.RefreshUnchangedRaw = false;
        StatementImport import;
        SyncRun run;
        int created = 0, duplicates = 0;
        IDisposable? commitScope = null;
        try
        {
            (import, run, created, duplicates) = await db.InTransactionAsync(async token =>
            {
                var now = time.GetUtcNow();
                var tracked = await db.Connections.SingleAsync(c => c.Id == connection.Id, token);
                var r = SyncRun.Start(tracked.TenantId, tracked.Id, now, plan.From, plan.To);
                db.SyncRuns.Add(r);

                var accounts = await writer.UpsertAccountsAsync(tracked, plan.Statements
                    .GroupBy(p => p.Account).Select(g => g.Last().AccountInfo).ToList(), token);
                var byNumber = accounts.ToDictionary(a => a.AccountNumber, StringComparer.Ordinal);
                // New accounts must exist before operations are written in bulk (foreign key).
                using (profile.Measure("save_changes"))
                    await db.SaveChangesAsync(token);
                // All statements of one account at once (a daily MT940 file has one statement per day): one lookup of the
                // stored ids, one bulk write of operations and raw records, one upsert of day balances per account, and a
                // single SaveChanges for the whole file below.
                int n = 0, d = 0;
                foreach (var group in plan.Statements.GroupBy(p => p.Account, StringComparer.Ordinal))
                {
                    var account = byNumber[group.Key];
                    var counts = await writer.UpsertTransactionsAsync(tracked, account, group.SelectMany(p => p.Infos).ToList(), dayClosed: true, token);
                    n += counts.New;
                    d += counts.Existing;
                    await writer.UpsertBalancesAsync(tracked, account, group.SelectMany(p => p.Days).ToList(), token);
                }

                var i = new StatementImport
                {
                    TenantId = tracked.TenantId,
                    ConnectionId = tracked.Id,
                    SyncRunId = r.Id,
                    FileName = fileName,
                    FileSha256 = sha,
                    Format = request.Format,
                    Profile = Cut(request.Profile.Name, 128),
                    ImportedAt = now,
                    ImportedByKey = request.ImportedByKey,
                    Account = Cut(string.Join(",", plan.Statements.Select(p => p.Account).Distinct()), StatementImport.MaxAccountLength),
                    PeriodFrom = plan.From,
                    PeriodTo = plan.To,
                    TransactionsTotal = total,
                    TransactionsNew = n,
                    TransactionsDuplicate = d,
                    RowsRejected = parsed.RowsRejected,
                    WarningCount = warnings.Count,
                    BalanceCheck = balanceCheck,
                };
                db.StatementImports.Add(i);
                // Operations and balances must be in the database before the totals and the reconciliation read them
                // (the EF write path keeps them pending until SaveChanges; the bulk path has written them already).
                using (profile.Measure("save_changes"))
                    await db.SaveChangesAsync(token);

                // Monthly totals and reconciliation of exactly what this file touched, atomically with the import.
                await writer.CompleteAsync(tracked, token);
                r.Succeed(time.GetUtcNow(), isComplete: true, total);
                tracked.MarkSyncSucceeded(time.GetUtcNow());
                using (profile.Measure("save_changes"))
                    await db.SaveChangesAsync(token);
                commitScope = profile.Measure("commit");
                return (i, r, n, d);
            }, ct);
            commitScope?.Dispose();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            writer.Discard();
            // Two imports of the same file at once: the unique (tenant, sha256) index lets only one through.
            var winner = await db.InTransactionAsync(token =>
                db.StatementImports.AsNoTracking().FirstOrDefaultAsync(i => i.FileSha256 == sha, token), CancellationToken.None);
            if (winner is not null)
                return new StatementImportResult(StatementImportStatus.AlreadyImported,
                    $"This file was imported at the same time by another request (import {winner.Id}); nothing was changed.",
                    sha, parsed.Encoding, checks, warnings, 0, 0, 0, parsed.RowsRejected, winner.BalanceCheck, winner.Id, winner.SyncRunId, Existing: View(winner));
            logger.LogWarning("Statement import failed: connection {ConnectionId}, file {Sha}, error type {ErrorType}",
                connection.Id, sha[..12], ex.GetType().Name);
            await RecordFailedRunAsync(connection, plan.From, plan.To, "Statement import failed: " + ex.GetType().Name, total);
            throw;
        }

        logger.LogInformation(
            "Statement imported: connection {ConnectionId}, import {ImportId}, format {Format}, {Total} operations ({New} new, {Duplicate} already stored), {Rejected} rows rejected, balance check {BalanceCheck}",
            connection.Id, import.Id, request.Format, total, created, duplicates, parsed.RowsRejected, balanceCheck);

        // Attention rules over the new data (own scope and transaction; never fails the import).
        using (profile.Measure("attention_rules"))
            await attention.AfterLoadAsync(AdapterKind.File, succeeded: true, CancellationToken.None);

        var message = $"Imported {total} operation(s) from {checks.Count} statement(s): {created} new, {duplicates} already stored" +
                      (parsed.RowsRejected > 0 ? $", {parsed.RowsRejected} row(s) rejected" : "") +
                      $"; balance check {balanceCheck.ToString().ToLowerInvariant()}.";
        return new StatementImportResult(StatementImportStatus.Imported, message, sha, parsed.Encoding, checks, warnings,
            total, created, duplicates, parsed.RowsRejected, balanceCheck, import.Id, run.Id) { Timings = profile.Snapshot() };
    }

    /// <summary>Imports of the current tenant, newest first (optionally of one connection).</summary>
    public Task<List<StatementImportView>> ListAsync(Guid? connectionId, int limit, CancellationToken ct) =>
        db.InTransactionAsync(async token =>
        {
            var q = db.StatementImports.AsNoTracking();
            if (connectionId is { } id) q = q.Where(i => i.ConnectionId == id);
            var rows = await q.OrderByDescending(i => i.ImportedAt).ThenByDescending(i => i.Id).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(token);
            return rows.Select(View).ToList();
        }, ct);

    private static StatementImportView View(StatementImport i) => new(i.Id, i.ConnectionId, i.SyncRunId, i.FileName, i.FileSha256, i.Format,
        i.Profile, i.ImportedAt, i.ImportedByKey, i.Account, i.PeriodFrom, i.PeriodTo, i.TransactionsTotal, i.TransactionsNew,
        i.TransactionsDuplicate, i.RowsRejected, i.WarningCount, i.BalanceCheck);

    // ---- Planning (no writes) ------------------------------------------------------------------------------------------

    private sealed class StatementPlan
    {
        public required string Account { get; init; }
        public required BankAccountInfo AccountInfo { get; init; }
        public required List<BankTransactionInfo> Infos { get; init; }
        public required List<BankBalanceInfo> Days { get; init; }
        public required StatementCheck Check { get; init; }
    }

    private sealed class ImportPlan
    {
        public List<StatementPlan> Statements { get; } = [];
        public List<string> Errors { get; } = [];
        public DateOnly From { get; set; } = DateOnly.MaxValue;
        public DateOnly To { get; set; } = DateOnly.MinValue;
    }

    private async Task<ImportPlan> PlanAsync(
        StatementImportRequest request, Connection connection, ParsedStatementFile parsed, List<StatementWarning> warnings, CancellationToken ct)
    {
        var plan = new ImportPlan();
        if (parsed.Statements.Count == 0)
        {
            plan.Errors.Add(parsed.RowsRejected > 0
                ? $"No operation of the file could be read ({parsed.RowsRejected} row(s) rejected, see the warnings)."
                : "The file contains no statement.");
            return plan;
        }

        var numbers = parsed.Statements.Select(s => s.AccountNumber).Distinct().ToList();
        var known = await db.InTransactionAsync(token => db.BankAccounts.AsNoTracking()
            .Where(a => numbers.Contains(a.AccountNumber))
            .Join(db.Connections.AsNoTracking(), a => a.ConnectionId, c => c.Id,
                (a, c) => new { a.AccountNumber, a.ConnectionId, a.Currency, a.Mfo, a.Name, a.ExternalId, ConnectionName = c.Name })
            .ToListAsync(token), ct);

        foreach (var s in parsed.Statements)
        {
            var masked = Mask(s.AccountNumber);
            var own = known.FirstOrDefault(k => k.AccountNumber == s.AccountNumber && k.ConnectionId == connection.Id);
            var other = known.FirstOrDefault(k => k.AccountNumber == s.AccountNumber && k.ConnectionId != connection.Id);
            if (own is null && other is not null)
            {
                plan.Errors.Add($"Account {masked} is already loaded by connection '{other.ConnectionName}'; importing a statement for it into " +
                                $"'{connection.Name}' would count its operations twice. Import it into that connection's account only when it has no other source.");
                continue;
            }
            if (own is null && !request.CreateAccounts)
            {
                plan.Errors.Add($"Account {masked} is not known for this client. Check the file, or import with --create-accounts to create it.");
                continue;
            }

            var currencyText = s.Currency ?? request.Profile.Currency
                ?? (s.AccountNumber.Length == 20 && IsoCurrency.IsKnownNumeric(s.AccountNumber.Substring(5, 3)) ? s.AccountNumber.Substring(5, 3) : null);
            var currency = IsoCurrency.Normalize(currencyText);
            if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            {
                plan.Errors.Add(string.IsNullOrEmpty(currency)
                    ? $"The currency of account {masked} is unknown: the file has none; set \"currency\" in the profile."
                    : $"The currency \"{currency}\" of account {masked} is not a known ISO 4217 code.");
                continue;
            }
            if (own is not null && !string.IsNullOrEmpty(own.Currency) && own.Currency != currency)
            {
                plan.Errors.Add($"Account {masked} is in {own.Currency}, the file says {currency}.");
                continue;
            }

            var p = BuildStatement(s, connection, currency, own is not null, own?.Mfo, own?.Name, own?.ExternalId, request, warnings, plan.Errors);
            if (p is null) continue;
            plan.Statements.Add(p);
            if (p.Check.PeriodFrom < plan.From) plan.From = p.Check.PeriodFrom;
            if (p.Check.PeriodTo > plan.To) plan.To = p.Check.PeriodTo;
        }

        var failed = plan.Statements.Where(p => p.Check.BalanceCheck == BalanceCheckResult.Failed).ToList();
        foreach (var f in failed)
        {
            var c = f.Check;
            var text = $"The balances of account {Mask(c.AccountNumber)} for {c.PeriodFrom:yyyy-MM-dd} - {c.PeriodTo:yyyy-MM-dd} do not add up: " +
                       $"opening {StatementAmounts.Format(c.OpeningBalance!.Value)} + credit {StatementAmounts.Format(c.TotalCredit)} - debit " +
                       $"{StatementAmounts.Format(c.TotalDebit)} = {StatementAmounts.Format(c.ComputedClosing!.Value)}, but the closing balance is " +
                       $"{StatementAmounts.Format(c.ClosingBalance!.Value)} (difference {StatementAmounts.Format(c.ComputedClosing.Value - c.ClosingBalance.Value)}).";
            if (request.Force) warnings.Add(new StatementWarning("balance_mismatch_forced", text + " Imported anyway (--force); balance_check = Failed."));
            else plan.Errors.Add(text + " The file was not imported; check that it is complete, or import it with --force.");
        }
        return plan;
    }

    private StatementPlan? BuildStatement(
        ParsedStatement s, Connection connection, string currency, bool exists, string? knownMfo, string? knownName, string? knownExternalId,
        StatementImportRequest request, List<StatementWarning> warnings, List<string> errors)
    {
        var account = s.AccountNumber;
        var ops = s.Operations;
        var from = s.PeriodFrom ?? (ops.Count > 0 ? ops.Min(o => o.PostingDate) : s.Closing?.Date ?? s.Opening?.Date);
        var to = s.PeriodTo ?? (ops.Count > 0 ? ops.Max(o => o.PostingDate) : s.Closing?.Date ?? s.Opening?.Date);
        if (from is null || to is null)
        {
            errors.Add($"The statement of account {Mask(account)} has neither operations nor balances.");
            return null;
        }
        var outside = ops.Where(o => o.PostingDate < from || o.PostingDate > to).ToList();
        if (outside.Count > 0)
        {
            warnings.Add(new StatementWarning("operations_outside_period",
                $"{outside.Count} operation(s) of account {Mask(account)} are dated outside the statement period {from:yyyy-MM-dd} - {to:yyyy-MM-dd}; the period was widened.",
                outside[0].Line));
            from = new[] { from.Value, outside.Min(o => o.PostingDate) }.Min();
            to = new[] { to.Value, outside.Max(o => o.PostingDate) }.Max();
        }
        if (to.Value.DayNumber - from.Value.DayNumber + 1 > options.Value.MaxPeriodDays)
        {
            errors.Add($"The statement of account {Mask(account)} covers more than {options.Value.MaxPeriodDays} days.");
            return null;
        }

        // Operation ids: the file's own id when the profile trusts it, otherwise a deterministic hash with the ordinal
        // among identical rows of this statement (so identical payments on one day stay separate, and overlapping files
        // produce the same ids).
        var infos = new List<BankTransactionInfo>(ops.Count);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var o in ops)
        {
            var normalizedPurpose = StatementText.NormalizeForHash(o.Purpose);
            var key = JsonHasher.HashParts(account, o.PostingDate.ToString("yyyy-MM-dd"), StatementText.Invariant(o.Amount),
                ((int)o.Direction).ToString(System.Globalization.CultureInfo.InvariantCulture), o.DocumentNumber ?? "", normalizedPurpose,
                o.IsReversal ? "R" : "");
            var ordinal = ordinals.TryGetValue(key, out var seen) ? seen + 1 : 0;
            ordinals[key] = ordinal;

            var weak = o.FileOperationId is null;
            // The posting date leads the computed id: operations of one day sit next to each other in the unique index
            // (and in the raw layer), so a large import touches few index pages.
            var id = weak
                ? $"fh:{o.PostingDate:yyyyMMdd}:" + JsonHasher.HashParts(key, ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : "f:" + Cut(o.FileOperationId!, 120);
            var details = new Dictionary<string, object?> { ["import"] = "statement_file", ["format"] = Name(request.Format) };
            if (weak) details["weak_operation_id"] = true;
            if (o.IsReversal) details["reversal"] = true;
            if (o.TransactionType is { } type) details["transaction_type"] = type;
            var raw = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["format"] = Name(request.Format),
                ["account"] = account,
                ["reversal"] = o.IsReversal,
                ["fields"] = o.Source,
            }, RawJson);

            infos.Add(new BankTransactionInfo(
                OperationId: id,
                Amount: o.Amount,
                Currency: currency,
                Direction: o.Direction,
                CounterpartyInn: o.CounterpartyInn,
                CounterpartyName: o.CounterpartyName,
                CounterpartyAccount: o.CounterpartyAccount,
                CounterpartyMfo: o.CounterpartyMfo,
                Purpose: o.Purpose,
                PurposeCode: o.PurposeCode,
                DocumentNumber: o.DocumentNumber,
                PostingDate: o.PostingDate,
                ValueDate: o.ValueDate,
                SourceState: o.IsReversal ? "reversal" : null,
                ErrorCode: null,
                ErrorMessage: null,
                Raw: new RawRecord(TransactionObjectType, account + ":" + id, raw))
            {
                IdSource = weak ? OperationIdSource.FileHash : OperationIdSource.File,
                Details = JsonSerializer.Serialize(details, RawJson),
            });
        }

        // Day balances: from the opening balance through every day of the period; the days of a file are closed.
        long credit = ops.Where(o => o.Direction == TransactionDirection.Incoming).Sum(o => o.Amount);
        long debit = ops.Where(o => o.Direction == TransactionDirection.Outgoing).Sum(o => o.Amount);
        var days = new List<BankBalanceInfo>();
        BalanceCheckResult check;
        long? computed = null;
        if (s.Opening is { } opening && s.Closing is { } closing)
        {
            var byDay = ops.GroupBy(o => o.PostingDate).ToDictionary(g => g.Key, g => (
                Credit: g.Where(o => o.Direction == TransactionDirection.Incoming).Sum(o => o.Amount),
                Debit: g.Where(o => o.Direction == TransactionDirection.Outgoing).Sum(o => o.Amount)));
            var running = opening.Amount;
            for (var day = from.Value; day <= to.Value; day = day.AddDays(1))
            {
                var (c, d) = byDay.GetValueOrDefault(day);
                var dayOpening = running;
                running = dayOpening + c - d;
                // The bank's closing balance is kept on the last day even when it does not add up (forced import), so the
                // stored day shows the discrepancy and the bank.balance_mismatch rule reports it.
                var dayClosing = day == to.Value ? closing.Amount : running;
                days.Add(new BankBalanceInfo(account, day, dayOpening, dayClosing, d, c, true,
                    new RawRecord(DayBalanceObjectType, $"{account}:{day:yyyy-MM-dd}", JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["source"] = "statement_file",
                        ["opening_balance"] = dayOpening,
                        ["closing_balance"] = dayClosing,
                        ["total_debit"] = d,
                        ["total_credit"] = c,
                    }, RawJson))));
            }
            computed = running;
            check = running == closing.Amount ? BalanceCheckResult.Passed : BalanceCheckResult.Failed;
        }
        else
        {
            check = BalanceCheckResult.Skipped;
            warnings.Add(new StatementWarning("no_balances",
                $"The statement of account {Mask(account)} has no {(s.Opening is null && s.Closing is null ? "opening and closing balances" : s.Opening is null ? "opening balance" : "closing balance")}: " +
                "the balance check was skipped and no day balances were stored."));
        }

        var mfo = s.BankCode is { } bank && StatementText.Digits(bank) is { Length: <= Limits.Mfo } digits && digits.Length == bank.Trim().Length
            ? digits
            : knownMfo ?? (string.IsNullOrEmpty(s.BankCode) ? "" : Cut(s.BankCode, Limits.Mfo));
        var accountJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["source"] = "statement_file",
            ["account"] = account,
            ["bank_code"] = s.BankCode,
            ["currency"] = currency,
        }, RawJson);
        var accountInfo = new BankAccountInfo(account, mfo, currency, knownName, knownExternalId, new RawRecord(AccountObjectType, account, accountJson));

        return new StatementPlan
        {
            Account = account,
            AccountInfo = accountInfo,
            Infos = infos,
            Days = days,
            Check = new StatementCheck(account, s.BankCode, currency, from.Value, to.Value, s.Opening?.Amount, s.Closing?.Amount, credit, debit,
                computed, infos.Count, check, exists),
        };
    }

    private async Task<int> CountExistingAsync(ImportPlan plan, CancellationToken ct) =>
        await db.InTransactionAsync(async token =>
        {
            var count = 0;
            foreach (var p in plan.Statements.Where(p => p.Check.AccountExists))
            {
                var accountId = await db.BankAccounts.AsNoTracking().Where(a => a.AccountNumber == p.Account).Select(a => a.Id).FirstAsync(token);
                foreach (var chunk in p.Infos.Select(i => i.OperationId).Distinct().Chunk(5000))
                {
                    var ids = chunk.ToList();
                    count += await db.BankTransactions.AsNoTracking().CountAsync(t => t.BankAccountId == accountId && ids.Contains(t.OperationId), token);
                }
            }
            return count;
        }, ct);

    private async Task<StatementImportResult> RejectAsync(
        StatementImportRequest request, Connection connection, string sha, string? encoding, IReadOnlyList<StatementCheck> checks,
        IReadOnlyList<StatementWarning> warnings, int rejectedRows, string message, CancellationToken ct)
    {
        Guid? runId = null;
        if (!request.DryRun)
        {
            var from = checks.Count > 0 ? checks.Min(c => c.PeriodFrom) : (DateOnly?)null;
            var to = checks.Count > 0 ? checks.Max(c => c.PeriodTo) : (DateOnly?)null;
            runId = await RecordFailedRunAsync(connection, from, to, message, checks.Sum(c => c.Operations));
        }
        logger.LogInformation("Statement import rejected: connection {ConnectionId}, file {Sha}, dry run {DryRun}",
            connection.Id, sha[..12], request.DryRun);
        return new StatementImportResult(StatementImportStatus.Rejected, message, sha, encoding, checks, warnings, checks.Sum(c => c.Operations),
            0, 0, rejectedRows, Overall(checks), SyncRunId: runId);
    }

    /// <summary>
    /// A rejected or failed import is a failed run of the connection (visible in sync_runs and freshness). The circuit
    /// breaker is not fed: a bad file says nothing about the connection itself.
    /// </summary>
    private Task<Guid> RecordFailedRunAsync(Connection connection, DateOnly? from, DateOnly? to, string error, int records) =>
        db.InTransactionAsync(async token =>
        {
            var now = time.GetUtcNow();
            var run = SyncRun.Start(connection.TenantId, connection.Id, now,
                from is { } f && f != DateOnly.MaxValue ? f : null, to is { } t && t != DateOnly.MinValue ? t : null);
            run.Fail(now, error, records);
            db.SyncRuns.Add(run);
            await db.SaveChangesAsync(token);
            return run.Id;
        }, CancellationToken.None);

    private static BalanceCheckResult Overall(IReadOnlyCollection<StatementCheck> checks) =>
        checks.Any(c => c.BalanceCheck == BalanceCheckResult.Failed) ? BalanceCheckResult.Failed
        : checks.Any(c => c.BalanceCheck == BalanceCheckResult.Passed) ? BalanceCheckResult.Passed
        : BalanceCheckResult.Skipped;

    private static async Task<byte[]> ReadAsync(Stream content, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max) throw new ArgumentException($"The file is larger than Statements:MaxFileBytes ({max} bytes).");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static string Mask(string account) => account.Length <= 4 ? "****" : "****" + account[^4..];

    private static string Name(StatementFormat format) => format == StatementFormat.Mt940 ? "MT940" : "CSV";

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}
