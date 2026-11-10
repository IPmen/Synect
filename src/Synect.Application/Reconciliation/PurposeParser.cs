using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Synect.Domain.Text;

namespace Synect.Application.Reconciliation;

/// <summary>What a reference in a payment purpose points to.</summary>
public enum PurposeReferenceKind
{
    /// <summary>Счёт-фактура / hisob-faktura / ЭСФ.</summary>
    Invoice,

    /// <summary>Акт / dalolatnoma.</summary>
    Act,

    /// <summary>Счёт, накладная and other documents.</summary>
    OtherDocument,

    /// <summary>A code with letters and digits written without a keyword (e.g. "INV-002"); matches only exactly.</summary>
    Code,

    /// <summary>Договор / shartnoma / контракт.</summary>
    Contract,
}

/// <param name="Raw">The number as written (transliterated, lower case).</param>
/// <param name="Keys">Comparison keys (see <see cref="ReferenceKey"/>).</param>
/// <param name="Date">Date written right after the number ("от 12.10.2026"), when present.</param>
public sealed record PurposeReference(PurposeReferenceKind Kind, string Raw, IReadOnlyList<string> Keys, DateOnly? Date);

/// <summary>References and dates found in one payment purpose.</summary>
public sealed record ParsedPurpose(
    IReadOnlyList<PurposeReference> Documents, IReadOnlyList<PurposeReference> Contracts, IReadOnlyList<DateOnly> Dates)
{
    public static readonly ParsedPurpose Empty = new([], [], []);

    /// <summary>The purpose names a document or a contract number.</summary>
    public bool HasReference => Documents.Count > 0 || Contracts.Count > 0;
}

/// <summary>
/// Comparison keys of document and contract numbers: transliterated, upper case, letters and digits only, leading zeros
/// of every digit run removed ("INV-0012" and "inv 12" give "INV12"; "№ 0001" gives "1"). A digits-only key ("12")
/// also matches a number whose digits are the same ("SF-12"), see <see cref="Matches"/>.
/// </summary>
public static partial class ReferenceKey
{
    public static string Full(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return string.Empty;
        var latin = LatinTransliterator.ToLatinLower(number);
        var sb = new StringBuilder(latin.Length);
        var inDigits = false;
        var leadingZero = false;
        foreach (var c in latin)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                if (leadingZero) sb.Append('0');
                inDigits = false;
                leadingZero = false;
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                if (!inDigits) { inDigits = true; leadingZero = c == '0'; if (c != '0') sb.Append(c); continue; }
                if (leadingZero && c == '0') continue;
                leadingZero = false;
                sb.Append(c);
                continue;
            }
            if (leadingZero) sb.Append('0');
            inDigits = false;
            leadingZero = false;
            sb.Append(char.ToUpperInvariant(c));
        }
        if (leadingZero) sb.Append('0');
        return sb.ToString();
    }

    /// <summary>The digits of the number (leading zeros of every run removed), or empty.</summary>
    public static string Digits(string? number) => new(Full(number).Where(char.IsAsciiDigit).ToArray());

    /// <summary>Keys of a number found in a purpose: the full key, plus the key without a trailing year ("15/2026" -> "15").</summary>
    public static IReadOnlyList<string> KeysOf(string raw)
    {
        var full = Full(raw);
        if (full.Length == 0) return [];
        var keys = new List<string> { full };
        var m = YearSuffix().Match(raw);
        if (m.Success && Full(m.Groups[1].Value) is { Length: > 0 } withoutYear && withoutYear != full) keys.Add(withoutYear);
        return keys;
    }

    /// <summary>
    /// A purpose reference matches a document number when a full key is equal, or when the reference is digits only and
    /// equals the digits of the document number (except for <see cref="PurposeReferenceKind.Code"/>, which needs the full key).
    /// </summary>
    public static bool Matches(PurposeReference reference, string? documentNumber)
    {
        if (string.IsNullOrWhiteSpace(documentNumber)) return false;
        var full = Full(documentNumber);
        if (full.Length == 0) return false;
        foreach (var key in reference.Keys)
        {
            if (key == full) return true;
            if (reference.Kind != PurposeReferenceKind.Code && key.All(char.IsAsciiDigit) && key == Digits(documentNumber)) return true;
        }
        return false;
    }

    [GeneratedRegex(@"^(.*\d)[/-](?:20\d\d)$")]
    private static partial Regex YearSuffix();
}

