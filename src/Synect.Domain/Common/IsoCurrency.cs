namespace Synect.Domain.Common;

/// <summary>
/// Normalizes currency codes at load time: ISO 4217 numeric codes of the currencies Synect sees become alphabetic
/// (860 and the Kapital Bank account code 000 -> UZS, 840 -> USD, 978 -> EUR, 643 -> RUB); alphabetic codes are upper-cased;
/// an unknown numeric code is kept as is (the source value always stays in the raw JSON).
/// </summary>
public static class IsoCurrency
{
    private static readonly Dictionary<string, string> Numeric = new(StringComparer.Ordinal)
    {
        ["000"] = "UZS", // Kapital Bank account numbers use 000 for the national currency
        ["860"] = "UZS",
        ["840"] = "USD",
        ["978"] = "EUR",
        ["643"] = "RUB",
    };

    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        var trimmed = code.Trim();
        if (Numeric.TryGetValue(trimmed, out var alpha)) return alpha;
        return trimmed.All(char.IsAsciiLetter) ? trimmed.ToUpperInvariant() : trimmed;
    }

    /// <summary>True for a 3-digit code that <see cref="Normalize"/> turns into a letter code.</summary>
    public static bool IsKnownNumeric(string? code) => code is not null && Numeric.ContainsKey(code.Trim());

    public static IReadOnlyCollection<string> KnownNumericCodes => Numeric.Keys;
}
