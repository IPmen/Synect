using System.Globalization;
using System.Text;
using Synect.Domain.Banking;

namespace Synect.Application.Statements;

/// <summary>
/// CSV statements described by a profile: delimiter (or auto), header row, column of each model field (header names or
/// "#n"), date and number formats, debit and credit columns or one signed amount column (or a direction column), and
/// the INN / document number either from a column or from the purpose text (profile text field rules). Rows are grouped
/// into one statement per account. Optional opening / closing balance columns: the first value (by date) is the opening
/// balance, the last one the closing balance of the account's statement.
/// </summary>
public sealed class CsvStatementParser : IStatementParser
{
    public StatementFormat Format => StatementFormat.Csv;

    public ParsedStatementFile Parse(byte[] content, StatementProfile profile)
    {
        if (content.Length == 0) throw new StatementFormatException("The file is empty.");
        var (text, encoding) = StatementText.Decode(content, profile.Encoding);
        if (string.IsNullOrWhiteSpace(text)) throw new StatementFormatException("The file is empty.");
        var csv = profile.Csv;
        var quote = string.IsNullOrEmpty(csv.Quote) ? '\0' : csv.Quote[0];
        var delimiter = Delimiter(text, csv, quote);
        var records = ReadRecords(text, delimiter, quote);
        if (records.All(r => r.Fields.All(string.IsNullOrWhiteSpace))) throw new StatementFormatException("The file is empty.");

        var warnings = new List<StatementWarning>();
        Dictionary<string, int> columns;
        int firstData;
        if (csv.HeaderRow > 0)
        {
            var nonEmpty = records.Where(r => r.Fields.Any(f => !string.IsNullOrWhiteSpace(f))).ToList();
            if (nonEmpty.Count < csv.HeaderRow) throw new StatementFormatException($"The file has no header row {csv.HeaderRow}.");
            var header = nonEmpty[csv.HeaderRow - 1];
            columns = MapColumns(header.Fields, csv, header.Line);
            firstData = records.IndexOf(header) + 1;
        }
        else
        {
            columns = MapColumns([], csv, 1);
            firstData = 0;
        }
        if (!columns.ContainsKey("postingDate"))
            throw new StatementFormatException("No date column: the profile's csv.columns.postingDate matches no column of the header.");

        var mode = csv.AmountMode == "auto"
            ? columns.ContainsKey("debit") && columns.ContainsKey("credit") ? "debit_credit" : "signed"
            : csv.AmountMode;
        if (mode == "debit_credit" && !(columns.ContainsKey("debit") && columns.ContainsKey("credit")))
            throw new StatementFormatException("No debit and credit columns (csv.columns.debit / credit) in the header.");
        if (mode == "signed" && !columns.ContainsKey("amount"))
            throw new StatementFormatException("No amount column: map csv.columns.amount, or debit and credit.");
        if (!columns.ContainsKey("account") && string.IsNullOrWhiteSpace(profile.AccountNumber))
            throw new StatementFormatException("No account: map csv.columns.account or set accountNumber in the profile.");

        char? decimalSeparator = csv.DecimalSeparator switch { "," => ',', "." => '.', _ => null };
        var skip = csv.SkipRowsMatching.Select(p => StatementRegex.Compile(p, "csv.skipRowsMatching")).ToList();
        var formats = csv.DateFormats.ToArray();

        var byAccount = new Dictionary<string, (ParsedStatement Statement, List<(DateOnly Date, int Order, long? Opening, long? Closing)> Balances)>(StringComparer.Ordinal);
        var rejected = 0;
        var order = 0;
        for (var i = firstData; i < records.Count; i++)
        {
            var (line, fields) = records[i];
            if (fields.All(string.IsNullOrWhiteSpace)) continue;
            var joined = string.Join(" ", fields.Select(f => f.Trim())).Trim();
            if (skip.Any(r => Safe(() => r.IsMatch(joined)))) continue;

            string? Get(string field) => columns.TryGetValue(field, out var index) && index < fields.Count ? NullIfEmpty(fields[index]) : null;
            void Reject(string code, string message)
            {
                warnings.Add(new StatementWarning(code, message, line));
                rejected++;
            }

            if (!TryDate(Get("postingDate"), formats, out var postingDate))
            {
                Reject("row_rejected", "The row has no valid date; it was not imported.");
                continue;
            }
            DateOnly? valueDate = TryDate(Get("valueDate"), formats, out var vd) ? vd : null;

            TransactionDirection direction;
            long amount;
            if (mode == "debit_credit")
            {
                var de = Amount(Get("debit"), decimalSeparator, out var debit);
                var ce = Amount(Get("credit"), decimalSeparator, out var credit);
                if (de == StatementAmounts.Error.FractionalMinorUnits || ce == StatementAmounts.Error.FractionalMinorUnits)
                {
                    Reject("fractional_minor_units", $"The row was not imported: {StatementAmounts.Describe(StatementAmounts.Error.FractionalMinorUnits)}.");
                    continue;
                }
                if (de is not (StatementAmounts.Error.None or StatementAmounts.Error.Empty) || ce is not (StatementAmounts.Error.None or StatementAmounts.Error.Empty))
                {
                    Reject("row_rejected", "The row was not imported: the debit or credit amount is not a number.");
                    continue;
                }
                if (debit != 0 && credit != 0)
                {
                    Reject("row_rejected", "The row was not imported: it has both a debit and a credit amount.");
                    continue;
                }
                if (debit == 0 && credit == 0)
                {
                    Reject("row_rejected", "The row was not imported: no amount.");
                    continue;
                }
                (direction, amount) = debit != 0
                    ? (debit > 0 ? TransactionDirection.Outgoing : TransactionDirection.Incoming, Math.Abs(debit))
                    : (credit > 0 ? TransactionDirection.Incoming : TransactionDirection.Outgoing, Math.Abs(credit));
            }
            else
            {
                var e = Amount(Get("amount"), decimalSeparator, out var signed);
                if (e != StatementAmounts.Error.None || signed == 0)
                {
                    var code = e == StatementAmounts.Error.FractionalMinorUnits ? "fractional_minor_units" : "row_rejected";
                    Reject(code, $"The row was not imported: {(e == StatementAmounts.Error.None ? "the amount is zero" : StatementAmounts.Describe(e))}.");
                    continue;
                }
                var incoming = signed > 0 == csv.PositiveIsCredit;
                if (Get("direction") is { } dir)
                {
                    if (csv.CreditValues.Contains(dir.Trim(), StringComparer.OrdinalIgnoreCase)) incoming = true;
                    else if (csv.DebitValues.Contains(dir.Trim(), StringComparer.OrdinalIgnoreCase)) incoming = false;
                    else
                    {
                        Reject("row_rejected", "The row was not imported: unknown direction value.");
                        continue;
                    }
                }
                direction = incoming ? TransactionDirection.Incoming : TransactionDirection.Outgoing;
                amount = Math.Abs(signed);
            }

            var account = (Get("account") is { } a ? a.Replace(" ", "") : profile.AccountNumber?.Replace(" ", "")) ?? "";
            if (account.Length == 0 || account.Length > Limits.Account)
            {
                Reject("row_rejected", "The row was not imported: no account number.");
                continue;
            }
            if (!byAccount.TryGetValue(account, out var group))
            {
                group = (new ParsedStatement
                {
                    Line = line,
                    AccountNumber = account,
                    BankCode = profile.BankCode,
                    Currency = Get("currency") ?? profile.Currency,
                }, []);
                byAccount[account] = group;
            }
            else if (Get("currency") is { } cur && group.Statement.Currency is { } known
                     && !string.Equals(Synect.Domain.Common.IsoCurrency.Normalize(cur), Synect.Domain.Common.IsoCurrency.Normalize(known), StringComparison.Ordinal))
            {
                Reject("row_rejected", "The row was not imported: its currency differs from the other rows of the account.");
                continue;
            }

            long? opening = null, closing = null;
            if (Get("openingBalance") is { } ob)
            {
                if (StatementAmounts.TryParse(ob, decimalSeparator, out var v) == StatementAmounts.Error.None) opening = v;
                else warnings.Add(new StatementWarning("balance_ignored", "The opening balance of the row is not a valid amount; ignored.", line));
            }
            if (Get("closingBalance") is { } cb)
            {
                if (StatementAmounts.TryParse(cb, decimalSeparator, out var v) == StatementAmounts.Error.None) closing = v;
                else warnings.Add(new StatementWarning("balance_ignored", "The closing balance of the row is not a valid amount; ignored.", line));
            }
            group.Balances.Add((postingDate, order++, opening, closing));

            var purposeText = Get("purpose");
            var tf = TextFieldExtractor.Extract(profile, purposeText);
            var inn = StatementText.Digits(Get("counterpartyInn"));
            if (inn is not null && inn.Length is not (9 or 14))
            {
                warnings.Add(new StatementWarning("inn_ignored", "The counterparty INN column is not 9 or 14 digits; ignored.", line));
                inn = null;
            }
            inn ??= tf.Inn;

            var source = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (field, index) in columns)
                if (index < fields.Count && NullIfEmpty(fields[index]) is { } value)
                    source[field] = value;

            group.Statement.Operations.Add(new ParsedOperation(
                line,
                postingDate,
                valueDate,
                direction,
                amount,
                IsReversal: false,
                profile.OperationId == "column" ? Get("operationId") : null,
                StatementText.Clean(Get("documentNumber") ?? tf.DocumentNumber, Limits.DocumentNumber),
                inn,
                StatementText.Clean(Get("counterpartyName") ?? tf.Name, Limits.Name),
                (StatementText.Digits(Get("counterpartyAccount")) ?? tf.Account) is { Length: <= Limits.Account } acc ? acc : null,
                StatementText.Digits(Get("counterpartyMfo")) is { Length: <= Limits.Mfo } mfo ? mfo : tf.Mfo,
                StatementText.Clean(tf.Purpose ?? purposeText, Limits.Purpose),
                StatementText.Clean(Get("purposeCode") ?? tf.PurposeCode, Limits.PurposeCode),
                null,
                source));
        }