/// <summary>Word lists of <see cref="PurposeParser"/>, loaded from the embedded PurposeDictionary.json.</summary>
public sealed class PurposeDictionary
{
    public const string ResourceName = "Synect.Reconciliation.PurposeDictionary.json";

    private static readonly Lazy<PurposeDictionary> DefaultInstance = new(Load);

    public static PurposeDictionary Default => DefaultInstance.Value;

    /// <summary>Keyword phrases (canonical words) and their kind, longest first.</summary>
    internal List<(string[] Words, PurposeReferenceKind Kind)> Keywords { get; } = [];
    internal HashSet<string> NumberMarkers { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Fillers { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> DateMarkers { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> UzbekSuffixes { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> ListConnectors { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> Months { get; } = new(StringComparer.Ordinal);

    /// <summary>Single canonical words that start a keyword phrase (to split "sf12" into "sf" + "12").</summary>
    internal HashSet<string> KeywordPrefixes { get; } = new(StringComparer.Ordinal);

    public static PurposeDictionary Load()
    {
        using var stream = typeof(PurposeDictionary).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        return FromJson(stream);
    }

    public static PurposeDictionary FromJson(Stream json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;
        var d = new PurposeDictionary();
        IEnumerable<string> List(string name) =>
            root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Select(x => x.GetString()).OfType<string>()
                : [];

        void AddKeywords(string name, PurposeReferenceKind kind)
        {
            foreach (var entry in List(name))
            {
                var words = PurposeParser.Words(entry);
                if (words.Length == 0) continue;
                d.Keywords.Add((words, kind));
                if (words.Length == 1) d.KeywordPrefixes.Add(words[0]);
            }
        }

        AddKeywords("invoiceKeywords", PurposeReferenceKind.Invoice);
        AddKeywords("actKeywords", PurposeReferenceKind.Act);
        AddKeywords("otherDocumentKeywords", PurposeReferenceKind.OtherDocument);
        AddKeywords("contractKeywords", PurposeReferenceKind.Contract);
        d.Keywords.Sort((a, b) => b.Words.Length.CompareTo(a.Words.Length));

        foreach (var w in List("numberMarkers").SelectMany(PurposeParser.Words)) d.NumberMarkers.Add(w);
        foreach (var w in List("fillers").SelectMany(PurposeParser.Words)) d.Fillers.Add(w);
        foreach (var w in List("dateMarkers").SelectMany(PurposeParser.Words)) d.DateMarkers.Add(w);
        foreach (var w in List("uzbekNumberSuffixes").SelectMany(PurposeParser.Words)) d.UzbekSuffixes.Add(w);
        foreach (var w in List("listConnectors").SelectMany(PurposeParser.Words)) d.ListConnectors.Add(w);
        if (root.TryGetProperty("months", out var months) && months.ValueKind == JsonValueKind.Object)
            foreach (var p in months.EnumerateObject())
                if (PurposeParser.Words(p.Name) is [var word] && p.Value.TryGetInt32(out var m)) d.Months[word] = m;
        return d;
    }
}

/// <summary>
/// Extracts document and contract numbers and dates from a bank payment purpose written in Russian or Uzbek (Latin or
/// Cyrillic): "Оплата по счет-фактуре № 15 от 12.10.2026, договор 7/2026", "15-sonli shartnoma bo'yicha
/// hisob-faktura №21 uchun", "ҳисоб-фактура №21". Deterministic and rule based; the words come from
/// <see cref="PurposeDictionary"/>. Text is transliterated with the same <see cref="LatinTransliterator"/> as counterparty
/// names. Numbers of 16 and more digits (bank accounts) are never taken as references.
/// </summary>
public sealed partial class PurposeParser(PurposeDictionary dictionary)
{
    public const int MaxPurposeLength = 2000;

    private enum TokenType { Word, Number, Date, Sign, Sep }

    private sealed class Token(TokenType type, string text, string canon)
    {
        public TokenType Type { get; } = type;
        public string Text { get; } = text;
        public string Canon { get; } = canon;
        public bool UzbekNumberSuffix { get; init; }
        public bool Consumed { get; set; }
    }

    public static PurposeParser Default { get; } = new(PurposeDictionary.Default);

    public ParsedPurpose Parse(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose)) return ParsedPurpose.Empty;
        var text = Prepare(purpose.Length > MaxPurposeLength ? purpose[..MaxPurposeLength] : purpose);
        var tokens = Tokenize(text);
        var documents = new List<PurposeReference>();
        var contracts = new List<PurposeReference>();
        var dates = new List<DateOnly>();

        foreach (var t in tokens.Where(t => t.Type == TokenType.Date))
            if (ParseDate(t.Text) is { } d) dates.Add(d);
        dates.AddRange(TextDates(text));

        // 1. Keyword, then the number: "счет-фактура № 15", "shartnoma №7", "акт 3 и 4".
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type != TokenType.Word || tokens[i].Consumed) continue;
            if (MatchKeyword(tokens, i) is not { } kw) continue;
            var found = CollectNumbers(tokens, i + kw.Length, kw.Kind);
            if (found.Count == 0) continue;
            (kw.Kind == PurposeReferenceKind.Contract ? contracts : documents).AddRange(found);
            for (var k = i; k < i + kw.Length; k++) tokens[k].Consumed = true;
        }

        // 2. Uzbek order, the number before the keyword: "15-sonli shartnoma", "№21 hisob-faktura".
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Type != TokenType.Number || t.Consumed || IsAccountLike(t.Text)) continue;
            var marked = t.UzbekNumberSuffix || (i > 0 && (tokens[i - 1].Type == TokenType.Sign || dictionary.NumberMarkers.Contains(tokens[i - 1].Canon)));
            if (!marked) continue;
            var j = i + 1;
            while (j < tokens.Count && j <= i + 2 && tokens[j].Type == TokenType.Word && dictionary.UzbekSuffixes.Contains(tokens[j].Canon)) j++;
            if (j >= tokens.Count || tokens[j].Type != TokenType.Word || MatchKeyword(tokens, j) is not { } kw) continue;
            t.Consumed = true;
            var reference = new PurposeReference(kw.Kind, t.Text, ReferenceKey.KeysOf(t.Text), DateBefore(tokens, i));
            (kw.Kind == PurposeReferenceKind.Contract ? contracts : documents).Add(reference);
        }

