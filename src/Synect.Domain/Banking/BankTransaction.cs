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

    /// <summary>Hash of the raw payload this row was built from; used to skip no-op updates.</summary>
    public string SourceHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
