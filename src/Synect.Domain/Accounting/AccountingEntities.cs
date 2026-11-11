using Synect.Domain.Common;

namespace Synect.Domain.Accounting;

/// <summary>Kind of an accounting document of 1C: a sale (реализация) or a purchase (поступление).</summary>
public enum AccountingDocumentKind
{
    Sales = 1,
    Purchase = 2,
}

/// <summary>Direction of a 1C bank document: money received (поступление) or paid (списание).</summary>
public enum BankDocumentDirection
{
    Receipt = 1,
    Payment = 2,
}

/// <summary>
/// Columns shared by every record received from 1C (stage 8a). Records are never deleted: a deletion or a record missing
/// from a completed full export only sets <see cref="IsDeleted"/>. Fields of the export contract that Synect does not know
/// are kept as they came in <see cref="Extra"/> (jsonb) and the whole item in raw_objects.
/// </summary>
public abstract class AccountingRecordBase : TenantEntity
{
    public bool IsDeleted { get; set; }

    /// <summary>Change time of the object in 1C (or, when the item has none, the export time of its package).</summary>
    public DateTimeOffset ModifiedAt { get; set; }

    /// <summary>SHA-256 (hex) of the canonical JSON of the item as received; equal hash = unchanged, nothing is rewritten.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Item fields Synect does not know (jsonb object, as received).</summary>
    public string Extra { get; set; } = "{}";

    /// <summary>schema_version of the package that last changed the record.</summary>
    public int SchemaVersion { get; set; }

    /// <summary>ingest_batches row that last changed the record.</summary>
    public Guid LastBatchId { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>Last time the record was received (refreshed at most once an hour for unchanged records).</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Last time the stored content changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A 1C object identified by its reference (Ссылка, a string of at most 64 characters) within an organization.</summary>
public abstract class AccountingRefRecord : AccountingRecordBase
{
    public const int MaxRefLength = 64;

    public string Ref { get; set; } = string.Empty;
}

/// <summary>accounting_counterparties (Контрагенты).</summary>
public class AccountingCounterparty : AccountingRefRecord
{
    public string? Tin { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? FullName { get; set; }

    /// <summary>Source text, e.g. legal entity / individual.</summary>
    public string? Kind { get; set; }
}

/// <summary>accounting_contracts (Договоры контрагентов).</summary>
public class AccountingContract : AccountingRefRecord
{
    public string CounterpartyRef { get; set; } = string.Empty;
    public string? Number { get; set; }
    public DateOnly? Date { get; set; }
    public string? Kind { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateOnly? ValidUntil { get; set; }
    public int? PaymentTermDays { get; set; }
}

/// <summary>accounting_bank_accounts (Банковские счета организации).</summary>
public class AccountingBankAccount : AccountingRefRecord
{
    public string Number { get; set; } = string.Empty;
    public string? BankCode { get; set; }
    public string? BankName { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? GlAccountCode { get; set; }
    public bool? IsActive { get; set; }
}

/// <summary>accounting_documents: sales and purchase documents in one table.</summary>
public class AccountingDocument : AccountingRefRecord
{
    public AccountingDocumentKind DocKind { get; set; }

    /// <summary>Document type as named by the source (e.g. "РеализацияТоваровУслуг").</summary>
    public string? DocType { get; set; }

    public string Number { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public bool Posted { get; set; }
    public string? CounterpartyRef { get; set; }
    public string? CounterpartyTin { get; set; }
    public string? ContractRef { get; set; }
    public string? ContractNumber { get; set; }
    public DateOnly? ContractDate { get; set; }
    public string Currency { get; set; } = string.Empty;
    public long AmountTotalMinor { get; set; }
    public long? VatAmountMinor { get; set; }
    public string? EsfNumber { get; set; }
    public DateOnly? EsfDate { get; set; }
    public string? EsfId { get; set; }
    public string? EsfStatus { get; set; }
    public DateOnly? DueDate { get; set; }

    /// <summary>Untrusted third-party text, stored as received.</summary>
    public string? Comment { get; set; }
}

/// <summary>
/// accounting_document_lines: replaced as a whole whenever the document changes. Key: (tenant, document_ref, line_no).
/// </summary>
public class AccountingDocumentLine : ITenantOwned
{
    public Guid TenantId { get; set; }
    public string DocumentRef { get; set; } = string.Empty;
    public int LineNo { get; set; }
    public string? Name { get; set; }
    public string? ProductCode { get; set; }
    public decimal? Quantity { get; set; }
    public long? PriceMinor { get; set; }
    public decimal? VatRate { get; set; }
    public long AmountMinor { get; set; }
    public string Extra { get; set; } = "{}";
}

/// <summary>accounting_bank_documents (Поступление / Списание с расчётного счёта).</summary>
public class AccountingBankDocument : AccountingRefRecord
{
    public BankDocumentDirection Direction { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public bool Posted { get; set; }
    public string? BankAccountRef { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? CounterpartyRef { get; set; }
    public string? CounterpartyTin { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;

    /// <summary>Untrusted third-party text, stored as received.</summary>
    public string? Purpose { get; set; }

    public string? ContractRef { get; set; }
    public string? BankDocumentNumber { get; set; }
    public DateOnly? BankDocumentDate { get; set; }
    public string? CashFlowItem { get; set; }
}

/// <summary>
/// accounting_bank_document_settlements (расшифровка платежа): replaced as a whole with the document. Key: (tenant,
/// document_ref, line_no).
/// </summary>
public class AccountingBankDocumentSettlement : ITenantOwned
{
    public Guid TenantId { get; set; }
    public string DocumentRef { get; set; } = string.Empty;
    public int LineNo { get; set; }
    public string? SettledDocumentRef { get; set; }
    public long AmountMinor { get; set; }
    public string Extra { get; set; } = "{}";
}

/// <summary>accounting_payment_orders (Платёжные поручения).</summary>
public class AccountingPaymentOrder : AccountingRefRecord
{
    public string Number { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public bool Posted { get; set; }
    public string? BankAccountRef { get; set; }
    public string? CounterpartyTin { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;

    /// <summary>Untrusted third-party text, stored as received.</summary>
    public string? Purpose { get; set; }

    public string? BankStatus { get; set; }
}

/// <summary>
/// accounting_settlement_balances: settlement balances and turnovers per counterparty, contract, GL account and currency at a
/// date. Unique by (as_of_date, counterparty_ref, contract_ref, gl_account_code, currency); NULLs compare equal.
/// </summary>
public class AccountingSettlementBalance : AccountingRecordBase
{
    public DateOnly AsOfDate { get; set; }
    public string? CounterpartyRef { get; set; }
    public string? CounterpartyTin { get; set; }
    public string? ContractRef { get; set; }
    public string GlAccountCode { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public long? BalanceDebitMinor { get; set; }
    public long? BalanceCreditMinor { get; set; }
    public long? TurnoverDebitMinor { get; set; }
    public long? TurnoverCreditMinor { get; set; }
}

/// <summary>accounting_bank_account_balances: balance of a bank account in 1C at a date.</summary>
public class AccountingBankAccountBalance : AccountingRecordBase
{
    public DateOnly AsOfDate { get; set; }
    public string BankAccountRef { get; set; } = string.Empty;
    public long BalanceMinor { get; set; }
    public long? TurnoverDebitMinor { get; set; }
    public long? TurnoverCreditMinor { get; set; }
}
