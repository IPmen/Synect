using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Synect.Domain.Accounting;
using Synect.Domain.Common;

namespace Synect.Application.Ingest;

/// <summary>A refused item or field: a stable code for the extension and our own message (never the received value).</summary>
public sealed class IngestValueException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Item rejection codes (also the per-item "code" of the answer).</summary>
public static class IngestCodes
{
    public const string InvalidItem = "invalid_item";
    public const string InvalidText = "invalid_text";
    public const string MissingField = "missing_field";
    public const string EmptyRef = "empty_ref";
    public const string RefTooLong = "ref_too_long";
    public const string InvalidRef = "invalid_ref";
    public const string InvalidTin = "invalid_tin";
    public const string InvalidDate = "invalid_date";
    public const string InvalidTimestamp = "invalid_timestamp";
    public const string InvalidAmount = "invalid_amount";
    public const string FractionalMinorUnits = "fractional_minor_units";
    public const string AmountOutOfRange = "amount_out_of_range";
    public const string InvalidNumber = "invalid_number";
    public const string InvalidCurrency = "invalid_currency";
    public const string InvalidBoolean = "invalid_boolean";
    public const string InvalidValue = "invalid_value";
    public const string TextTooLong = "text_too_long";
    public const string InvalidLines = "invalid_lines";
    public const string DuplicateLineNo = "duplicate_line_no";
    public const string InvalidBarcode = "invalid_barcode";

    // Not errors: why an item was left unchanged.
    public const string SameContent = "same_content";
    public const string OlderThanStored = "older_than_stored";
    public const string UnknownRefDeleted = "unknown_ref_deleted";

    // Not an error: an accepted item that lifted the deletion mark of a full export (same content received again).
    public const string Restored = "restored";
}

