using Synect.Domain.Common;

namespace Synect.Domain.Banking;

/// <summary>Money movement direction from the account holder's point of view.</summary>
public enum TransactionDirection
{
    /// <summary>Money received (Kapital Bank dir = 1).</summary>
    Incoming = 1,

    /// <summary>Money paid out (Kapital Bank dir = 2).</summary>
    Outgoing = 2,
}

/// <summary>Where <see cref="BankTransaction.OperationId"/> comes from (how reliable it is).</summary>
public enum OperationIdSource
{
    /// <summary>The bank's own stable operation id (Kapital Bank b2_id).</summary>
    Bank = 1,

    /// <summary>Hash of the identifying fields, for a bank API without stable ids ("syn:" prefix).</summary>
    Synthetic = 2,

    /// <summary>An identifier given in an imported statement file (a profile names the column or the MT940 reference).</summary>
    File = 3,

    /// <summary>
    /// Deterministic hash computed for an imported statement row without a stable identifier (account, posting date,
    /// amount, direction, document number, normalized purpose, ordinal among identical rows of the file). Less reliable:
    /// two genuinely different identical rows split across files could be taken for one.
    /// </summary>
    FileHash = 4,
}

/// <summary>A single posted bank operation on an account.</summary>
public class BankTransaction : TenantEntity
{
    public Guid ConnectionId { get; set; }
    public Guid BankAccountId { get; set; }

    /// <summary>Bank operation identifier, unique per account. Synthetic hash when the bank has no stable id.</summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>Absolute amount in minor currency units (tiyin for UZS). Always positive.</summary>
    public long Amount { get; set; }

    public string Currency { get; set; } = string.Empty;
    public TransactionDirection Direction { get; set; }

    /// <summary>Signed amount: positive for incoming, negative for outgoing.</summary>
    public long SignedAmount => Direction == TransactionDirection.Incoming ? Amount : -Amount;

    public string? CounterpartyInn { get; set; }
    public string? CounterpartyName { get; set; }
    public string? CounterpartyAccount { get; set; }
    public string? CounterpartyMfo { get; set; }

    public string? Purpose { get; set; }
    public string? PurposeCode { get; set; }
    public string? DocumentNumber { get; set; }

    public DateOnly PostingDate { get; set; }
    public DateOnly? ValueDate { get; set; }

    /// <summary>True when the bank confirmed the operation can no longer change (day closed).</summary>
    public bool IsFinal { get; set; }

    /// <summary>Source state code as received (bank specific).</summary>
    public string? SourceState { get; set; }

    /// <summary>
    /// The bank reported an error for this operation (Kapital Bank: err != 0). Such operations
    /// are kept for visibility but excluded from balances and reconciliation by default.
    /// </summary>
    public bool HasError { get; set; }

    /// <summary>Bank error code (Kapital Bank: err); null when there is no error.</summary>
    public int? ErrorCode { get; set; }

    /// <summary>Bank error message (Kapital Bank: err_msg); null when there is no error.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Origin of <see cref="OperationId"/>.</summary>
    public OperationIdSource IdSource { get; set; } = OperationIdSource.Bank;

    /// <summary>
    /// Optional technical details as a JSON object (jsonb), e.g. {"weak_operation_id": true, "reversal": true} for imported
    /// statement rows. Never contains secrets.
    /// </summary>
    public string? Details { get; set; }

    /// <summary>Hash of the raw payload this row was built from; used to skip no-op updates.</summary>
    public string SourceHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
