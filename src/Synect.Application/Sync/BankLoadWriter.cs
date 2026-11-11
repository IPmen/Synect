using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Analytics.Totals;
using Synect.Application.Reconciliation;
using Synect.Domain.Banking;
using Synect.Domain.Counterparties;
using Synect.Domain.Sync;

namespace Synect.Application.Sync;

/// <summary>What an upsert of operations did: inserted, changed (existing id, new content) and unchanged rows.</summary>
public sealed record TransactionUpsertCounts(int New, int Changed, int Unchanged)
{
    public int Existing => Changed + Unchanged;
}

/// <summary>
/// Writes bank data of the common model (accounts, operations, day balances) into the database, idempotently, for every
/// bank source: the API sync (<see cref="BankSyncService"/>) and statement file imports. Covers the raw layer, operations
/// keyed by (account, operation id), counterparties and their name spellings, and the analytics change set;
/// <see cref="CompleteAsync"/> refreshes the monthly totals and the reconciliation of the touched INNs.
/// Works inside the caller's transaction and never calls SaveChanges.
/// </summary>
public sealed class BankLoadWriter(
    ISynectDbContext db,
    RawObjectStore rawStore,
    CounterpartyStore counterparties,
    AnalyticsChangeSet changes,
    CounterpartyMonthlyRefresher monthly,
    ReconciliationService reconciliation,
    AliasVolumeMonitor aliasMonitor,
    IOptions<ReconciliationOptions> reconciliationOptions,
    IOptions<BankSyncOptions> bankOptions,
    LoadProfile profile,
    TimeProvider time)
{
    public async Task<List<BankAccount>> UpsertAccountsAsync(
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
                existing[info.AccountNumber] = account;
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

    /// <summary>
    /// Upserts operations of one account. <paramref name="infos"/> must carry final operation ids (the caller decides how
    /// ids are made); the last occurrence of an id wins. An existing operation is rewritten only when its raw payload or
    /// its finality changed.
    /// </summary>
    public async Task<TransactionUpsertCounts> UpsertTransactionsAsync(
        Connection connection, BankAccount account, IReadOnlyList<BankTransactionInfo> infos, bool dayClosed, CancellationToken ct)
    {
        if (infos.Count == 0) return new TransactionUpsertCounts(0, 0, 0);
        var now = time.GetUtcNow();
        var bulk = UseBulk;

        var items = infos.GroupBy(i => i.OperationId, StringComparer.Ordinal).Select(g => g.Last()).ToList();
        IReadOnlyList<string> hashes;
        using (profile.Measure("raw_layer"))
        {
            var raw = items.Select(i => i.Raw).ToList();
            hashes = bulk ? await rawStore.UpsertBulkAsync(connection, raw, now, ct, RefreshUnchangedRaw) : await rawStore.UpsertAsync(connection, raw, now, ct);
        }

        // Stored operations of these ids: one query per 5000 ids. PostgreSQL: a projection (nothing tracked); the rows
        // are written below with one INSERT ... ON CONFLICT. Elsewhere (SQLite tests): tracked entities.
        var existing = new Dictionary<string, BankTransaction>(StringComparer.Ordinal);
        using (profile.Measure("existing_operations_lookup"))
        {
            foreach (var chunk in items.Select(i => i.OperationId).Chunk(5000))
            {
                var ids = chunk.ToList();
                var query = db.BankTransactions.Where(t => t.BankAccountId == account.Id && ids.Contains(t.OperationId));
                var rows = bulk
                    ? await query.AsNoTracking().Select(t => new BankTransaction
                    {
                        OperationId = t.OperationId, SourceHash = t.SourceHash, IsFinal = t.IsFinal, CounterpartyInn = t.CounterpartyInn,
                        CounterpartyName = t.CounterpartyName, PostingDate = t.PostingDate,
                    }).ToListAsync(ct)
                    : await query.ToListAsync(ct);
                foreach (var tx in rows) existing[tx.OperationId] = tx;
            }
        }
        var operations = profile.Measure("operations_upsert");

        int created = 0, changed = 0, unchanged = 0;
        var names = new List<NameSighting>();
        var toWrite = new List<BankTransaction>();
        NameSighting Seen(BankTransactionInfo x) => new(x.CounterpartyInn, x.CounterpartyName, CounterpartyNameSource.Bank,
            new DateTimeOffset(x.PostingDate.ToDateTime(TimeOnly.MinValue), bankOptions.Value.OperationalDayUtcOffset));
        for (var i = 0; i < items.Count; i++)
        {
            var info = items[i];
            var hash = hashes[i];
            if (existing.TryGetValue(info.OperationId, out var tx))
            {
                var isFinal = tx.IsFinal || dayClosed;
                if (tx.SourceHash == hash && tx.IsFinal == isFinal)
                {
                    unchanged++;
                    continue;
                }
                changed++;
                // A changed operation counts its name again only when the name itself changed.
                if (tx.CounterpartyInn != info.CounterpartyInn || tx.CounterpartyName != info.CounterpartyName) names.Add(Seen(info));
                changes.Add(tx.CounterpartyInn, tx.PostingDate);
                changes.Add(info.CounterpartyInn, info.PostingDate);
                if (bulk)
                {
                    var row = NewRow(connection, account, info, now);
                    Apply(row, info, account, hash, isFinal);
                    toWrite.Add(row);
                }
                else
                {
                    Apply(tx, info, account, hash, isFinal);
                    tx.UpdatedAt = now;
                }
                continue;
            }

            tx = NewRow(connection, account, info, now);
            Apply(tx, info, account, hash, dayClosed);
            if (bulk) toWrite.Add(tx);
            else db.BankTransactions.Add(tx);
            existing[info.OperationId] = tx;
            created++;
            changes.Add(info.CounterpartyInn, info.PostingDate);
            names.Add(Seen(info));
        }
        if (bulk && toWrite.Count > 0)
        {
            // Sorted by operation id so that neighbouring ids land on the same pages of the unique index.
            foreach (var chunk in toWrite.OrderBy(r => r.OperationId, StringComparer.Ordinal).Chunk(10_000)) await WriteRowsAsync(chunk, ct);
        }
        operations.Dispose();

        using (profile.Measure("counterparties"))
            await counterparties.UpsertAsync(connection.TenantId, items.Select(i => (i.CounterpartyInn, i.CounterpartyName)), now, ct);
        using (profile.Measure("name_spellings"))
            await counterparties.RecordNamesAsync(connection.TenantId, names, ct);
        return new TransactionUpsertCounts(created, changed, unchanged);
    }

    /// <summary>
    /// PostgreSQL bulk path: also refresh last_seen_at of unchanged raw objects (true, as the API sync always did).
    /// Statement imports turn it off (see <see cref="RawObjectStore.UpsertBulkAsync"/>).
    /// </summary>
    public bool RefreshUnchangedRaw { get; set; } = true;

    /// <summary>
    /// Write path: null (default) = bulk SQL on PostgreSQL, EF entities elsewhere. false forces the EF path (tests compare
    /// both paths on the same data); true outside PostgreSQL is refused.
    /// </summary>
    public bool? BulkWrites { get; set; }

    private bool UseBulk => BulkWrites switch
    {
        null => IsPostgres,
        false => false,
        true => IsPostgres ? true : throw new InvalidOperationException("Bulk writes need PostgreSQL."),
    };

    private bool IsPostgres => db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static BankTransaction NewRow(Connection connection, BankAccount account, BankTransactionInfo info, DateTimeOffset now) => new()
    {
        TenantId = connection.TenantId,
        ConnectionId = connection.Id,
        BankAccountId = account.Id,
        OperationId = info.OperationId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// New and changed operations in one statement. A changed row keeps its id and created_at; is_final never goes back
    /// from true to false. The account must already be saved (foreign key).
    /// </summary>
    private async Task WriteRowsAsync(IReadOnlyList<BankTransaction> rows, CancellationToken ct)
    {
        var first = rows[0];
        var at = rows.Select(r => r.UpdatedAt.ToUniversalTime()).ToArray();
        var ids = rows.Select(r => r.Id).ToArray();
        var accounts = rows.Select(r => r.BankAccountId).ToArray();
        var operations = rows.Select(r => r.OperationId).ToArray();
        var amounts = rows.Select(r => r.Amount).ToArray();
        var currencies = rows.Select(r => r.Currency).ToArray();
        var directions = rows.Select(r => r.Direction.ToString()).ToArray();
        var inns = rows.Select(r => r.CounterpartyInn).ToArray();
        var names = rows.Select(r => r.CounterpartyName).ToArray();
        var cpAccounts = rows.Select(r => r.CounterpartyAccount).ToArray();
        var mfos = rows.Select(r => r.CounterpartyMfo).ToArray();
        var purposes = rows.Select(r => r.Purpose).ToArray();
        var purposeCodes = rows.Select(r => r.PurposeCode).ToArray();
        var documents = rows.Select(r => r.DocumentNumber).ToArray();
        var posting = rows.Select(r => r.PostingDate).ToArray();
        var values = rows.Select(r => r.ValueDate).ToArray();
        var finals = rows.Select(r => r.IsFinal).ToArray();
        var states = rows.Select(r => r.SourceState).ToArray();
        var errors = rows.Select(r => r.HasError).ToArray();
        var codes = rows.Select(r => r.ErrorCode).ToArray();
        var messages = rows.Select(r => r.ErrorMessage).ToArray();
        var sources = rows.Select(r => r.IdSource.ToString()).ToArray();
        var details = rows.Select(r => r.Details).ToArray();
        var hashes = rows.Select(r => r.SourceHash).ToArray();
        using var _ = profile.Measure("operations_sql");
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO bank_transactions (id, tenant_id, connection_id, bank_account_id, operation_id, amount, currency, direction,
                counterparty_inn, counterparty_name, counterparty_account, counterparty_mfo, purpose, purpose_code, document_number,
                posting_date, value_date, is_final, source_state, has_error, error_code, error_message, id_source, details, source_hash,
                created_at, updated_at)
            SELECT u.id, {first.TenantId}, {first.ConnectionId}, u.account, u.op, u.amount, u.currency, u.direction,
                u.inn, u.name, u.cp_account, u.mfo, u.purpose, u.purpose_code, u.doc,
                u.posting, u.value_date, u.final, u.state, u.has_error, u.code, u.message, u.id_source, u.details::jsonb, u.hash,
                u.at, u.at
            FROM unnest({ids}, {accounts}, {operations}, {amounts}, {currencies}, {directions}, {inns}, {names}, {cpAccounts}, {mfos},
                {purposes}, {purposeCodes}, {documents}, {posting}, {values}, {finals}, {states}, {errors}, {codes}, {messages},
                {sources}, {details}, {hashes}, {at})
                AS u(id, account, op, amount, currency, direction, inn, name, cp_account, mfo, purpose, purpose_code, doc,
                     posting, value_date, final, state, has_error, code, message, id_source, details, hash, at)
            ON CONFLICT (tenant_id, bank_account_id, operation_id) DO UPDATE SET
                amount = excluded.amount, currency = excluded.currency, direction = excluded.direction,
                counterparty_inn = excluded.counterparty_inn, counterparty_name = excluded.counterparty_name,
                counterparty_account = excluded.counterparty_account, counterparty_mfo = excluded.counterparty_mfo,
                purpose = excluded.purpose, purpose_code = excluded.purpose_code, document_number = excluded.document_number,
                posting_date = excluded.posting_date, value_date = excluded.value_date,
                is_final = bank_transactions.is_final OR excluded.is_final, source_state = excluded.source_state,
                has_error = excluded.has_error, error_code = excluded.error_code, error_message = excluded.error_message,
                id_source = excluded.id_source, details = excluded.details, source_hash = excluded.source_hash,
                updated_at = excluded.updated_at
            """, ct);
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
        tx.IdSource = info.IdSource;
        tx.Details = info.Details;
        tx.SourceHash = hash;
        tx.IsFinal = isFinal;
    }

    public Task UpsertBalanceAsync(Connection connection, BankAccount account, BankBalanceInfo info, CancellationToken ct) =>
        UpsertBalancesAsync(connection, account, [info], ct);

    /// <summary>Upserts day balances of one account (one query for the stored days).</summary>
    public async Task UpsertBalancesAsync(
        Connection connection, BankAccount account, IReadOnlyList<BankBalanceInfo> infos, CancellationToken ct)
    {
        if (infos.Count == 0) return;
        using var _ = profile.Measure("day_balances");
        var now = time.GetUtcNow();
        var items = infos.GroupBy(i => i.Day).Select(g => g.Last()).ToList();
        var raw = items.Select(i => i.Raw).ToList();
        if (UseBulk) await rawStore.UpsertBulkAsync(connection, raw, now, ct, RefreshUnchangedRaw);
        else await rawStore.UpsertAsync(connection, raw, now, ct);

        var from = items.Min(i => i.Day);
        var to = items.Max(i => i.Day);
        var stored = await db.BankDailyBalances
            .Where(b => b.BankAccountId == account.Id && b.Date >= from && b.Date <= to)
            .ToDictionaryAsync(b => b.Date, ct);

        foreach (var info in items)
        {
            if (!stored.TryGetValue(info.Day, out var balance))
            {
                balance = new BankDailyBalance
                {
                    TenantId = connection.TenantId,
                    BankAccountId = account.Id,
                    Date = info.Day,
                };
                db.BankDailyBalances.Add(balance);
                stored[info.Day] = balance;
            }
            else if (balance.OpeningBalance == info.OpeningBalance && balance.ClosingBalance == info.ClosingBalance
                     && balance.TotalDebit == info.TotalDebit && balance.TotalCredit == info.TotalCredit
                     && balance.IsDayClosed == info.IsDayClosed)
            {
                continue;
            }

            balance.OpeningBalance = info.OpeningBalance;
            balance.ClosingBalance = info.ClosingBalance;
            balance.TotalDebit = info.TotalDebit;
            balance.TotalCredit = info.TotalCredit;
            balance.IsDayClosed = info.IsDayClosed;
            balance.UpdatedAt = now;
        }
    }

    /// <summary>
    /// End of a successful load, in the caller's transaction: precomputed monthly totals of exactly the (INN, month) pairs
    /// the load touched, the reconciliation of exactly the INNs whose operations changed (whole history of each), and the
    /// name spelling volume check.
    /// </summary>
    public async Task CompleteAsync(Connection connection, CancellationToken ct)
    {
        using (profile.Measure("monthly_totals"))
            await monthly.RefreshAsync(changes.Keys, ct);
        if (reconciliationOptions.Value.RunAfterLoad)
        {
            using (profile.Measure("reconciliation"))
                await reconciliation.ReconcileAsync(ReconciliationScope.ForInns(changes.Keys.Select(k => k.Inn)), ct);
        }
        changes.Clear();
        using (profile.Measure("alias_volume_check"))
            await aliasMonitor.CheckAsync(db, connection.TenantId, ct);
    }

    /// <summary>Forgets the touched keys of a load that was rolled back.</summary>
    public void Discard() => changes.Clear();
}
