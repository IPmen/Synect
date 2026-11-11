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
    bool? IsDayClosed);

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
/// Bank adapter contract. Everything bank specific (transport, auth, field formats) lives
/// behind this interface; callers only see the common models above.
/// </summary>
public interface IBankAdapter : ISourceAdapter
{
    BankAdapterCapabilities Capabilities { get; }

    Task<ConnectionTestResult> TestConnectionAsync(AdapterContext context, CancellationToken ct);

    Task<IReadOnlyList<BankAccountInfo>> GetAccountsAsync(AdapterContext context, CancellationToken ct);

    /// <summary>Operations of one account for one operational day. Duplicates must already be removed.</summary>
    Task<BankDayTransactions> GetTransactionsAsync(AdapterContext context, string accountNumber, DateOnly day, CancellationToken ct);

    /// <summary>Daily totals of one account; null when the bank has nothing for that day.</summary>
    Task<BankBalanceInfo?> GetBalancesAsync(AdapterContext context, string accountNumber, DateOnly day, CancellationToken ct);
}
