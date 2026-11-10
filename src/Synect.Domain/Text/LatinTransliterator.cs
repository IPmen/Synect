using System.Text;

namespace Synect.Domain.Text;

/// <summary>
/// Shared transliteration of Russian and Uzbek text (Cyrillic and Latin, any case) into one plain lower-case Latin
/// form, used by counterparty name search (<see cref="Counterparties.CounterpartyNameNormalizer"/>) and by the
/// payment purpose parser of the reconciliation. One table for Russian and Uzbek Cyrillic (ў→o, қ→k, ғ→g, ҳ/х→h, ш→sh,
/// ч→ch, ж→j, ё→yo), accented Latin letters to plain ones; the apostrophes of o' and g' (tutuq belgisi) are dropped.
/// </summary>
public static class LatinTransliterator
{
    /// <summary>Cyrillic (Russian and Uzbek) to Latin, close to the official Uzbek Latin alphabet.</summary>
    private static readonly Dictionary<char, string> Cyrillic = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo", ['ж'] = "j",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sh", ['ъ'] = "", ['ы'] = "i", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya",
        // Uzbek letters.
        ['ў'] = "o", ['қ'] = "k", ['ғ'] = "g", ['ҳ'] = "h",
        // Kazakh / Karakalpak letters sometimes seen in names.
        ['ә'] = "a", ['ө'] = "o", ['ү'] = "u", ['ң'] = "n", ['і'] = "i",
        // Latin letters with diacritics (the app runs with invariant globalization, so string.Normalize cannot be
        // relied on). Uzbek Latin of 1993-1995 used ş, ç, ğ, ö for sh, ch, g', o'.
        ['ş'] = "sh", ['ç'] = "ch", ['ğ'] = "g", ['ı'] = "i", ['ö'] = "o", ['ü'] = "u",
        ['à'] = "a", ['á'] = "a", ['â'] = "a", ['ã'] = "a", ['ä'] = "a", ['å'] = "a",
        ['è'] = "e", ['é'] = "e", ['ê'] = "e", ['ë'] = "e",
        ['ì'] = "i", ['í'] = "i", ['î'] = "i", ['ï'] = "i",
        ['ò'] = "o", ['ó'] = "o", ['ô'] = "o", ['õ'] = "o", ['ø'] = "o",
        ['ù'] = "u", ['ú'] = "u", ['û'] = "u", ['ñ'] = "n", ['ý'] = "y", ['ÿ'] = "y",
    };

    /// <summary>Latin spellings of the same sound, after transliteration (applied in this order).</summary>
    private static readonly (string From, string To)[] LatinVariants =
    [
        ("kh", "h"), // Russian-style "Khamkor" = Uzbek "Hamkor" / "Xamkor"
        ("ph", "f"), // "Alpha" = "Alfa" = "Альфа"
        ("zh", "j"), // "Zhasur" = "Jasur"
        ("x", "h"),  // Uzbek Latin x = Cyrillic х
        ("q", "k"),  // Uzbek Latin q = қ, often written к in Russian spelling
        ("w", "v"),
    ];

    /// <summary>Apostrophe-like characters (o', g', tutuq belgisi, typographic quotes used as apostrophes).</summary>
    public static readonly IReadOnlySet<char> Apostrophes = new HashSet<char>
        { '\'', '`', (char)0x00B4, (char)0x02BB, (char)0x02BC, (char)0x2018, (char)0x2019, (char)0x02B9, (char)0x02C8 };

    public static bool IsApostrophe(char c) => Apostrophes.Contains(c);

    /// <summary>
    /// Lower case and Cyrillic (and accented Latin) to plain Latin; apostrophes are dropped, so "O'zbekiston",
    /// "Oʻzbekiston" and "Ўзбекистон" all give "ozbekiston". Every other character is kept as is.
    /// </summary>
    public static string ToLatinLower(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var latin = new StringBuilder(text.Length + 8);
        foreach (var raw in text.ToLowerInvariant())
        {
            if (Apostrophes.Contains(raw)) continue;
            if (Cyrillic.TryGetValue(raw, out var mapped)) latin.Append(mapped);
            else latin.Append(raw);
        }
        return latin.ToString();
    }

    /// <summary>Unifies Latin spelling variants of one sound (kh/h, x/h, q/k, zh/j, ph/f, w/v) in lower-case Latin text.</summary>
    public static string ApplyLatinVariants(string latinLower)
    {
        var text = latinLower;
        foreach (var (from, to) in LatinVariants) text = text.Replace(from, to, StringComparison.Ordinal);
        return text;
    }
}
