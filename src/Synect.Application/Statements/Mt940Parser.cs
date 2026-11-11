using System.Globalization;
using System.Text.RegularExpressions;
using Synect.Domain.Banking;

namespace Synect.Application.Statements;

/// <summary>
/// SWIFT MT940 customer statements: :20: reference, :25: account, :28C: number, :60F:/:60M: opening balance,
/// :61: operations each optionally followed by a (multi-line) :86:, :62F:/:62M: closing balance, :64:/:65: available
/// balances (ignored). Several statements per file; a statement continued in the next message (:62M: then :60M:) is
/// joined. :61: marks C, D, RC and RD (reversals: RC takes money out, RD brings it back), value date YYMMDD with an
/// optional entry date MMDD (the posting date), decimal comma. SWIFT envelopes ({1:...}{2:...}{4: ... -}) are removed.
/// Counterparty details are read from :86: by the profile only (subfields and text field rules); no bank dialect is
/// assumed here.
/// </summary>
public sealed class Mt940Parser : IStatementParser
{
    public StatementFormat Format => StatementFormat.Mt940;

    private static readonly Regex Tag = new(@"^:(?<tag>\d{2}[A-Z]?):(?<value>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Line61 = new(
        @"^(?<vd>\d{6})(?<ed>\d{4})?(?<mark>RC|RD|C|D)(?<fc>[A-Z])?(?<amt>\d[\d,]*)(?<type>[A-Z][A-Z0-9]{3})(?<ref>.*?)(?://(?<bref>.*))?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex BalanceValue = new(@"^(?<mark>[CD])(?<date>\d{6})(?<cur>[A-Z]{3}|\d{3})(?<amt>\d[\d,]*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Envelope = new(@"\{[1-3]:(?:[^{}]|\{[^{}]*\})*\}", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Ignored = ["64", "65", "13D", "21", "34F", "90D", "90C", "28"];

    private sealed class Field(string tag, string value, int line)
    {
        public string Tag { get; } = tag;
        public List<string> Lines { get; } = [value];
        public int Line { get; } = line;
    }

    private sealed class Message
    {
        public int Line;
        public string? Reference;
        public string? Account;
        public int AccountLine;
        public string? Number;
        public (ParsedBalance Balance, string Currency, string Tag, int Line)? Opening;
        public (ParsedBalance Balance, string Currency, string Tag, int Line)? Closing;
        public readonly List<(Field F61, Field? F86)> Operations = [];
    }

    public ParsedStatementFile Parse(byte[] content, StatementProfile profile)
    {
        if (content.Length == 0) throw new StatementFormatException("The file is empty.");
        var (text, encoding) = StatementText.Decode(content, profile.Encoding);
        var lines = StatementText.Lines(text);
        if (lines.All(string.IsNullOrWhiteSpace)) throw new StatementFormatException("The file is empty.");

        var warnings = new List<StatementWarning>();
        var fields = ReadFields(lines, warnings);
        if (fields.Count == 0)
            throw new StatementFormatException("No MT940 tags (:20:, :25:, :60F:, :61:, :62F:) found: the file is not MT940 or is damaged.");

        var messages = Split(fields, warnings);
        if (messages.Count == 0) throw new StatementFormatException("No statement (:20:) found in the file.");
        var joined = Join(messages, warnings);

        var rejected = 0;
        var statements = new List<ParsedStatement>();
        foreach (var m in joined)
        {
            if (m.Account is null) throw new StatementFormatException("The statement has no account (:25:).", m.Line);
            if (m.Opening is not { } open) throw new StatementFormatException("The statement has no opening balance (:60F:).", m.Line);
            if (m.Closing is not { } close) throw new StatementFormatException("The statement has no closing balance (:62F:).", m.Line);
            if (!string.Equals(open.Currency, close.Currency, StringComparison.OrdinalIgnoreCase))
                throw new StatementFormatException($"Opening (:60F:) and closing (:62F:) balances are in different currencies ({open.Currency}, {close.Currency}).", close.Line);

            var (account, bank) = ParseAccount(m.Account, profile, m.AccountLine);
            var from = profile.Mt940.OpeningBalanceDate == "previous_day" ? open.Balance.Date.AddDays(1) : open.Balance.Date;
            var statement = new ParsedStatement
            {
                Line = m.Line,
                AccountNumber = account,
                BankCode = bank ?? profile.BankCode,
                Currency = open.Currency,
                Reference = StatementText.Clean(m.Reference, 64),
                Number = StatementText.Clean(m.Number, 32),
                Opening = open.Balance,
                Closing = close.Balance,
                PeriodFrom = from <= close.Balance.Date ? from : close.Balance.Date,
                PeriodTo = close.Balance.Date,
            };
            foreach (var (f61, f86) in m.Operations)
            {
                var op = ParseOperation(f61, f86, profile, warnings);
                if (op is null) rejected++;
                else statement.Operations.Add(op);
            }
            statements.Add(statement);
        }
        return new ParsedStatementFile(StatementFormat.Mt940, encoding, statements, warnings, rejected);
    }

    private static List<Field> ReadFields(string[] lines, List<StatementWarning> warnings)
    {
        var fields = new List<Field>();
        Field? current = null;
        var stray = 0;
        int? firstStray = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd();
            var lineNo = i + 1;
            if (raw.StartsWith('{'))
            {
                // SWIFT envelope: drop the header blocks and keep what follows "{4:".
                raw = Envelope.Replace(raw, "");
                if (raw.StartsWith("{4:", StringComparison.Ordinal)) raw = raw[3..];
                current = null;
                if (raw.Length == 0) continue;
            }
            if (raw is "-" or "-}" || raw.StartsWith("-}", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }
            var m = Tag.Match(raw);
            if (m.Success)
            {
                current = new Field(m.Groups["tag"].Value, m.Groups["value"].Value, lineNo);
                fields.Add(current);
                continue;
            }
            if (raw.Length == 0) continue;
            if (current is null)
            {
                stray++;
                firstStray ??= lineNo;
                continue;
            }
            current.Lines.Add(raw);
        }
        if (stray > 0)
            warnings.Add(new StatementWarning("text_outside_tags", $"{stray} line(s) outside MT940 fields were ignored.", firstStray));
        return fields;
    }

    private static List<Message> Split(List<Field> fields, List<StatementWarning> warnings)
    {
        var messages = new List<Message>();
        Message? m = null;
        Field? last61 = null;
        var unknown = new HashSet<string>();
        foreach (var f in fields)
        {
            if (f.Tag == "20")
            {
                m = new Message { Line = f.Line, Reference = f.Lines[0] };
                messages.Add(m);
                last61 = null;
                continue;
            }
            if (m is null)
            {
                warnings.Add(new StatementWarning("field_before_20", $"Field :{f.Tag}: before the first :20: was ignored.", f.Line));
                continue;
            }
            switch (f.Tag)
            {
                case "25":
                    m.Account = f.Lines[0];
                    m.AccountLine = f.Line;
                    break;
                case "28C":
                    m.Number = f.Lines[0];
                    break;
                case "60F" or "60M":
                    m.Opening = ParseBalance(f);
                    break;
                case "62F" or "62M":
                    m.Closing = ParseBalance(f);
                    last61 = null;
                    break;
                case "61":
                    m.Operations.Add((f, null));
                    last61 = f;
                    break;
                case "86":
                    // :86: right after :61: describes that operation; elsewhere it is statement information (not used).
                    if (last61 is not null && m.Operations.Count > 0 && ReferenceEquals(m.Operations[^1].F61, last61) && m.Operations[^1].F86 is null)
                        m.Operations[^1] = (last61, f);
                    break;
                default:
                    if (!Ignored.Contains(f.Tag) && unknown.Add(f.Tag))
                        warnings.Add(new StatementWarning("unknown_tag", $"Field :{f.Tag}: is not used.", f.Line));
                    break;
            }
            if (f.Tag != "61" && f.Tag != "86") last61 = null;
        }
        return messages;
    }

    /// <summary>A statement split over several messages (:62M: ... :60M:) becomes one statement.</summary>
    private static List<Message> Join(List<Message> messages, List<StatementWarning> warnings)
    {
        var result = new List<Message>();
        foreach (var m in messages)
        {
            var previous = result.Count > 0 ? result[^1] : null;
            if (previous is { Closing.Tag: "62M" } && m.Opening is { Tag: "60M" } && previous.Account == m.Account)
            {
                if (previous.Closing.Value.Balance.Amount != m.Opening.Value.Balance.Amount)
                    warnings.Add(new StatementWarning("intermediate_balance_mismatch",
                        "The intermediate closing balance (:62M:) differs from the next opening balance (:60M:).", m.Opening.Value.Line));
                previous.Operations.AddRange(m.Operations);
                previous.Closing = m.Closing;
                continue;
            }
            result.Add(m);
        }
        return result;
    }

    private static (ParsedBalance, string, string, int) ParseBalance(Field f)
    {
        var value = f.Lines[0].Trim();
        var m = BalanceValue.Match(value);
        if (!m.Success) throw new StatementFormatException($"Field :{f.Tag}: is not a balance (mark, YYMMDD, currency, amount).", f.Line);
        var date = Date(m.Groups["date"].Value) ?? throw new StatementFormatException($"Field :{f.Tag}: has an invalid date.", f.Line);
        var error = StatementAmounts.TryParse(m.Groups["amt"].Value, ',', out var amount);
        if (error != StatementAmounts.Error.None)
            throw new StatementFormatException($"Field :{f.Tag}: {StatementAmounts.Describe(error)}.", f.Line);
        if (m.Groups["mark"].Value == "D") amount = -amount;
        return (new ParsedBalance(date, amount), m.Groups["cur"].Value, f.Tag, f.Line);
    }

    private static (string Account, string? Bank) ParseAccount(string value, StatementProfile profile, int line)
    {
        if (profile.Mt940.AccountPattern is { } pattern)
        {
            var m = StatementRegex.Compile(pattern, "mt940.accountPattern").Match(value);
            if (!m.Success || !m.Groups["account"].Success)
                throw new StatementFormatException("The account (:25:) does not match mt940.accountPattern of the profile.", line);
            return (m.Groups["account"].Value.Replace(" ", ""), m.Groups["bank"].Success ? m.Groups["bank"].Value.Trim() : null);
        }
        var v = value.Replace(" ", "").Trim();
        string? bank = null;
        var slash = v.LastIndexOf('/');
        if (slash >= 0)
        {
            bank = v[..slash].Trim('/');
            v = v[(slash + 1)..];
        }
        // A currency written after the number ("...001UZS").
        if (v.Length > 3 && v[^3..].All(char.IsAsciiLetterUpper) && char.IsAsciiDigit(v[^4])) v = v[..^3];
        if (v.Length == 0 || v.Length > Limits.Account) throw new StatementFormatException("The account (:25:) is empty or too long.", line);
        return (v, string.IsNullOrEmpty(bank) ? null : bank);
    }

    private static ParsedOperation? ParseOperation(Field f61, Field? f86, StatementProfile profile, List<StatementWarning> warnings)
    {
        var first = f61.Lines[0].Trim();
        var m = Line61.Match(first);
        if (!m.Success)
        {
            warnings.Add(new StatementWarning("row_rejected", "The operation (:61:) does not follow date, mark, amount, type; it was not imported.", f61.Line));
            return null;
        }
        var valueDate = Date(m.Groups["vd"].Value);
        if (valueDate is null)
        {
            warnings.Add(new StatementWarning("row_rejected", "The operation (:61:) has an invalid value date; it was not imported.", f61.Line));
            return null;
        }
        var postingDate = valueDate.Value;
        if (m.Groups["ed"].Success)
        {
            var ed = m.Groups["ed"].Value;
            var month = int.Parse(ed[..2], CultureInfo.InvariantCulture);
            var day = int.Parse(ed[2..], CultureInfo.InvariantCulture);
            var year = valueDate.Value.Year + (month - valueDate.Value.Month > 6 ? -1 : valueDate.Value.Month - month > 6 ? 1 : 0);
            if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                warnings.Add(new StatementWarning("row_rejected", "The operation (:61:) has an invalid entry date; it was not imported.", f61.Line));
                return null;
            }
            postingDate = new DateOnly(year, month, day);
        }

        var error = StatementAmounts.TryParse(m.Groups["amt"].Value, ',', out var amount);
        if (error != StatementAmounts.Error.None || amount <= 0)
        {
            var code = error == StatementAmounts.Error.FractionalMinorUnits ? "fractional_minor_units" : "row_rejected";
            var why = error == StatementAmounts.Error.None ? "the amount is zero" : StatementAmounts.Describe(error);
            warnings.Add(new StatementWarning(code, $"The operation (:61:) was not imported: {why}.", f61.Line));
            return null;
        }

        var mark = m.Groups["mark"].Value;
        var direction = mark is "C" or "RD" ? TransactionDirection.Incoming : TransactionDirection.Outgoing;
        var ownerRef = m.Groups["ref"].Value.Trim();
        var bankRef = m.Groups["bref"].Success ? m.Groups["bref"].Value.Trim() : "";
        var supplementary = f61.Lines.Count > 1 ? string.Join(" ", f61.Lines.Skip(1).Select(l => l.Trim())) : null;

        // Lines are already right-trimmed; a leading space of a continuation line is kept (it may separate words when LineJoin is "").
        var text86 = f86 is null ? null : string.Join(profile.Mt940.LineJoin, f86.Lines);
        var fromSubfields = Subfields(text86, profile);
        var tf = TextFieldExtractor.Extract(profile, text86);
        string? Pick(string field, string? fallback) =>
            fromSubfields.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        var inn = StatementText.Digits(Pick("inn", tf.Inn));
        if (inn is not null && inn.Length is not (9 or 14)) inn = tf.Inn;
        var purpose = Pick("purpose", tf.Purpose) ?? (fromSubfields.Count > 0 ? null : text86);

        string? fileId = profile.OperationId switch
        {
            "bank_reference" => NonRef(bankRef),
            "owner_reference" => NonRef(ownerRef),
            _ => null,
        };
        if (profile.OperationId is "bank_reference" or "owner_reference" && fileId is null)
            warnings.Add(new StatementWarning("no_reference", "The operation has no reference to use as its id; a computed id is used.", f61.Line));

        var source = new Dictionary<string, string>(StringComparer.Ordinal) { ["61"] = string.Join("\n", f61.Lines) };
        if (f86 is not null) source["86"] = string.Join("\n", f86.Lines);

        return new ParsedOperation(
            f61.Line,
            postingDate,
            valueDate,
            direction,
            amount,
            IsReversal: mark is "RC" or "RD",
            fileId,
            StatementText.Clean(Pick("documentNumber", tf.DocumentNumber), Limits.DocumentNumber),
            inn,
            StatementText.Clean(Pick("name", tf.Name), Limits.Name),
            StatementText.Digits(Pick("account", tf.Account)) is { Length: <= Limits.Account } acc ? acc : null,
            StatementText.Digits(Pick("mfo", tf.Mfo)) is { Length: <= Limits.Mfo } mfo ? mfo : null,
            StatementText.Clean(purpose ?? supplementary, Limits.Purpose),
            StatementText.Clean(Pick("purposeCode", tf.PurposeCode), Limits.PurposeCode),
            m.Groups["type"].Value,
            source);
    }

    private static string? NonRef(string reference) =>
        string.IsNullOrWhiteSpace(reference) || reference.Equals("NONREF", StringComparison.OrdinalIgnoreCase) ? null : reference.Trim();

    /// <summary>Structured :86: ("?20...?32..."): subfield values joined per target field of the profile.</summary>
    private static Dictionary<string, string> Subfields(string? text, StatementProfile profile)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var marker = profile.Mt940.SubfieldMarker;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(marker) || profile.Mt940.Subfields.Count == 0) return result;
        var parts = Regex.Split(text, "(" + Regex.Escape(marker) + @"\d{2})", RegexOptions.CultureInvariant, StatementRegex.Timeout);
        for (var i = 1; i + 1 < parts.Length; i += 2)
        {
            var code = parts[i][marker.Length..];
            if (!profile.Mt940.Subfields.TryGetValue(code, out var field)) continue;
            var value = parts[i + 1].Trim();
            result[field] = result.TryGetValue(field, out var existing) ? existing + (field == "purpose" || field == "name" ? " " : "") + value : value;
        }
        return result;
    }

    /// <summary>YYMMDD (years 00-79 are 2000-2079).</summary>
    private static DateOnly? Date(string yymmdd)
    {
        if (yymmdd.Length != 6 || !yymmdd.All(char.IsAsciiDigit)) return null;
        var yy = int.Parse(yymmdd[..2], CultureInfo.InvariantCulture);
        var month = int.Parse(yymmdd[2..4], CultureInfo.InvariantCulture);
        var day = int.Parse(yymmdd[4..], CultureInfo.InvariantCulture);
        var year = yy < 80 ? 2000 + yy : 1900 + yy;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return new DateOnly(year, month, day);
    }
}
