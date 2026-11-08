using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Analytics.Totals;
using Synect.Application.Common;
using Synect.Application.Resilience;
using Synect.Application.Secrets;
using Synect.Domain.Banking;
using Synect.Domain.Sync;

namespace Synect.Application.Sync;

/// <summary>Sync:Bank.</summary>
public sealed class BankSyncOptions
{
    /// <summary>Days to load on the first sync of an account (capped by the adapter history depth).</summary>
    public int InitialDays { get; set; } = 30;

    /// <summary>UTC offset of the bank operational day. Uzbekistan is UTC+5 without DST.</summary>
    public TimeSpan OperationalDayUtcOffset { get; set; } = TimeSpan.FromHours(5);
}

/// <summary>
/// Loads accounts, operations and daily balances of one bank connection into the database.
/// Every step is idempotent: re-running the same range produces no duplicates.
/// </summary>
public sealed class BankSyncService(
    ISynectDbContext db,
    IAdapterRegistry registry,
    RawObjectStore rawStore,
    CounterpartyStore counterparties,
    ICredentialProvider credentials,
    AdapterCallPolicy policy,
    AnalyticsChangeSet changes,
    CounterpartyMonthlyRefresher monthly,
    TimeProvider time,
    IOptions<BankSyncOptions> bankOptions,
    IOptions<CircuitBreakerOptions> breakerOptions,
    ILogger<BankSyncService> logger)
{
    /// <summary>
    /// Incremental load. Per account: the first load covers InitialDays (capped by HistoryDepthDays);
    /// later loads re-read from the earliest day that is not closed yet (bank_daily_balances.is_day_closed = false)
    /// up to today, or only yesterday and today when every stored day is closed. If the worker did not run
    /// for a while, reading also starts right after the latest stored day so that no day is skipped.
    /// </summary>
    public Task<SyncOutcome> SyncAsync(Guid connectionId, CancellationToken ct) =>
        RunAsync(connectionId, null, ct);

    /// <summary>Loads an explicit range for every account (manual re-reads, tests).</summary>
    public Task<SyncOutcome> SyncAsync(Guid connectionId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from) throw new ArgumentException("Range end is before range start.", nameof(to));
        return RunAsync(connectionId, (from, to), ct);
    }

    /// <summary>Today in the bank operational time zone.</summary>
    public DateOnly Today() =>
        DateOnly.FromDateTime(time.GetUtcNow().ToOffset(bankOptions.Value.OperationalDayUtcOffset).DateTime);

    private async Task<SyncOutcome> RunAsync(Guid connectionId, (DateOnly From, DateOnly To)? explicitRange, CancellationToken ct)
    {
        var (connection, run) = await db.InTransactionAsync(async token =>
        {
            var c = await db.Connections.SingleOrDefaultAsync(x => x.Id == connectionId, token)
                ?? throw new KeyNotFoundException($"Connection {connectionId} not found for the current tenant.");
            if (c.AdapterKind != AdapterKind.Bank)
                throw new InvalidOperationException($"Connection {connectionId} is not a bank connection.");
            if (c.Status == ConnectionStatus.Disabled)
                throw new InvalidOperationException($"Connection {connectionId} is disabled.");

            var r = SyncRun.Start(c.TenantId, c.Id, time.GetUtcNow(), explicitRange?.From, explicitRange?.To);
            db.SyncRuns.Add(r);
            await db.SaveChangesAsync(token);
            return (c, r);
        }, ct);

        var adapter = registry.GetBank(connection.SourceCode);
        var context = SyncRunFinalizer.CreateAdapterContext(connection, credentials);
        var records = 0;

        logger.LogInformation("Bank sync started: connection {ConnectionId}, source {SourceCode}", connection.Id, connection.SourceCode);

        try
        {
            var accountInfos = await policy.ExecuteAsync(adapter, context, "GetAccounts",
                token => adapter.GetAccountsAsync(context, token), ct);
            records += accountInfos.Count;
            var accounts = await db.InTransactionAsync(async token =>
            {
                var result = await UpsertAccountsAsync(connection, accountInfos, token);
                await db.SaveChangesAsync(token);
                return result;
            }, ct);

            var allDaysClosed = adapter.Capabilities.HasFinalityFlag;
            DateOnly? rangeFrom = null, rangeTo = null;
            foreach (var account in accounts)
            {
                var (from, to) = explicitRange
                    ?? await db.InTransactionAsync(token => RangeForAsync(connection, account, adapter.Capabilities, token), ct);
                rangeFrom = rangeFrom is { } rf && rf < from ? rf : from;
                rangeTo = rangeTo is { } rt && rt > to ? rt : to;

                for (var day = from; day <= to; day = day.AddDays(1))
                {
                    var d = day;
                    var dayTransactions = await policy.ExecuteAsync(adapter, context, "GetTransactions",
                        token => adapter.GetTransactionsAsync(context, account.AccountNumber, d, token), ct);
                    var balance = await policy.ExecuteAsync(adapter, context, "GetBalances",
                        token => adapter.GetBalancesAsync(context, account.AccountNumber, d, token), ct);
                    var dayClosed = adapter.Capabilities.HasFinalityFlag
                        && (balance?.IsDayClosed ?? dayTransactions.IsDayClosed ?? false);
                    allDaysClosed &= dayClosed;
                    records += dayTransactions.Transactions.Count + (balance is null ? 0 : 1);

                    await db.InTransactionAsync(async token =>
                    {
                        await UpsertTransactionsAsync(connection, account, dayTransactions.Transactions,
                            dayClosed, adapter.Capabilities, token);
                        if (balance is not null)
                            await UpsertBalanceAsync(connection, account, balance, token);
                        await db.SaveChangesAsync(token);
                    }, ct);
                }
            }

            await db.InTransactionAsync(async token =>
            {
                var now = time.GetUtcNow();
                if (explicitRange is null && rangeFrom is { } f && rangeTo is { } t) run.SetRange(f, t);
                // Precomputed totals for exactly the (INN, month) pairs this load touched, atomically with success.
                await monthly.RefreshAsync(changes.Keys, token);
                changes.Clear();
                run.Succeed(now, allDaysClosed, records);
                connection.MarkSyncSucceeded(now);
                await db.SaveChangesAsync(token);
            }, ct);

            logger.LogInformation("Bank sync succeeded: connection {ConnectionId}, records {Records}, complete {Complete}",
                connection.Id, records, allDaysClosed);
            return new SyncOutcome(run.Id, run.Result, run.IsComplete, records, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Bank sync failed: connection {ConnectionId}, error type {ErrorType}",
                connection.Id, ex.GetType().Name);
            var outcome = await SyncRunFinalizer.FailAsync(db, run.Id, connection.Id, ex, records, time.GetUtcNow(),
                breakerOptions.Value.FailureThreshold);
            if (ex is OperationCanceledException) throw;
            return outcome;
        }
    }

    private async Task<(DateOnly From, DateOnly To)> RangeForAsync(
        Connection connection, BankAccount account, BankAdapterCapabilities capabilities, CancellationToken ct)
    {
        var today = Today();
        var initialDays = bankOptions.Value.InitialDays;
        if (capabilities.HistoryDepthDays is { } depth) initialDays = Math.Min(initialDays, depth);
        var initialFrom = today.AddDays(-Math.Max(1, initialDays) + 1);

        var balances = db.BankDailyBalances.Where(b => b.BankAccountId == account.Id);
        var latestStored = await balances.Select(b => (DateOnly?)b.Date).MaxAsync(ct);
        if (connection.LastSuccessfulSyncAt is null || latestStored is null)
            return (initialFrom, today);

        // Default: yesterday and today.
        var from = today.AddDays(-1);
        // Re-read from the earliest day the bank has not closed yet.
        var earliestOpen = await balances.Where(b => !b.IsDayClosed).Select(b => (DateOnly?)b.Date).MinAsync(ct);
        if (earliestOpen is { } open && open < from) from = open;
        // Gap protection: never skip days after the latest stored one (worker was down).
        var afterLatest = latestStored.Value.AddDays(1);
        if (afterLatest < from) from = afterLatest;
        // Do not ask for more history than the bank keeps.
        if (capabilities.HistoryDepthDays is { } maxDepth && from < today.AddDays(-maxDepth + 1))
            from = today.AddDays(-maxDepth + 1);
        return (from, today);
    }

    private async Task<List<BankAccount>> UpsertAccountsAsync(
        Connection connection, IReadOnlyList<BankAccountInfo> infos, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await rawStore.UpsertAsync(connection, infos.Select(i => i.Raw).ToList(), now, ct);

        var existing = await db.BankAccounts
            .Where(a => a.ConnectionId == connection.Id)
            .ToDictionaryAsync(a => a.AccountNumber, ct);

        var result = new List<BankAccount>();
        foreach (var info in infos.DistinctBy(i => i.AccountNumber))
        {
            if (!existing.TryGetValue(info.AccountNumber, out var account))
            {
                account = new BankAccount
                {
                    TenantId = connection.TenantId,
                    ConnectionId = connection.Id,
                    AccountNumber = info.AccountNumber,
                    CreatedAt = now,
                };
                db.BankAccounts.Add(account);
            }

            if (account.Mfo != info.Mfo || account.Currency != info.Currency
                || account.Name != info.Name || account.ExternalId != info.ExternalId)
            {
                account.Mfo = info.Mfo;
                account.Currency = info.Currency;
                account.Name = info.Name;
                account.ExternalId = info.ExternalId;
                account.UpdatedAt = now;
            }
            result.Add(account);
        }
        return result;
    }

    private async Task UpsertTransactionsAsync(
        Connection connection,
        BankAccount account,
        IReadOnlyList<BankTransactionInfo> infos,
        bool dayClosed,
        BankAdapterCapabilities capabilities,
        CancellationToken ct)
    {
        if (infos.Count == 0) return;
        var now = time.GetUtcNow();

        // Last occurrence wins when the bank repeats an operation in the same response.
        var items = infos
            .Select(i => capabilities.HasStableOperationId ? i : i with { OperationId = SyntheticOperationId(account, i) })
            .GroupBy(i => i.OperationId)
            .Select(g => g.Last())
            .ToList();

        var hashes = await rawStore.UpsertAsync(connection, items.Select(i => i.Raw).ToList(), now, ct);

        var ids = items.Select(i => i.OperationId).ToList();
        var existing = await db.BankTransactions
            .Where(t => t.BankAccountId == account.Id && ids.Contains(t.OperationId))
            .ToDictionaryAsync(t => t.OperationId, StringComparer.Ordinal, ct);

        for (var i = 0; i < items.Count; i++)
        {
            var info = items[i];
            var hash = hashes[i];
            if (existing.TryGetValue(info.OperationId, out var tx))
            {
                var isFinal = tx.IsFinal || dayClosed;
                if (tx.SourceHash == hash && tx.IsFinal == isFinal) continue;
                changes.Add(tx.CounterpartyInn, tx.PostingDate);
                changes.Add(info.CounterpartyInn, info.PostingDate);
                Apply(tx, info, account, hash, isFinal);
                tx.UpdatedAt = now;
                continue;
            }

            tx = new BankTransaction
            {
                TenantId = connection.TenantId,
                ConnectionId = connection.Id,
                BankAccountId = account.Id,
                OperationId = info.OperationId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            Apply(tx, info, account, hash, dayClosed);
            db.BankTransactions.Add(tx);
            changes.Add(info.CounterpartyInn, info.PostingDate);
        }

        await counterparties.UpsertAsync(connection.TenantId,
            items.Select(i => (i.CounterpartyInn, i.CounterpartyName)), now, ct);
    }

    private static void Apply(BankTransaction tx, BankTransactionInfo info, BankAccount account, string hash, bool isFinal)
    {
        tx.Amount = info.Amount;
        // An operation is in the currency of its account; the account currency comes from the bank's "val".
        tx.Currency = string.IsNullOrEmpty(account.Currency) ? info.Currency : account.Currency;
        tx.Direction = info.Direction;
        tx.CounterpartyInn = info.CounterpartyInn;
        tx.CounterpartyName = info.CounterpartyName;
        tx.CounterpartyAccount = info.CounterpartyAccount;
        tx.CounterpartyMfo = info.CounterpartyMfo;
        tx.Purpose = info.Purpose;
        tx.PurposeCode = info.PurposeCode;
        tx.DocumentNumber = info.DocumentNumber;
        tx.PostingDate = info.PostingDate;
        tx.ValueDate = info.ValueDate;
        tx.SourceState = info.SourceState;
        tx.HasError = info.HasError;
        tx.ErrorCode = info.ErrorCode;
        tx.ErrorMessage = info.ErrorMessage;
        tx.SourceHash = hash;
        tx.IsFinal = isFinal;
    }

    private async Task UpsertBalanceAsync(
        Connection connection, BankAccount account, BankBalanceInfo info, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await rawStore.UpsertAsync(connection, [info.Raw], now, ct);

        var balance = await db.BankDailyBalances
            .SingleOrDefaultAsync(b => b.BankAccountId == account.Id && b.Date == info.Day, ct);
        if (balance is null)
        {
            balance = new BankDailyBalance
            {
                TenantId = connection.TenantId,
                BankAccountId = account.Id,
                Date = info.Day,
            };
            db.BankDailyBalances.Add(balance);
        }
        else if (balance.OpeningBalance == info.OpeningBalance && balance.ClosingBalance == info.ClosingBalance
                 && balance.TotalDebit == info.TotalDebit && balance.TotalCredit == info.TotalCredit
                 && balance.IsDayClosed == info.IsDayClosed)
        {
            return;
        }

        balance.OpeningBalance = info.OpeningBalance;
        balance.ClosingBalance = info.ClosingBalance;
        balance.TotalDebit = info.TotalDebit;
        balance.TotalCredit = info.TotalCredit;
        balance.IsDayClosed = info.IsDayClosed;
        balance.UpdatedAt = now;
    }

    /// <summary>Fallback id for banks without stable operation ids: hash of the identifying fields.</summary>
    private static string SyntheticOperationId(BankAccount account, BankTransactionInfo i) =>
        "syn:" + JsonHasher.HashParts(
            account.AccountNumber, i.PostingDate.ToString("O"), i.Amount.ToString(), ((int)i.Direction).ToString(),
            i.CounterpartyAccount, i.CounterpartyMfo, i.DocumentNumber, i.Purpose);
}
