using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Synect.Domain.Banking;

namespace Synect.Application.Statements;

/// <summary>
/// How to read one bank's statement files. Everything bank specific lives here, never in the parsers: which text encoding,
/// how the MT940 :86: text or the CSV columns map to the common model, how to find the counterparty INN, name, account,
/// MFO and document number (column, markers or regular expressions). Built-in profiles: <see cref="StatementProfiles"/>;
/// custom profiles are JSON files (see docs/import-profiles.md).
/// </summary>
public sealed class StatementProfile
{
    public string Name { get; set; } = string.Empty;
    public StatementFormat Format { get; set; }
    public string? Description { get; set; }

    /// <summary>auto (UTF-8 with or without BOM, Windows-1251 or CP866 detected), utf-8, windows-1251, cp866.</summary>
    public string Encoding { get; set; } = "auto";

    /// <summary>Statement account when the file does not give it (CSV without an account column).</summary>
    public string? AccountNumber { get; set; }

    /// <summary>Currency when the file does not give it (letters or ISO digits).</summary>
    public string? Currency { get; set; }

    /// <summary>Bank code (MFO) when the file does not give it.</summary>
    public string? BankCode { get; set; }

    /// <summary>
    /// Where the operation id comes from. hash (default): computed from the row (less reliable, marked FileHash).
    /// MT940: bank_reference (after "//" in :61:) or owner_reference (before "//"); CSV: column (operationId column).
    /// Use a file id only when the bank confirms it is stable across exports.
    /// </summary>
    public string OperationId { get; set; } = "hash";

    /// <summary>
    /// Fields taken from the payment text (MT940 :86:, CSV purpose column) when the file has no own column for them.
    /// Keys: inn, name, account, mfo, documentNumber, purposeCode, purpose.
    /// </summary>
    public Dictionary<string, TextFieldRule> TextFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Characters that end a value found after a marker (e.g. "/" in "/INN/301234567/").</summary>
    public string Separators { get; set; } = "/;|";

    public Mt940Settings Mt940 { get; set; } = new();
    public CsvSettings Csv { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Reads and validates a profile JSON (all regular expressions compiled once here).</summary>
    public static StatementProfile FromJson(string json)
    {
        StatementProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<StatementProfile>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The profile is not valid JSON: {ex.Message}", ex);
        }
        if (profile is null) throw new ArgumentException("The profile is empty.");
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("The profile needs a \"name\".");
        if (!Enum.IsDefined(profile.Format)) throw new ArgumentException("The profile needs \"format\": \"mt940\" or \"csv\".");
        profile.TextFields = new Dictionary<string, TextFieldRule>(profile.TextFields, StringComparer.OrdinalIgnoreCase);
        profile.Csv.Columns = new Dictionary<string, List<string>>(profile.Csv.Columns, StringComparer.OrdinalIgnoreCase);
        profile.Validate();
        return profile;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Checks names and compiles every pattern; throws <see cref="ArgumentException"/> with the offending part.</summary>
    public void Validate()
    {
        foreach (var key in TextFields.Keys)
            if (!TextFieldRule.Fields.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown text field \"{key}\" (known: {string.Join(", ", TextFieldRule.Fields)}).");
        foreach (var (key, rule) in TextFields) rule.Compile(key, Separators);
        foreach (var key in Csv.Columns.Keys)
            if (!CsvSettings.Fields.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Unknown CSV column field \"{key}\" (known: {string.Join(", ", CsvSettings.Fields)}).");
        foreach (var p in Csv.SkipRowsMatching) StatementRegex.Compile(p, "csv.skipRowsMatching");
        if (Mt940.AccountPattern is { } ap) StatementRegex.Compile(ap, "mt940.accountPattern");
        StatementText.ResolveEncoding(Encoding);
        if (OperationId is not ("hash" or "bank_reference" or "owner_reference" or "column"))
            throw new ArgumentException("operationId must be hash, bank_reference, owner_reference or column.");
        if (Mt940.OpeningBalanceDate is not ("first_day" or "previous_day"))
            throw new ArgumentException("mt940.openingBalanceDate must be first_day or previous_day.");
        if (Csv.AmountMode is not ("auto" or "debit_credit" or "signed"))
            throw new ArgumentException("csv.amountMode must be auto, debit_credit or signed.");
        if (Csv.DecimalSeparator is not ("auto" or "," or "."))
            throw new ArgumentException("csv.decimalSeparator must be auto, \",\" or \".\".");
    }

    /// <summary>A copy (profiles are mutable option objects; built-ins are never changed).</summary>
    public StatementProfile Clone() => FromJson(ToJson());
}

/// <summary>
/// One field read from free payment text. Either <see cref="Markers"/> (a value right after one of the words, e.g. "ИНН",
/// "INN", "STIR", matching <see cref="Pattern"/>, or up to the next separator when Pattern is empty) or explicit
/// <see cref="Regex"/> patterns with a named group "v". The first match wins. Matching ignores case.
/// </summary>
public sealed class TextFieldRule
{
    public static readonly string[] Fields = ["inn", "name", "account", "mfo", "documentNumber", "purposeCode", "purpose"];

