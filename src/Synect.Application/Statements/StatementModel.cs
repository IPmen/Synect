using Synect.Domain.Banking;

namespace Synect.Application.Statements;

/// <summary>A problem found while reading a statement file. <see cref="Line"/> is 1-based (null for the whole file).</summary>
/// <param name="Code">Stable machine code, e.g. "fractional_minor_units", "row_rejected", "no_balances".</param>
public sealed record StatementWarning(string Code, string Message, int? Line = null)
{
    public override string ToString() => Line is { } l ? $"line {l}: {Message}" : Message;
}

/// <summary>A balance from the file (signed, minor units) and the date it refers to.</summary>
public sealed record ParsedBalance(DateOnly Date, long Amount);

/// <summary>
/// One operation of a statement in the common model. <see cref="Amount"/> is positive, in minor units (tiyin).
/// Text values are as found in the file (trimmed); they are third-party text and are never interpreted as instructions.
/// </summary>
/// <param name="Line">1-based line of the operation in the decoded file.</param>
/// <param name="IsReversal">MT940 RC / RD: reversal (storno) of an earlier credit / debit.</param>
/// <param name="FileOperationId">Identifier given by the file, used only when the profile says it is stable.</param>
/// <param name="Source">Source fields of the row (MT940 tags or CSV columns) for the raw layer.</param>
public sealed record ParsedOperation(
    int Line,
    DateOnly PostingDate,
    DateOnly? ValueDate,
    TransactionDirection Direction,
    long Amount,
    bool IsReversal,
    string? FileOperationId,
    string? DocumentNumber,
    string? CounterpartyInn,
    string? CounterpartyName,
    string? CounterpartyAccount,
    string? CounterpartyMfo,
    string? Purpose,
    string? PurposeCode,
    string? TransactionType,
    IReadOnlyDictionary<string, string> Source);

/// <summary>One account statement of a file (a file may hold several, e.g. one MT940 message per day or per account).</summary>
public sealed class ParsedStatement
{
    public int Line { get; init; }

    /// <summary>Account number as written in the file (spaces removed).</summary>
    public string AccountNumber { get; init; } = string.Empty;

    /// <summary>Bank code (MFO / BIC) when the file gives one.</summary>
    public string? BankCode { get; init; }

    /// <summary>Currency as written in the file (letters or ISO digits); normalized by the import.</summary>
    public string? Currency { get; init; }

    /// <summary>MT940 :20: transaction reference number.</summary>
    public string? Reference { get; init; }

    /// <summary>MT940 :28C: statement / sequence number.</summary>
    public string? Number { get; init; }

    public ParsedBalance? Opening { get; init; }
    public ParsedBalance? Closing { get; init; }

    /// <summary>First and last day the statement covers, when the format tells (null: from the operations).</summary>
    public DateOnly? PeriodFrom { get; init; }
    public DateOnly? PeriodTo { get; init; }

    public List<ParsedOperation> Operations { get; } = [];
}

/// <summary>Everything a parser read from one file. Parsers never touch the database.</summary>
/// <param name="Encoding">Detected or configured text encoding (e.g. "utf-8", "windows-1251", "cp866").</param>
/// <param name="RowsRejected">Rows that could not be taken (bad date or amount, fractional tiyin); each has a warning.</param>
public sealed record ParsedStatementFile(
    StatementFormat Format,
    string Encoding,
    IReadOnlyList<ParsedStatement> Statements,
    IReadOnlyList<StatementWarning> Warnings,
    int RowsRejected);

/// <summary>The file cannot be read as a statement of the requested format (empty, damaged, wrong format).</summary>
public sealed class StatementFormatException(string message, int? line = null) : Exception(line is { } l ? $"line {l}: {message}" : message)
{
    public int? Line { get; } = line;
}

/// <summary>Reads one statement file format into the common model. Implementations are pure (no database, no I/O).</summary>
public interface IStatementParser
{
    StatementFormat Format { get; }

    /// <exception cref="StatementFormatException">The file is not a readable statement of this format.</exception>
    ParsedStatementFile Parse(byte[] content, StatementProfile profile);
}
