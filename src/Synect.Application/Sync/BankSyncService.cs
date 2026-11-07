using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Common;
using Synect.Domain.Banking;
using Synect.Domain.Sync;

namespace Synect.Application.Sync;

/// <summary>
/// Loads accounts, operations and daily balances of one bank connection into the database.
/// Every step is idempotent: re-running the same range produces no duplicates.
/// </summary>
public sealed class BankSyncService(
    ISynectDbContext db,
    IAdapterRegistry registry,
    RawObjectStore rawStore,
    CounterpartyStore counterparties,
    TimeProvider time,
    ILogger<BankSyncService> logger)
{
    public async Task<SyncOutcome> SyncAsync(Guid connectionId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from) throw new ArgumentException("Range end is before range start.", nameof(to));

        var (connection, run) = await db.InTransactionAsync(async token =>
        {
            var c = await db.Connections.SingleOrDefaultAsync(x => x.Id == connectionId, token)
                ?? throw new KeyNotFoundException($"Connection {connectionId} not found for the current tenant.");
            if (c.AdapterKind != AdapterKind.Bank)
                throw new InvalidOperationException($"Connection {connectionId} is not a bank connection.");
            if (c.Status == ConnectionStatus.Disabled)
                throw new InvalidOperationException($"Connection {connectionId} is disabled.");

            var r = SyncRun.Start(c.TenantId, c.Id, time.GetUtcNow(), from, to);
            db.SyncRuns.Add(r);
            await db.SaveChangesAsync(token);
            return (c, r);
        }, ct);

        var adapter = registry.GetBank(connection.SourceCode);
        var context = SyncRunFinalizer.CreateAdapterContext(connection);
        var records = 0;

        logger.LogInformation("Bank sync started: connection {ConnectionId}, source {SourceCode}, {From}..{To}",
            connection.Id, connection.SourceCode, from, to);

        try
        {
            var accountInfos = await adapter.GetAccountsAsync(context, ct);
            records += accountInfos.Count;
            var accounts = await db.InTransactionAsync(async token =>
            {
                var result = await UpsertAccountsAsync(connection, accountInfos, token);
                await db.SaveChangesAsync(token);
                return result;
            }, ct);

            var allDaysClosed = adapter.Capabilities.HasFinalityFlag;
            foreach (var account in accounts)
            {
                for (var day = from; day <= to; day = day.AddDays(1))
                {
                    var dayTransactions = await adapter.GetTransactionsAsync(context, account.AccountNumber, day, ct);
                    var balance = await adapter.GetBalancesAsync(context, account.AccountNumber, day, ct);
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
            var outcome = await SyncRunFinalizer.FailAsync(db, run.Id, connection.Id, ex, records, time.GetUtcNow());
            if (ex is OperationCanceledException) throw;
            return outcome;
        }
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
