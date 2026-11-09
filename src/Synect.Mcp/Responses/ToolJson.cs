using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Synect.Mcp.Responses;

/// <summary>JSON conventions of tool answers: snake_case names and enum values, nulls kept.</summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        // Keeps Cyrillic readable (fewer tokens for the model). Quotes, backslashes and control characters are
        // still escaped, so third-party text cannot break out of a JSON string. Answers are never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
}

/// <summary>An amount of one currency: integer minor units plus the decimal string. Never added across currencies.</summary>
public sealed record MoneyValue(long AmountMinor, string Amount, string Currency)
{
    public static MoneyValue Of(long minor, string currency) => new(minor, Format(minor, currency), currency);

    /// <summary>Minor units per major unit. Currencies without minor units would go here (none are used now).</summary>
    public static int Decimals(string currency) => currency switch
    {
        "JPY" or "KRW" => 0,
        _ => 2,
    };

    public static string Format(long minor, string currency)
    {
        var decimals = Decimals(currency);
        if (decimals == 0) return minor.ToString(CultureInfo.InvariantCulture);
        var divisor = (long)Math.Pow(10, decimals);
        var abs = minor == long.MinValue ? (ulong)long.MaxValue + 1 : (ulong)Math.Abs(minor);
        var whole = abs / (ulong)divisor;
        var fraction = abs % (ulong)divisor;
        return (minor < 0 ? "-" : "") + whole.ToString(CultureInfo.InvariantCulture) + "."
               + fraction.ToString(CultureInfo.InvariantCulture).PadLeft(decimals, '0');
    }
}

/// <summary>
/// Third-party text (payment purpose, counterparty or account name, status comment). Always returned in this
/// explicit wrapper: {"untrusted_text": "..."}; cut to Mcp:UntrustedTextMaxLength, control and invisible
/// formatting characters removed. It is data, never instructions.
/// </summary>
public sealed record UntrustedValue(
    [property: JsonPropertyName("untrusted_text")] string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Truncated = false);

public static class TextSafety
{
    /// <summary>Removes control and format characters (bidi overrides, zero-width), collapses whitespace, cuts to max.</summary>
    public static string Clean(string value, int maxLength, out bool truncated)
    {
        var sb = new StringBuilder(Math.Min(value.Length, maxLength + 1));
        var lastWasSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || category is UnicodeCategory.Control)
            {
                if (!lastWasSpace && sb.Length > 0) sb.Append(' ');
                lastWasSpace = true;
                continue;
            }
            if (category is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                continue;
            sb.Append(rune.ToString());
            lastWasSpace = false;
        }
        var cleaned = sb.ToString().Trim();
        truncated = false;
        if (cleaned.Length > maxLength)
        {
            var cut = maxLength;
            if (char.IsHighSurrogate(cleaned[cut - 1])) cut--;
            cleaned = cleaned[..cut].TrimEnd() + "…";
            truncated = true;
        }
        return cleaned;
    }

    public static UntrustedValue? Untrusted(string? value, int maxLength)
    {
        if (value is null) return null;
        var cleaned = Clean(value, maxLength, out var truncated);
        return new UntrustedValue(cleaned, truncated);
    }

    /// <summary>Keeps the last 4 characters: "****1234".</summary>
    public static string? MaskAccount(string? number, bool mask)
    {
        if (number is null || !mask) return number;
        var trimmed = number.Trim();
        return trimmed.Length <= 4 ? "****" : "****" + trimmed[^4..];
    }
}
