using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Analytics.Totals;
using Synect.Application.Attention;
using Synect.Application.Common;
using Synect.Application.Reconciliation;
using Synect.Application.Resilience;
using Synect.Application.Secrets;
using Synect.Domain.Banking;
using Synect.Domain.Counterparties;
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
    BankLoadWriter writer,
    ICredentialProvider credentials,
    AdapterCallPolicy policy,
    AttentionTrigger attention,
    TimeProvider time,
    IOptions<BankSyncOptions> bankOptions,
    IOptions<CircuitBreakerOptions> breakerOptions,
    Synect.Application.Queue.SyncShutdown shutdown,
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
            var accountList = await policy.ExecuteAsync(adapter, context, "GetAccounts",
                token => adapter.GetAccountsAsync(context, token), ct);
            var accountInfos = accountList.Accounts;
            records += accountInfos.Count;
            // Accounts the adapter could not read are left out of this load (stored accounts are kept as they are); the
            // other accounts still load, and the run is not complete.
            var accountsRejected = accountList.Rejected.Count > 0;
            if (accountsRejected)
                logger.LogWarning("Bank sync: {Count} account(s) could not be read and were left out, connection {ConnectionId}: {Reasons}",
                    accountList.Rejected.Count, connection.Id, string.Join("; ", accountList.Rejected));
            var accounts = await db.InTransactionAsync(async token =>
            {
                var result = await writer.UpsertAccountsAsync(connection, accountInfos, token);
                await db.SaveChangesAsync(token);
                return result;
            }, ct);

            var allDaysClosed = adapter.Capabilities.HasFinalityFlag && !accountsRejected;
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
                    // One bank request per account and day: operations and totals from the same answer.
                    var (dayTransactions, balance) = await policy.ExecuteAsync(adapter, context, "GetDay",
                        token => adapter.GetDayAsync(context, account.AccountNumber, d, token), ct);
                    // Operations the adapter could not read are left out; the day stays open (not final) so that the
                    // next load reads it again, and the run is not complete.
                    var rejected = dayTransactions.Rejected.Count > 0;
                    if (rejected)
                    {
                        logger.LogWarning("Bank sync: {Count} operation(s) of {Day} could not be read and were left out, connection {ConnectionId}, account id {AccountId}: {Reasons}",
                            dayTransactions.Rejected.Count, d, connection.Id, account.Id, string.Join("; ", dayTransactions.Rejected.Select(r => r.Reason)));
                        if (balance is not null) balance = balance with { IsDayClosed = false };
                    }
                    var dayClosed = adapter.Capabilities.HasFinalityFlag && !rejected
                        && (balance?.IsDayClosed ?? dayTransactions.IsDayClosed ?? false);
                    allDaysClosed &= dayClosed;
                    records += dayTransactions.Transactions.Count + (balance is null ? 0 : 1);

                    await db.InTransactionAsync(async token =>
                    {
                        var items = adapter.Capabilities.HasStableOperationId
                            ? dayTransactions.Transactions
                            : dayTransactions.Transactions.Select(i => i with
                            {
                                OperationId = SyntheticOperationId(account, i), IdSource = OperationIdSource.Synthetic,
                            }).ToList();
                        await writer.UpsertTransactionsAsync(connection, account, items, dayClosed, token);
                        if (balance is not null)
                            await writer.UpsertBalanceAsync(connection, account, balance, token);
                        if (dayTransactions.RejectedObjectType is { } rejectedType)
                            await writer.ReplaceRejectedOperationsAsync(connection, account, d, rejectedType, dayTransactions.Rejected, token);
                        await db.SaveChangesAsync(token);
                    }, ct);
                }
            }

            await db.InTransactionAsync(async token =>
            {
                var now = time.GetUtcNow();
                if (explicitRange is null && rangeFrom is { } f && rangeTo is { } t) run.SetRange(f, t);
                // Monthly totals and reconciliation of exactly what this load touched, atomically with success.
                await writer.CompleteAsync(connection, token);
                run.Succeed(now, allDaysClosed, records);
                connection.MarkSyncSucceeded(now);
                await db.SaveChangesAsync(token);
            }, ct);

            // Attention rules over the new data (own scope and transaction; never fails the load, hence no cancellation token).
            await attention.AfterLoadAsync(AdapterKind.Bank, succeeded: true, CancellationToken.None);
            logger.LogInformation("Bank sync succeeded: connection {ConnectionId}, records {Records}, complete {Complete}",
                connection.Id, records, allDaysClosed);
            return new SyncOutcome(run.Id, run.Result, run.IsComplete, records, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Bank sync failed: connection {ConnectionId}, error type {ErrorType}",
                connection.Id, ex.GetType().Name);
            var outcome = await SyncRunFinalizer.FailAsync(db, run.Id, connection.Id, ex, records, time.GetUtcNow(),
                breakerOptions.Value.FailureThreshold,
                interruptedByShutdown: ex is OperationCanceledException && shutdown.JobsCancelled);
            if (ex is OperationCanceledException) throw;
            await attention.AfterLoadAsync(AdapterKind.Bank, succeeded: false, CancellationToken.None);
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

    /// <summary>Fallback id for banks without stable operation ids: hash of the identifying fields.</summary>
    private static string SyntheticOperationId(BankAccount account, BankTransactionInfo i) =>
        "syn:" + JsonHasher.HashParts(
            account.AccountNumber, i.PostingDate.ToString("O"), i.Amount.ToString(), ((int)i.Direction).ToString(),
            i.CounterpartyAccount, i.CounterpartyMfo, i.DocumentNumber, i.Purpose);
}