/// <summary>Parsing and checking of single values of the export contract (see <see cref="IngestFieldType"/>).</summary>
public static partial class IngestValues
{
    public const int ShortTextLength = 256;
    public const int CodeLength = 64;
    private const string EmptyGuid = "00000000-0000-0000-0000-000000000000";

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?)?(Z|[+-]\d{2}(:?\d{2})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateTime();

    /// <summary>JSON null, an empty or whitespace-only string: the field is treated as absent.</summary>
    public static bool IsMissing(JsonElement e) =>
        e.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (e.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(e.GetString()));

    /// <summary>Text of a scalar (strings as they are, numbers as written); objects and arrays are refused.</summary>
    private static string Scalar(JsonElement e, string field) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString()!,
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new IngestValueException(IngestCodes.InvalidValue, $"Field '{field}' must be a scalar value."),
    };

    /// <summary>A reference to another object: null for the empty 1C reference.</summary>
    public static string? Ref(JsonElement e, string field)
    {
        var value = Scalar(e, field).Trim();
        if (value.Length == 0 || value == EmptyGuid) return null;
        if (value.Length > AccountingRefRecord.MaxRefLength)
            throw new IngestValueException(IngestCodes.RefTooLong, $"Field '{field}' is longer than {AccountingRefRecord.MaxRefLength} characters.");
        return value;
    }

    public static string Text(JsonElement e, string field, int maxLength)
    {
        var value = Scalar(e, field);
        if (value.Length > maxLength)
            throw new IngestValueException(IngestCodes.TextTooLong, $"Field '{field}' is longer than {maxLength} characters.");
        return value;
    }

    public static string Code(JsonElement e, string field)
    {
        var value = Scalar(e, field).Trim();
        if (value.Length > CodeLength)
            throw new IngestValueException(IngestCodes.TextTooLong, $"Field '{field}' is longer than {CodeLength} characters.");
        return value;
    }

    public static string Tin(JsonElement e, string field, bool extraLengths = false)
    {
        var value = Scalar(e, field).Trim();
        if (!IsTin(value, extraLengths))
            throw new IngestValueException(IngestCodes.InvalidTin, $"Field '{field}' must be a TIN of {TinLengths(extraLengths)} digits.");
        return value;
    }

    /// <summary>
    /// 9 or 14 ASCII digits; with <paramref name="extraLengths"/> (Ingest:AllowAnyTinLengthInDevelopment in Development)
    /// also 10 or 12.
    /// </summary>
    public static bool IsTin(string value, bool extraLengths = false) =>
        (value.Length is 9 or 14 || (extraLengths && value.Length is 10 or 12)) && value.All(char.IsAsciiDigit);

    public static string TinLengths(bool extraLengths) => extraLengths ? "9, 10, 12 or 14" : "9 or 14";

    /// <summary>The date part of an ISO 8601 date or date-time; null for the empty 1C date (year 1).</summary>
    public static DateOnly? Date(JsonElement e, string field)
    {
        var value = e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : "";
        if (!IsoDateTime().IsMatch(value)
            || !DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new IngestValueException(IngestCodes.InvalidDate, $"Field '{field}' must be an ISO 8601 date (yyyy-MM-dd).");
        if (date.Year == 1) return null;
        if (date.Year is < 1900 or > 2199)
            throw new IngestValueException(IngestCodes.InvalidDate, $"Field '{field}' is outside the years 1900-2199.");
        return date;
    }

    /// <summary>ISO 8601 date-time; without an offset <paramref name="defaultOffset"/> is assumed; null for the empty 1C date.</summary>
    public static DateTimeOffset? Timestamp(JsonElement e, string field, TimeSpan defaultOffset)
    {
        var value = e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : "";
        if (!IsoDateTime().IsMatch(value))
            throw new IngestValueException(IngestCodes.InvalidTimestamp, $"Field '{field}' must be an ISO 8601 date-time.");
        if (value.StartsWith("0001-01-01", StringComparison.Ordinal)) return null;
        var normalized = value.Replace(' ', 'T');
        var hasOffset = normalized.Length > 10 && (normalized.EndsWith('Z') || normalized.IndexOfAny(['+', '-'], 10) > 0);
        try
        {
            if (hasOffset)
                return DateTimeOffset.Parse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();
            var local = DateTime.Parse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None);
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), defaultOffset).ToUniversalTime();
        }
        catch (FormatException)
        {
            throw new IngestValueException(IngestCodes.InvalidTimestamp, $"Field '{field}' must be an ISO 8601 date-time.");
        }
    }

    /// <summary>
    /// A decimal written as a JSON number or a string. Strings may use a comma as the decimal separator and spaces between
    /// digit groups ("1 234,50"). Returns false when the text is not a number.
    /// </summary>
    public static bool TryDecimal(JsonElement e, out decimal value)
    {
        value = 0;
        if (e.ValueKind == JsonValueKind.Number) return e.TryGetDecimal(out value);
        if (e.ValueKind != JsonValueKind.String) return false;
        var text = new string(e.GetString()!.Where(c => !char.IsWhiteSpace(c) && c != ' ' && c != ' ').ToArray());
        if (text.Contains(',') && !text.Contains('.')) text = text.Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Amount in currency units -> minor units (x100); a fraction of a minor unit (tiyin) is refused.</summary>
    public static long Money(JsonElement e, string field)
    {
        if (!TryDecimal(e, out var amount))
            throw new IngestValueException(IngestCodes.InvalidAmount, $"Field '{field}' must be a decimal amount (number or string).");
        decimal minor;
        try
        {
            minor = amount * 100m;
        }
        catch (OverflowException)
        {
            throw new IngestValueException(IngestCodes.AmountOutOfRange, $"Field '{field}' is out of range.");
        }
        if (minor != decimal.Truncate(minor))
            throw new IngestValueException(IngestCodes.FractionalMinorUnits, $"Field '{field}' has fractions of a minor unit (more than 2 decimals).");
        if (minor is > long.MaxValue / 1000 or < long.MinValue / 1000)
            throw new IngestValueException(IngestCodes.AmountOutOfRange, $"Field '{field}' is out of range.");
        return (long)minor;
    }

    public static decimal Decimal(JsonElement e, string field)
    {
        if (!TryDecimal(e, out var value) || Math.Abs(value) >= 1_000_000_000_000_000_000_000m)
            throw new IngestValueException(IngestCodes.InvalidNumber, $"Field '{field}' must be a decimal number.");
        return Math.Round(value, 6);
    }

    /// <summary>A VAT rate as a number ("12", "12%", 12); null for anything else (the source text is kept in extra).</summary>
    public static decimal? Rate(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            var text = e.GetString()!.Trim().TrimEnd('%').Trim();
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(text));
            return TryDecimal(doc.RootElement, out var v) && v is >= 0 and < 100 ? v : null;
        }
        return TryDecimal(e, out var n) && n is >= 0 and < 100 ? n : null;
    }

    public static int Int(JsonElement e, string field)
    {
        if (!TryDecimal(e, out var value) || value != decimal.Truncate(value) || value is < 0 or > 36_500)
            throw new IngestValueException(IngestCodes.InvalidNumber, $"Field '{field}' must be a whole number from 0 to 36500.");
        return (int)value;
    }

    public static bool Bool(JsonElement e, string field)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number when e.TryGetInt32(out var n) && n is 0 or 1: return n == 1;
            case JsonValueKind.String:
                switch (e.GetString()!.Trim().ToLowerInvariant())
                {
                    case "true" or "1" or "yes" or "да" or "истина" or "ha": return true;
                    case "false" or "0" or "no" or "нет" or "ложь" or "yo'q": return false;
                }
                break;
        }
        throw new IngestValueException(IngestCodes.InvalidBoolean, $"Field '{field}' must be true or false.");
    }

    private static readonly Dictionary<string, string> CurrencyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["сум"] = "UZS", ["сўм"] = "UZS", ["so'm"] = "UZS", ["som"] = "UZS", ["sum"] = "UZS", ["сум."] = "UZS",
        ["руб"] = "RUB", ["руб."] = "RUB", ["рубль"] = "RUB",
        ["доллар"] = "USD", ["долл."] = "USD", ["евро"] = "EUR",
    };

    /// <summary>Letter code (upper case), ISO numeric code (860, 840, 978, 643) or a known name -> letter code.</summary>
    public static string Currency(JsonElement e, string field)
    {
        var value = Scalar(e, field).Trim();
        if (CurrencyNames.TryGetValue(value, out var named)) return named;
        var code = IsoCurrency.Normalize(value);
        if (code.Length != 3 || !code.All(char.IsAsciiLetterUpper))
            throw new IngestValueException(IngestCodes.InvalidCurrency, $"Field '{field}' must be a currency code (e.g. UZS or 860).");
        return code;
    }

    public static AccountingDocumentKind DocKind(JsonElement e, string field) =>
        Scalar(e, field).Trim().ToLowerInvariant() switch
        {
            "sales" or "sale" or "realization" or "реализация" or "продажа" or "outgoing" => AccountingDocumentKind.Sales,
            "purchase" or "purchases" or "receipt" or "поступление" or "покупка" or "incoming" => AccountingDocumentKind.Purchase,
            _ => throw new IngestValueException(IngestCodes.InvalidValue, $"Field '{field}' must be Sales or Purchase."),
        };

    public static BankDocumentDirection Direction(JsonElement e, string field) =>
        Scalar(e, field).Trim().ToLowerInvariant() switch
        {
            "receipt" or "incoming" or "in" or "credit" or "поступление" => BankDocumentDirection.Receipt,
            "payment" or "outgoing" or "out" or "debit" or "списание" => BankDocumentDirection.Payment,
            _ => throw new IngestValueException(IngestCodes.InvalidValue, $"Field '{field}' must be Receipt or Payment."),
        };

    public const int MaxBarcodeLength = 64;
    public const int MaxBarcodes = 100;

    /// <summary>
    /// Barcodes of a catalog item: an array of strings (or numbers) of digits, or one such string. Order kept, duplicates
    /// dropped; anything that is not 1-64 digits refuses the item (a barcode is how the bot finds goods, a wrong one must
    /// not be stored silently).
    /// </summary>
    public static string[] Barcodes(JsonElement e, string field)
    {
        var values = new List<string>();
        if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in e.EnumerateArray())
                if (!IsMissing(x)) values.Add(Barcode(x, field));
        }
        else
        {
            values.Add(Barcode(e, field));
        }
        var distinct = values.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length > MaxBarcodes)
            throw new IngestValueException(IngestCodes.InvalidBarcode, $"Field '{field}' has more than {MaxBarcodes} barcodes.");
        return distinct;
    }

    private static string Barcode(JsonElement e, string field)
    {
        if (e.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            throw new IngestValueException(IngestCodes.InvalidBarcode, $"Field '{field}' must be an array of strings of digits.");
        var value = (e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).Trim();
        if (value.Length is 0 or > MaxBarcodeLength || !value.All(char.IsAsciiDigit))
            throw new IngestValueException(IngestCodes.InvalidBarcode, $"Field '{field}' must contain only strings of 1 to {MaxBarcodeLength} digits.");
        return value;
    }

    /// <summary>Goods / Service (Russian and Uzbek spellings too); null for any other text, which is kept in extra.</summary>
    public static string? ItemType(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) return null;
        return e.GetString()!.Trim().ToLowerInvariant() switch
        {
            "goods" or "good" or "product" or "item" or "товар" or "товары" or "tovar" => nameof(Domain.Catalog.CatalogItemType.Goods),
            "service" or "services" or "услуга" or "услуги" or "xizmat" or "хизмат" => nameof(Domain.Catalog.CatalogItemType.Service),
            _ => null,
        };
    }

    /// <summary>
    /// PostgreSQL refuses NUL characters and lone surrogates in text and jsonb; such an item is refused alone (instead of
    /// failing the whole package at write time).
    /// </summary>
    public static bool HasUnstorableText(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return Unstorable(e.GetString()!);
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    if (Unstorable(p.Name) || HasUnstorableText(p.Value)) return true;
                return false;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray())
                    if (HasUnstorableText(x)) return true;
                return false;
            default:
                return false;
        }
    }

    private static bool Unstorable(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\0') return true;
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(c)) return true;
        }
        return false;
    }
}