    public List<string> Markers { get; set; } = [];

    /// <summary>Value pattern after a marker (regular expression without groups), e.g. "\d{9}|\d{14}".</summary>
    public string? Pattern { get; set; }

    /// <summary>Full regular expressions with a group named "v" (or the first group).</summary>
    public List<string> Regex { get; set; } = [];

    private List<Regex>? _compiled;

    internal IReadOnlyList<Regex> Compile(string field, string separators)
    {
        if (_compiled is not null) return _compiled;
        var list = new List<Regex>();
        foreach (var r in Regex) list.Add(StatementRegex.Compile(r, $"textFields.{field}.regex"));
        if (Markers.Count > 0)
        {
            // A marker ending with a letter must not be the start of a longer word ("No" is not "Nov"); "№" may touch the value.
            var markers = string.Join("|", Markers.Where(m => m.Length > 0).OrderByDescending(m => m.Length)
                .Select(m => System.Text.RegularExpressions.Regex.Escape(m) + (char.IsLetter(m[^1]) ? @"(?![\p{L}])" : "")));
            var stop = string.Concat(separators.Select(ch => $"\\u{(int)ch:X4}"));
            var value = string.IsNullOrEmpty(Pattern) ? $"[^{stop}\\r\\n]+" : $"(?:{Pattern})";
            // The marker must not be the end of a longer word ("PINN" is not "INN"); the value follows ':', '=', '.', '№', '#' or spaces.
            var tail = string.IsNullOrEmpty(Pattern) ? "" : @"(?![\p{L}\d])";
            list.Add(StatementRegex.Compile($@"(?<![\p{{L}}\d])(?:{markers})[\s:=.№#/\-]*(?<v>{value}){tail}", $"textFields.{field}.markers"));
        }
        return _compiled = list;
    }

    /// <summary>First match in <paramref name="text"/>, trimmed (null when nothing matches).</summary>
    public string? Find(string field, string separators, string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var regex in Compile(field, separators))
        {
            Match m;
            try
            {
                m = regex.Match(text);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }
            if (!m.Success) continue;
            var g = m.Groups["v"].Success ? m.Groups["v"] : m.Groups.Count > 1 ? m.Groups[1] : m.Groups[0];
            var v = Unquote(g.Value.Trim());
            if (v.Length > 0) return v;
        }
        return null;
    }

    /// <summary>Removes quotes that wrap the whole value ("X", «X», 'X'); quotes inside a name (ООО «X») stay.</summary>
    private static string Unquote(string v)
    {
        while (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '«' && v[^1] == '»') || (v[0] == '\'' && v[^1] == '\'')))
            v = v[1..^1].Trim();
        return v;
    }
}

public sealed class Mt940Settings
{
    /// <summary>
    /// What the :60F: date means. first_day (default): the first day the statement covers. previous_day: the date of the
    /// previous closing balance, so the statement starts the next day. Banks differ; unknown for a new bank.
    /// </summary>
    public string OpeningBalanceDate { get; set; } = "first_day";

