using System.Globalization;
using System.Text.Json;

namespace Synect.Infrastructure.Adapters;

/// <summary>
/// Tolerant readers for source JSON: providers are inconsistent about numbers vs strings,
/// date formats and missing fields, so mappers read through these helpers.
/// </summary>
internal static class SourceJson
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "dd.MM.yyyy", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "dd.MM.yyyy HH:mm:ss", "yyyyMMdd",
    ];

    /// <summary>Returns the "data" property when the payload is wrapped in an envelope, otherwise the root.</summary>
    public static JsonElement Unwrap(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;

    public static string? Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => NullIfEmpty(v.GetString()),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static decimal? Dec(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString()?.Replace(',', '.'), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public static int? Int(JsonElement obj, string name) => Dec(obj, name) is { } d ? (int)d : null;

    public static DateOnly? Date(JsonElement obj, string name)
    {
        var s = Str(obj, name);
        if (s is null) return null;
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return DateOnly.FromDateTime(dt);
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return DateOnly.FromDateTime(dto.DateTime);
        throw new FormatException($"Unrecognized date in field '{name}'.");
    }

    /// <summary>
    /// Reads a timestamp. Strings without an offset are interpreted in <paramref name="sourceOffset"/>;
    /// numbers are Unix time (seconds, or milliseconds when large).
    /// </summary>
    public static DateTimeOffset? Timestamp(JsonElement obj, string name, TimeSpan sourceOffset)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number)
        {
            var n = v.GetInt64();
            return n > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
        }
        var s = Str(obj, name);
        if (s is null) return null;
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return new DateTimeOffset(local, sourceOffset).ToUniversalTime();
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.ToUniversalTime();
        throw new FormatException($"Unrecognized timestamp in field '{name}'.");
    }

    /// <summary>Converts a source amount to minor units, refusing to silently lose precision.</summary>
    public static long ToMinorUnits(decimal amount, AmountUnit unit, string field)
    {
        var minor = unit == AmountUnit.Major ? amount * 100m : amount;
        if (minor != decimal.Truncate(minor))
            throw new FormatException($"Amount in field '{field}' has more precision than minor units allow.");
        return checked((long)minor);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Unit in which a source reports money amounts.</summary>
public enum AmountUnit
{
    /// <summary>Minor units (tiyin, cents). Stored as is.</summary>
    Minor = 0,

    /// <summary>Major units (sum, dollars), possibly with a fractional part. Multiplied by 100.</summary>
    Major = 1,
}
