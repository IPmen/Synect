namespace Synect.Application.Statements;

/// <summary>
/// Decimal amounts of statement files (in sum) to minor units (tiyin): strictly times 100, never rounded. A value with
/// non-zero digits beyond the second decimal place ("12,345") is refused: the row is rejected with a warning.
/// Trailing zeros are fine ("12,3400"). The amount unit of the Kapital Bank API is not affected by this.
/// </summary>
public static class StatementAmounts
{
    public enum Error
    {
        None,
        Empty,
        Invalid,
        FractionalMinorUnits,
        TooLarge,
    }

    /// <param name="decimalSeparator">',' or '.', or null for auto (the last of ',' and '.' is the decimal separator).</param>
    /// <param name="value">Signed minor units.</param>
    public static Error TryParse(string? text, char? decimalSeparator, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return Error.Empty;
        var s = text.Trim();
        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1].Trim();
        }
        if (s.StartsWith('-') || s.StartsWith('−'))
        {
            negative = !negative;
            s = s[1..].Trim();
        }
        else if (s.StartsWith('+'))
        {
            s = s[1..].Trim();
        }
        else if (s.EndsWith('-'))
        {
            negative = !negative;
            s = s[..^1].Trim();
        }

        // Thousands separators that never mean a decimal point.
        s = s.Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace("'", "").Replace("’", "");
        if (s.Length == 0) return Error.Invalid;

        var sep = decimalSeparator ?? (s.LastIndexOfAny([',', '.']) is var i and >= 0 ? s[i] : '.');
        var other = sep == ',' ? '.' : ',';
        s = s.Replace(other.ToString(), ""); // the other sign can only be a thousands separator
        var parts = s.Split(sep);
        if (parts.Length > 2) return Error.Invalid;
        var whole = parts[0];
        var fraction = parts.Length == 2 ? parts[1] : "";
        if (whole.Length == 0 && fraction.Length == 0) return Error.Invalid;
        if (!whole.All(char.IsAsciiDigit) || !fraction.All(char.IsAsciiDigit)) return Error.Invalid;
        if (fraction.Length > 2)
        {
            if (fraction[2..].Any(c => c != '0')) return Error.FractionalMinorUnits;
            fraction = fraction[..2];
        }
        fraction = fraction.PadRight(2, '0');
        whole = whole.TrimStart('0');
        if (whole.Length > 16) return Error.TooLarge;
        try
        {
            var units = checked((whole.Length == 0 ? 0 : long.Parse(whole, System.Globalization.CultureInfo.InvariantCulture)) * 100
                                + int.Parse(fraction, System.Globalization.CultureInfo.InvariantCulture));
            value = negative ? -units : units;
            return Error.None;
        }
        catch (OverflowException)
        {
            return Error.TooLarge;
        }
    }

    /// <summary>"1 234 567,89" for messages (minor units in, sum out).</summary>
    public static string Format(long minor)
    {
        var sign = minor < 0 ? "-" : "";
        var abs = Math.Abs((decimal)minor) / 100m;
        return sign + abs.ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture).Replace(",", " ").Replace(".", ",");
    }

    public static string Describe(Error error) => error switch
    {
        Error.Empty => "the amount is empty",
        Error.Invalid => "the amount is not a number",
        Error.FractionalMinorUnits => "the amount has fractions of a tiyin (more than two decimal places); it is not rounded",
        Error.TooLarge => "the amount is too large",
        _ => "",
    };
}
