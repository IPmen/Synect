using Synect.Domain.Banking;

namespace Synect.Application.Adapters;

/// <param name="SupportsDateRange">The bank can return several days in one call.</param>
/// <param name="HistoryDepthDays">How far back the bank can return data; null if unknown or unlimited.</param>
/// <param name="HasFinalityFlag">The bank says whether a day is closed (Kapital Bank: fin).</param>
/// <param name="HasStableOperationId">Operation ids are stable across calls (Kapital Bank: b2_id).</param>
public sealed record BankAdapterCapabilities(
    bool SupportsDateRange,
    int? HistoryDepthDays,
    bool HasFinalityFlag,
    bool HasStableOperationId);

public sealed record BankAccountInfo(
    string AccountNumber,
    string Mfo,
    string Currency,
    string? Name,
    string? ExternalId,
    RawRecord Raw);

/// <summary>A bank operation in the common model. Amount is positive, in minor units.</summary>
public sealed record BankTransactionInfo(
    string OperationId,
    long Amount,
    string Currency,
    TransactionDirection Direction,
    string? CounterpartyInn,
    string? CounterpartyName,
    string? CounterpartyAccount,
    string? CounterpartyMfo,
    string? Purpose,
    string? PurposeCode,
    string? DocumentNumber,
    DateOnly PostingDate,
    DateOnly? ValueDate,
    string? SourceState,
    int? ErrorCode,
    string? ErrorMessage,
    RawRecord Raw)
{
    public bool HasError => ErrorCode is not null;

    /// <summary>Origin of <see cref="OperationId"/> (bank id by default).</summary>
    public OperationIdSource IdSource { get; init; } = OperationIdSource.Bank;

    /// <summary>Optional technical details stored with the operation (JSON object).</summary>
    public string? Details { get; init; }
}

/// <param name="IsDayClosed">Null when the bank does not report finality.</param>
public sealed record BankDayTransactions(
    string AccountNumber,
    DateOnly Day,
    IReadOnlyList<BankTransactionInfo> Transactions,
    bool? IsDayClosed)
{
    /// <summary>
    /// Operations of the answer that could not be read and were left out (the rest of the day still loads). A day with
    /// rejected operations is never treated as closed, so that it is read again on the next load.
    /// </summary>
    public IReadOnlyList<BankRejectedOperation> Rejected { get; init; } = [];

    /// <summary>
    /// raw_objects type under which this adapter keeps rejected operations (e.g. "kapitalbank.transaction.rejected");
    /// null when it does not keep them. Set even when nothing was rejected, so that rows of an earlier read of the same
    /// day can be removed once the bank sends the operations in a readable form.
    /// </summary>
    public string? RejectedObjectType { get; init; }
}

/// <summary>An operation the adapter could not read.</summary>
/// <param name="Reason">
/// Why, naming the position and the bank's operation id when there is one, never the operation's content (it goes to logs).
/// </param>
/// <param name="Raw">
/// The operation as received (with the reason), for raw_objects. Its ExternalId is unique within the account and day
/// only (the bank id, or a hash of the item); the writer prefixes it with the account number and the day.
/// </param>
public sealed record BankRejectedOperation(string Reason, RawRecord Raw);

/// <summary>The accounts of a bank connection as the bank lists them.</summary>
public sealed record BankAccountList(IReadOnlyList<BankAccountInfo> Accounts)
{
    /// <summary>
    /// Accounts of the answer that could not be read and were left out (the other accounts still load): one reason each,
    /// naming the position and the bank's internal account id when there is one, never the account number or name. A load
    /// with rejected accounts is never complete.
    /// </summary>
    public IReadOnlyList<string> Rejected { get; init; } = [];
}

public sealed record BankBalanceInfo(
    string AccountNumber,
    DateOnly Day,
    long OpeningBalance,
    long ClosingBalance,
    long TotalDebit,
    long TotalCredit,
    bool IsDayClosed,
    RawRecord Raw);

/// <summary>
/// One account and operational day as the bank returns it: the operations and the day totals.
/// <paramref name="Balance"/> is null when the bank has no totals for that day.
/// </summary>
public sealed record BankDayStatement(BankDayTransactions Transactions, BankBalanceInfo? Balance);

/// <summary>
/// Bank adapter contract. Everything bank specific (transport, auth, field formats) lives
/// behind this interface; callers only see the common models above.
/// </summary>
public interface IBankAdapter : ISourceAdapter
{
    BankAdapterCapabilities Capabilities { get; }

    Task<ConnectionTestResult> TestConnectionAsync(AdapterContext context, CancellationToken ct);

    Task<BankAccountList> GetAccountsAsync(AdapterContext context, CancellationToken ct);

    /// <summary>
    /// Operations (duplicates already removed) and daily totals of one account for one operational day, from one bank
    /// request where the bank returns both together (Kapital Bank: one GetDoc1C call).
    /// </summary>
    Task<BankDayStatement> GetDayAsync(AdapterContext context, string accountNumber, DateOnly day, CancellationToken ct);
}
