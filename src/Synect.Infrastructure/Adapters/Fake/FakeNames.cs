namespace Synect.Infrastructure.Adapters.Fake;

/// <summary>
/// Plausible company names for the fake bank and Didox, so that demo answers read like real ones. Deterministic: the same
/// seed or TIN always gives the same name, and the bank and Didox agree on a partner's name (both derive it from the TIN).
/// </summary>
public static class FakeNames
{
    private static readonly string[] Clients =
    [
        "SAMARQAND SAVDO", "TOSHKENT MEBEL", "NUR FARM", "ORIENT TEXTILE", "ZARAFSHON AGRO", "BARAKA FOOD", "SILK ROAD TRADE",
        "UNIVERSAL STROY", "AL-FAYZ", "MEGA PLAST",
    ];

    private static readonly string[] Words =
    [
        "AGRO", "TEXTILE", "STROY", "SAVDO", "LOGISTIC", "PLAST", "TRADE", "FARM", "OIL", "METALL", "SERVIS", "PRINT", "ENERGY",
        "FOOD", "AVTO", "INVEST", "DORI", "TECH", "MARKET", "QURILISH",
    ];

    private static readonly string[] Prefixes =
    [
        "ASIA", "GRAND", "ZAMIN", "OLTIN", "YANGI", "BUYUK", "SHARQ", "ANDIJON", "FARG'ONA", "BUXORO", "NAVOIY", "KAMOLOT",
        "IDEAL", "PREMIUM", "STAR", "ULUG'BEK", "TURON", "SAHOVAT", "BARKAMOL", "DIYOR",
    ];

    /// <summary>Name of the demo client organization (the account owner) for a fake connection seed.</summary>
    public static string Client(int seed) => $"OOO \"{Clients[Math.Abs(seed) % Clients.Length]}\"";

    /// <summary>Name of a partner company by its TIN.</summary>
    public static string Partner(string tin)
    {
        var n = long.TryParse(tin, out var v) ? v : tin.GetHashCode();
        n = Math.Abs(n);
        return $"OOO \"{Prefixes[n % Prefixes.Length]} {Words[n / Prefixes.Length % Words.Length]}\"";
    }
}