    /// <summary>
    /// Regular expression for :25: with groups "account" and optionally "bank". Default: "BANK/ACCOUNT" or "ACCOUNT",
    /// spaces removed, a trailing 3-letter currency dropped.
    /// </summary>
    public string? AccountPattern { get; set; }

    /// <summary>
    /// Structured :86: (e.g. "?20...?32..."): the marker before two-digit subfield codes ("?"); empty = free text.
    /// </summary>
    public string? SubfieldMarker { get; set; }

    /// <summary>Subfield code -> field (purpose, name, inn, account, mfo, documentNumber, purposeCode); same field codes are joined.</summary>
    public Dictionary<string, string> Subfields { get; set; } = new(StringComparer.Ordinal);

    /// <summary>How the lines of a multi-line :86: are joined ("" keeps SWIFT 65-character wrapping seamless; " " is the default).</summary>
    public string LineJoin { get; set; } = " ";
}

public sealed class CsvSettings
{
    public static readonly string[] Fields =
    [
        "account", "postingDate", "valueDate", "debit", "credit", "amount", "direction", "currency", "documentNumber",
        "counterpartyName", "counterpartyInn", "counterpartyAccount", "counterpartyMfo", "purpose", "purposeCode", "operationId",
        "openingBalance", "closingBalance",
    ];

    /// <summary>auto (the most frequent of ; , tab | in the header line), or the character itself ("\t" for tab).</summary>
    public string Delimiter { get; set; } = "auto";

    public string Quote { get; set; } = "\"";

    /// <summary>1-based number of the header record; 0 = no header (columns are given as "#1", "#2", ...).</summary>
    public int HeaderRow { get; set; } = 1;

    /// <summary>
    /// Field -> column header names (alternatives, compared without case and extra spaces) or "#n" (1-based column number).
    /// </summary>
    [JsonConverter(typeof(OneOrManyDictionaryConverter))]
    public Dictionary<string, List<string>> Columns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> DateFormats { get; set; } = ["dd.MM.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "dd.MM.yy", "dd.MM.yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"];

    /// <summary>auto: the last of "," and "." in a number is the decimal separator. Set "," or "." when amounts may have thousands separators.</summary>
    public string DecimalSeparator { get; set; } = "auto";

    /// <summary>debit_credit (two columns), signed (one amount column with a sign) or auto (debit_credit when both columns exist).</summary>
    public string AmountMode { get; set; } = "auto";

    /// <summary>Signed mode: a positive amount is money received (credit). False: positive = paid out.</summary>
    public bool PositiveIsCredit { get; set; } = true;

    /// <summary>Direction column values (compared without case) that mean money received / paid out.</summary>
    public List<string> CreditValues { get; set; } = ["C", "CR", "К", "КТ", "КРЕДИТ", "CREDIT", "KIRIM", "+"];
    public List<string> DebitValues { get; set; } = ["D", "DR", "Д", "ДТ", "ДЕБЕТ", "DEBIT", "CHIQIM", "-"];

    /// <summary>Rows whose text matches one of these patterns are skipped silently (totals, page footers).</summary>
    public List<string> SkipRowsMatching { get; set; } = [];
}

/// <summary>Reads {"field": "Header"} and {"field": ["Header 1", "Header 2"]} alike.</summary>
internal sealed class OneOrManyDictionaryConverter : JsonConverter<Dictionary<string, List<string>>>
{
    public override Dictionary<string, List<string>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("csv.columns must be an object.");
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            reader.Read();
            var values = new List<string>();
            if (reader.TokenType == JsonTokenType.String) values.Add(reader.GetString()!);
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    values.Add(reader.GetString() ?? throw new JsonException($"csv.columns.{key}: only strings are allowed."));
            }
            else if (reader.TokenType != JsonTokenType.Null) throw new JsonException($"csv.columns.{key} must be a string or a list of strings.");
            result[key] = values;
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, List<string>> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value)
        {
            writer.WritePropertyName(k);
            writer.WriteStartArray();
            foreach (var x in v) writer.WriteStringValue(x);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}

internal static class StatementRegex
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    public static Regex Compile(string pattern, string where)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Timeout);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"Invalid regular expression in {where}: {ex.Message}", ex);
        }
    }
}
