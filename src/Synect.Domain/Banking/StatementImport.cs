using Synect.Domain.Common;

namespace Synect.Domain.Banking;

public enum StatementFormat
{
    Mt940 = 1,
    Csv = 2,
}

/// <summary>Result of the balance check of an imported statement: opening + credit - debit = closing.</summary>
public enum BalanceCheckResult
{
    Passed = 1,

    /// <summary>The balances did not add up and the import was forced (--force).</summary>
    Failed = 2,

    /// <summary>The file has no opening and closing balances to check.</summary>
    Skipped = 3,
}

/// <summary>
/// One imported bank statement file (statement_imports). The same file (by SHA-256 of its bytes) is imported once per
/// tenant. Holds counts and the period only, never the file content.
/// </summary>
public class StatementImport : TenantEntity
{
    public const int MaxAccountLength = 512;

    public Guid ConnectionId { get; set; }

    /// <summary>The sync run of this import (written in the same transaction).</summary>
    public Guid? SyncRunId { get; set; }

    /// <summary>File name only (no directory).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>SHA-256 (hex, lower case) of the file bytes.</summary>
    public string FileSha256 { get; set; } = string.Empty;

    public StatementFormat Format { get; set; }

    /// <summary>Profile name (built-in) or the name inside a profile JSON file.</summary>
    public string Profile { get; set; } = string.Empty;

    public DateTimeOffset ImportedAt { get; set; }

    /// <summary>API key of the person who imported the file; null for developer tools.</summary>
    public Guid? ImportedByKey { get; set; }

    /// <summary>Account number of the statement; several accounts are separated by commas.</summary>
    public string Account { get; set; } = string.Empty;

    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }

    /// <summary>Operations accepted from the file (rejected rows are not counted).</summary>
    public int TransactionsTotal { get; set; }

    /// <summary>Operations that were not stored before.</summary>
    public int TransactionsNew { get; set; }

    /// <summary>Operations already stored (same account and operation id), e.g. from an overlapping file.</summary>
    public int TransactionsDuplicate { get; set; }

    /// <summary>Rows of the file that were rejected (see the warnings of the import).</summary>
    public int RowsRejected { get; set; }

    public int WarningCount { get; set; }

    public BalanceCheckResult BalanceCheck { get; set; }
}
