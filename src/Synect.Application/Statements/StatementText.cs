using System.Globalization;
using System.Text;

namespace Synect.Application.Statements;

/// <summary>Text decoding of statement files and small text helpers shared by the parsers.</summary>
public static class StatementText
{
    static StatementText() => System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public const string Utf8 = "utf-8";
    public const string Windows1251 = "windows-1251";
    public const string Cp866 = "cp866";

    /// <summary>Validates an encoding name of a profile; returns the canonical name ("auto" stays "auto").</summary>
    public static string ResolveEncoding(string? name) => (name ?? "auto").Trim().ToLowerInvariant() switch
    {
        "" or "auto" => "auto",
        "utf-8" or "utf8" => Utf8,
        "windows-1251" or "cp1251" or "1251" => Windows1251,
        "cp866" or "ibm866" or "866" or "dos" => Cp866,
        var other => throw new ArgumentException($"Unsupported encoding \"{other}\" (use auto, utf-8, windows-1251 or cp866)."),
    };

    /// <summary>
    /// Decodes the file. auto: a UTF-8 BOM, else valid UTF-8, else Windows-1251 or CP866, whichever reads as more plausible
    /// Russian / Uzbek Cyrillic text (frequent letters count, pseudo-graphics and control characters count against).
    /// </summary>
    public static (string Text, string Encoding) Decode(byte[] content, string? configured)
    {
        var name = ResolveEncoding(configured);
        var bom = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;
        if (name == Utf8 || (name == "auto" && bom))
            return (new UTF8Encoding(false).GetString(content, bom ? 3 : 0, content.Length - (bom ? 3 : 0)), Utf8);
        if (name == Windows1251) return (Get(1251).GetString(content), Windows1251);
        if (name == Cp866) return (Get(866).GetString(content), Cp866);

        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content), Utf8);
        }
        catch (DecoderFallbackException)
        {
        }
        var w = Get(1251).GetString(content);
        var d = Get(866).GetString(content);
        return Score(d) > Score(w) ? (d, Cp866) : (w, Windows1251);
    }

    public static System.Text.Encoding EncodingOf(string name) => name switch
    {
        Windows1251 => Get(1251),
        Cp866 => Get(866),
        _ => new UTF8Encoding(false),
    };

    private static System.Text.Encoding Get(int codePage) => System.Text.Encoding.GetEncoding(codePage);

    private const string Frequent = "оеаинтсрвлкмдпуяыьгзбчйхжшюцщэфъё";

    private static int Score(string text)
    {
        var score = 0;
        foreach (var ch in text)
        {
            var lower = char.ToLowerInvariant(ch);
            if (lower is >= 'а' and <= 'я' or 'ё' or 'ў' or 'қ' or 'ғ' or 'ҳ')
            {
                var rank = Frequent.IndexOf(lower);
                score += rank is >= 0 and < 10 ? 3 : 1;
                // Upper-case letters inside words are rare; CP866 read as 1251 gives upper case and symbols.
                if (char.IsUpper(ch)) score -= 1;
            }
            else if (ch is >= '─' and <= '◿' or >= '‐' and <= '›' or '¤' or '¦' or '§' or '®' or '¶' or '№' or '√' or 'Ђ' or 'Ѓ' or 'Љ' or 'Њ' or 'Ћ' or 'Ќ' or 'Џ' or 'ђ' or 'ѓ' or 'љ' or 'њ' or 'ћ' or 'ќ' or 'џ')
                score -= 4;
            else if (char.IsControl(ch) && ch is not ('\r' or '\n' or '\t'))
                score -= 6;
        }
        return score;
    }

    /// <summary>Splits into lines (CRLF, LF or CR).</summary>
    public static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>Trimmed, internal whitespace collapsed to single spaces, control characters removed; null when empty.</summary>
    public static string? Clean(string? value, int? maxLength = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        if (sb.Length == 0) return null;
        var result = sb.ToString();
        if (maxLength is { } max && result.Length > max)
        {
            var cut = max;
            if (char.IsHighSurrogate(result[cut - 1])) cut--;
            result = result[..cut];
        }
        return result;
    }

    /// <summary>Digits only (spaces and dashes removed); null when nothing is left.</summary>
    public static string? Digits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 0 ? null : digits;
    }

    /// <summary>Lower case, whitespace collapsed: the purpose as used in the operation hash.</summary>
    public static string NormalizeForHash(string? purpose) => (Clean(purpose) ?? string.Empty).ToLowerInvariant();

    public static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