        if (byAccount.Count == 0 && rejected == 0)
            throw new StatementFormatException("The file has a header but no operations.");

        var statements = new List<ParsedStatement>();
        foreach (var (statement, balances) in byAccount.Values)
        {
            var ordered = balances.OrderBy(b => b.Date).ThenBy(b => b.Order).ToList();
            var opening = ordered.FirstOrDefault(b => b.Opening is not null);
            var closing = ordered.LastOrDefault(b => b.Closing is not null);
            var from = statement.Operations.Count > 0 ? statement.Operations.Min(o => o.PostingDate) : (DateOnly?)null;
            var to = statement.Operations.Count > 0 ? statement.Operations.Max(o => o.PostingDate) : (DateOnly?)null;
            var withBalances = new ParsedStatement
            {
                Line = statement.Line,
                AccountNumber = statement.AccountNumber,
                BankCode = statement.BankCode,
                Currency = statement.Currency,
                Opening = opening.Opening is { } o ? new ParsedBalance(opening.Date, o) : null,
                Closing = closing.Closing is { } c ? new ParsedBalance(closing.Date, c) : null,
                PeriodFrom = from,
                PeriodTo = to,
            };
            withBalances.Operations.AddRange(statement.Operations);
            statements.Add(withBalances);
        }
        return new ParsedStatementFile(StatementFormat.Csv, encoding, statements, warnings, rejected);
    }

    private static bool Safe(Func<bool> f)
    {
        try
        {
            return f();
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static StatementAmounts.Error Amount(string? text, char? separator, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return StatementAmounts.Error.Empty;
        return StatementAmounts.TryParse(text, separator, out value);
    }

    private static bool TryDate(string? text, string[] formats, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }
        return false;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static Dictionary<string, int> MapColumns(IReadOnlyList<string> header, CsvSettings csv, int line)
    {
        var normalized = header.Select(Normalize).ToList();
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, names) in csv.Columns)
        {
            foreach (var name in names)
            {
                if (name.StartsWith('#') && int.TryParse(name[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1)
                {
                    result[field] = n - 1;
                    break;
                }
                var index = normalized.IndexOf(Normalize(name));
                if (index >= 0)
                {
                    result[field] = index;
                    break;
                }
            }
        }
        var duplicates = result.GroupBy(r => r.Value).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0)
            throw new StatementFormatException(
                $"Column {duplicates[0].Key + 1} is mapped to several fields ({string.Join(", ", duplicates[0].Select(x => x.Key))}); fix csv.columns of the profile.", line);
        return result;
    }

    private static string Normalize(string header) =>
        string.Join(' ', header.Replace("﻿", "").Trim().Trim('"').ToLowerInvariant().Replace('ё', 'е').Replace('ʻ', '\'').Replace('‘', '\'').Replace('’', '\'')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static char Delimiter(string text, CsvSettings csv, char quote)
    {
        var configured = csv.Delimiter;
        if (configured is "\\t" or "tab") return '\t';
        if (!string.IsNullOrEmpty(configured) && configured != "auto") return configured[0];
        // The first lines up to the header (and a few after): the candidate found most often outside quotes.
        var sample = StatementText.Lines(text).Where(l => l.Trim().Length > 0).Take(Math.Max(csv.HeaderRow, 1) + 3).ToList();
        var best = ';';
        var bestCount = -1;
        foreach (var candidate in new[] { ';', ',', '\t', '|' })
        {
            var count = sample.Sum(l => CountOutsideQuotes(l, candidate, quote));
            if (count > bestCount)
            {
                best = candidate;
                bestCount = count;
            }
        }
        return best;
    }

    private static int CountOutsideQuotes(string line, char c, char quote)
    {
        var inQuotes = false;
        var count = 0;
        foreach (var ch in line)
        {
            if (ch == quote && quote != '\0') inQuotes = !inQuotes;
            else if (ch == c && !inQuotes) count++;
        }
        return count;
    }

    /// <summary>RFC 4180 records: quoted fields may contain the delimiter, doubled quotes and line breaks.</summary>
    private static List<(int Line, List<string> Fields)> ReadRecords(string text, char delimiter, char quote)
    {
        var records = new List<(int, List<string>)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var line = 1;
        var recordLine = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == quote)
                {
                    if (i + 1 < text.Length && text[i + 1] == quote)
                    {
                        field.Append(quote);
                        i++;
                    }
                    else inQuotes = false;
                }
                else
                {
                    if (ch == '\n') line++;
                    field.Append(ch);
                }
                continue;
            }
            if (ch == quote && quote != '\0' && field.ToString().Trim().Length == 0)
            {
                field.Clear();
                inQuotes = true;
            }
            else if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(field.ToString());
                field.Clear();
                records.Add((recordLine, fields));
                fields = [];
                line++;
                recordLine = line;
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add((recordLine, fields));
        }
        return records;
    }
}