        // 3. Codes starting with a letter and containing digits but without a keyword ("оплата по ABC-0021"): weak document
        //    references ("3-oktyabr", "2-ya" start with a digit and are not codes).
        foreach (var t in tokens.Where(t => t.Type == TokenType.Number && !t.Consumed))
        {
            if (!char.IsAsciiLetter(t.Text[0]) || IsAccountLike(t.Text)) continue;
            var key = ReferenceKey.Full(t.Text);
            if (key.Length >= 4 && key.Count(char.IsAsciiDigit) >= 1)
                documents.Add(new PurposeReference(PurposeReferenceKind.Code, t.Text, [key], null));
        }

        return new ParsedPurpose(
            documents.DistinctBy(r => (r.Kind, r.Keys[0])).ToList(),
            contracts.DistinctBy(r => r.Keys[0]).ToList(),
            dates.Distinct().Order().ToList());
    }

    /// <summary>Canonical words of a phrase: transliterated, lower case, Latin variants unified, dots dropped.</summary>
    internal static string[] Words(string phrase)
    {
        var text = Prepare(phrase);
        return WordPattern().Matches(text).Select(m => Canon(m.Value)).Where(w => w.Length > 0).ToArray();
    }

    private static string Canon(string word) => LatinTransliterator.ApplyLatinVariants(word);

    /// <summary>Transliterates, unifies dashes and the number sign, glues abbreviations like "сч.-ф." and spaces signs.</summary>
    private static string Prepare(string value)
    {
        var latin = LatinTransliterator.ToLatinLower(value);
        var sb = new StringBuilder(latin.Length + 16);
        foreach (var c in latin)
        {
            switch (c)
            {
                case '‐' or '‑' or '‒' or '–' or '—' or '―' or '−': sb.Append('-'); break;
                case '№' or '#': sb.Append(" № "); break;
                case '\\': sb.Append('/'); break;
                default: sb.Append(char.IsControl(c) ? ' ' : c); break;
            }
        }
        var text = sb.ToString().Replace("n°", " № ", StringComparison.Ordinal).Replace("°", " ", StringComparison.Ordinal);
        text = AbbreviationDash().Replace(text, "-");
        return text;
    }

    private List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        foreach (Match m in TokenPattern().Matches(text))
        {
            if (m.Groups["date"].Success) tokens.Add(new Token(TokenType.Date, m.Value, m.Value));
            else if (m.Groups["sign"].Success) tokens.Add(new Token(TokenType.Sign, "№", "№"));
            else if (m.Groups["sep"].Success) tokens.Add(new Token(TokenType.Sep, m.Value, m.Value));
            else if (m.Groups["word"].Success) tokens.Add(new Token(TokenType.Word, m.Value, Canon(m.Value)));
            else if (m.Groups["num"].Success) AddNumber(tokens, m.Value);
        }
        return tokens;
    }

    /// <summary>Splits "sf12" / "akt-5" / "n15" into keyword + number and "15-sonli" into number + Uzbek suffix.</summary>
    private void AddNumber(List<Token> tokens, string value)
    {
        var number = value.TrimEnd('.', '-', '/', '_');
        var uzbek = false;
        var suffix = UzbekSuffix().Match(number);
        if (suffix.Success && dictionary.UzbekSuffixes.Contains(Canon(suffix.Groups[2].Value)))
        {
            number = suffix.Groups[1].Value;
            uzbek = true;
        }
        var prefix = AlphaPrefix().Match(number);
        if (prefix.Success)
        {
            var word = Canon(prefix.Groups[1].Value);
            if (dictionary.KeywordPrefixes.Contains(word) || dictionary.NumberMarkers.Contains(word))
            {
                tokens.Add(new Token(TokenType.Word, prefix.Groups[1].Value, word));
                number = prefix.Groups[2].Value;
            }
        }
        if (number.Length > 0) tokens.Add(new Token(TokenType.Number, number, number) { UzbekNumberSuffix = uzbek });
    }

    private (int Length, PurposeReferenceKind Kind)? MatchKeyword(List<Token> tokens, int start)
    {
        foreach (var (words, kind) in dictionary.Keywords)
        {
            if (start + words.Length > tokens.Count) continue;
            var ok = true;
            for (var k = 0; k < words.Length && ok; k++)
                ok = tokens[start + k].Type == TokenType.Word && !tokens[start + k].Consumed && tokens[start + k].Canon == words[k];
            if (ok) return (words.Length, kind);
        }
        return null;
    }

    /// <summary>
    /// Numbers after a keyword: fillers and markers are skipped; up to two other words are skipped only when a number sign
    /// follows ("договору поставки № 15"); "и", "va", commas continue a list.
    /// </summary>
    private List<PurposeReference> CollectNumbers(List<Token> tokens, int start, PurposeReferenceKind kind)
    {
        var result = new List<PurposeReference>();
        var i = start;
        DateOnly? dateBefore = null;
        var skipped = 0;
        var unknown = 0;
        var signAfterUnknown = false;
        while (i < tokens.Count && skipped <= 6)
        {
            var t = tokens[i];
            if (t.Type == TokenType.Sign && unknown > 0) signAfterUnknown = true;
            var gluedKeyword = t.Type == TokenType.Word && dictionary.KeywordPrefixes.Contains(t.Canon)
                               && i + 1 < tokens.Count && tokens[i + 1].Type == TokenType.Number; // "сф SF-12"
            if (t.Type == TokenType.Sign || gluedKeyword || (t.Type == TokenType.Word && (dictionary.NumberMarkers.Contains(t.Canon)
                    || dictionary.Fillers.Contains(t.Canon) || dictionary.DateMarkers.Contains(t.Canon))))
            {
                i++; skipped++; continue;
            }
            if (t.Type == TokenType.Date && result.Count == 0)
            {
                dateBefore ??= ParseDate(t.Text); // "договор от 01.02.2026 № 15"
                i++; skipped++; continue;
            }
            if (t.Type == TokenType.Word && unknown < 2 && MatchKeyword(tokens, i) is null)
            {
                unknown++; i++; skipped++; continue;
            }
            break;
        }
        if (unknown > 0 && !signAfterUnknown) return result;

        while (i < tokens.Count)
        {
            var t = tokens[i];
            // "15-sonli" belongs to the keyword after it (Uzbek order), never to the one before.
            if (t.Type != TokenType.Number || t.Consumed || t.UzbekNumberSuffix || IsAccountLike(t.Text)) break;
            t.Consumed = true;
            var date = DateAfter(tokens, i + 1) ?? dateBefore;
            result.Add(new PurposeReference(kind, t.Text, ReferenceKey.KeysOf(t.Text), date));
            i++;
            // Skip the date that belongs to this number.
            while (i < tokens.Count && (tokens[i].Type == TokenType.Date
                   || (tokens[i].Type == TokenType.Word && dictionary.DateMarkers.Contains(tokens[i].Canon)))) i++;
            // List continuation: ", 16", "и № 17", "va 18".
            var j = i;
            var connector = false;
            while (j < tokens.Count && (tokens[j].Type == TokenType.Sep
                   || (tokens[j].Type == TokenType.Word && dictionary.ListConnectors.Contains(tokens[j].Canon))))
            {
                connector = true; j++;
            }
            if (!connector) break;
            while (j < tokens.Count && (tokens[j].Type == TokenType.Sign
                   || (tokens[j].Type == TokenType.Word && dictionary.NumberMarkers.Contains(tokens[j].Canon)))) j++;
            if (j < tokens.Count && tokens[j].Type == TokenType.Number && !tokens[j].Consumed) i = j;
            else break;
        }
        return result;
    }

    private DateOnly? DateAfter(List<Token> tokens, int start)
    {
        for (var i = start; i < tokens.Count && i <= start + 2; i++)
        {
            if (tokens[i].Type == TokenType.Date) return ParseDate(tokens[i].Text);
            if (tokens[i].Type != TokenType.Word || !dictionary.DateMarkers.Contains(tokens[i].Canon)) return null;
        }
        return null;
    }

    private static DateOnly? DateBefore(List<Token> tokens, int index)
    {
        for (var i = index - 1; i >= 0 && i >= index - 3; i--)
            if (tokens[i].Type == TokenType.Date) return ParseDate(tokens[i].Text);
        return null;
    }

    private static bool IsAccountLike(string number) => number.Count(char.IsAsciiDigit) >= 16;

    private static DateOnly? ParseDate(string text)
    {
        string[] formats = ["d.M.yyyy", "d/M/yyyy", "d.M.yy", "d/M/yy", "yyyy-MM-dd"];
        if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return null;
        var date = DateOnly.FromDateTime(dt);
        return date.Year is >= 2000 and <= 2100 ? date : null;
    }

    /// <summary>"5 октября 2026", "2026 yil 12 oktyabrdagi".</summary>
    private IEnumerable<DateOnly> TextDates(string text)
    {
        foreach (Match m in DayMonthYear().Matches(text))
            if (Month(m.Groups["month"].Value) is { } month && Make(m.Groups["year"].Value, month, m.Groups["day"].Value) is { } d) yield return d;
        foreach (Match m in YearDayMonth().Matches(text))
            if (Month(m.Groups["month"].Value) is { } month && Make(m.Groups["year"].Value, month, m.Groups["day"].Value) is { } d) yield return d;
    }

    private int? Month(string word)
    {
        var canon = Canon(word);
        foreach (var (name, month) in dictionary.Months)
            if (canon.StartsWith(name, StringComparison.Ordinal)) return month;
        return null;
    }

    private static DateOnly? Make(string year, int month, string day)
    {
        if (!int.TryParse(year, CultureInfo.InvariantCulture, out var y) || !int.TryParse(day, CultureInfo.InvariantCulture, out var d)) return null;
        if (y is < 2000 or > 2100 || d < 1 || d > DateTime.DaysInMonth(y, month)) return null;
        return new DateOnly(y, month, d);
    }

    [GeneratedRegex(@"(?<date>(?<![0-9])\d{1,2}[./]\d{1,2}[./](?:\d{4}|\d{2})(?![0-9])|(?<![0-9])\d{4}-\d{2}-\d{2}(?![0-9]))|(?<num>(?=[a-z0-9/_.-]*\d)[a-z0-9]+(?:[-/_.][a-z0-9]+)*)|(?<word>[a-z]+(?:[-/][a-z]+)*)|(?<sign>№)|(?<sep>[,;])")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[a-z]+(?:[-/][a-z]+)*")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\.\s*-\s*")]
    private static partial Regex AbbreviationDash();

    [GeneratedRegex(@"^(.*\d)-?([a-z]+)$")]
    private static partial Regex UzbekSuffix();

    [GeneratedRegex(@"^([a-z]+(?:[-/][a-z]+)*)[-/_.]?(\d.*)$")]
    private static partial Regex AlphaPrefix();

    [GeneratedRegex(@"\b(?<day>\d{1,2})\s*-?\s*(?<month>[a-z]{3,})\s+(?<year>\d{4})\b")]
    private static partial Regex DayMonthYear();

    [GeneratedRegex(@"\b(?<year>\d{4})\s*(?:yil|y|g|goda)?\.?\s+(?<day>\d{1,2})\s*-?\s*(?<month>[a-z]{3,})")]
    private static partial Regex YearDayMonth();
}
