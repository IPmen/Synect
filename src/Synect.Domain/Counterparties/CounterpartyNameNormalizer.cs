using System.Text;

namespace Synect.Domain.Counterparties;

/// <summary>
/// Turns a counterparty name (Russian, Uzbek Cyrillic or Uzbek Latin, any case, with quotes and legal forms) into one
/// canonical Latin search key, so that «ООО "Ромашка"», "Romashka MChJ" and "РОМАШКА МЧЖ" give the same string.
/// Only for search: the original name is always kept next to it.
/// Steps: lower case; one apostrophe; Cyrillic (and accented Latin) to plain Latin with one table for Russian and Uzbek; Latin spelling variants
/// to one form; quotes and other signs to spaces; legal forms removed as whole words; spaces collapsed.
/// When removing legal forms would leave nothing (a name that is only "ООО"), they are kept.
/// </summary>
public static class CounterpartyNameNormalizer
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

    /// <summary>Legal forms (already canonical: transliterated, lower case, no signs). Multi-word forms first.</summary>
    private static readonly string[][] LegalForms =
    [
        ["masuliyati", "cheklangan", "jamiyati"],
        ["masuliyati", "cheklangan", "jamiyat"],
        ["obshestvo", "s", "ogranichennoy", "otvetstvennostyu"],
        ["obshestvo", "s", "ogranichennoi", "otvetstvennostyu"],
        ["aksiyadorlik", "jamiyati"],
        ["aktsionernoe", "obshestvo"],
        ["yakka", "tartibdagi", "tadbirkor"],
        ["individualniy", "predprinimatel"],
        ["individualnyy", "predprinimatel"],
        ["hususiy", "korhona"],
        ["limited", "liability", "company"],
        ["ooo"], ["oao"], ["zao"], ["pao"], ["ao"], ["ip"], ["chp"], ["mchj"], ["yatt"], ["aj"], ["hk"], ["kk"],
        ["llc"], ["ltd"], ["jsc"], ["mchj"], ["dup"], ["gup"], ["unitarnoe", "predpriyatie"],
    ];

    private static readonly HashSet<char> Apostrophes =
        ['\'', '`', (char)0x00B4, (char)0x02BB, (char)0x02BC, (char)0x2018, (char)0x2019, (char)0x02B9, (char)0x02C8];

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        // 1. Lower case and Cyrillic to Latin; apostrophes (o', g', tutuq belgisi) and other signs become separators
        //    later, so "O'zbekiston", "Oʻzbekiston" and "Ўзбекистон" all give "ozbekiston".
        var latin = new StringBuilder(name.Length + 8);
        foreach (var raw in name.ToLowerInvariant())
        {
            if (Apostrophes.Contains(raw)) continue;
            if (Cyrillic.TryGetValue(raw, out var mapped)) latin.Append(mapped);
            else latin.Append(raw);
        }

        // 2. Letters and digits only; everything else (quotes «» " „, dots, dashes, commas) separates words.
        var cleaned = new StringBuilder(latin.Length);
        foreach (var c in latin.ToString())
        {
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue; // combining accents
            cleaned.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        var text = cleaned.ToString();
        foreach (var (from, to) in LatinVariants) text = text.Replace(from, to, StringComparison.Ordinal);

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var withoutForms = RemoveLegalForms(words);
        return string.Join(' ', withoutForms.Count > 0 ? withoutForms : words);
    }

    private static List<string> RemoveLegalForms(List<string> words)
    {
        var result = new List<string>(words.Count);
        for (var i = 0; i < words.Count;)
        {
            var form = LegalForms.FirstOrDefault(f => i + f.Length <= words.Count && f.Select((w, k) => words[i + k] == w).All(x => x));
            if (form is not null)
            {
                i += form.Length;
                continue;
            }
            result.Add(words[i]);
            i++;
        }
        return result;
    }
}
