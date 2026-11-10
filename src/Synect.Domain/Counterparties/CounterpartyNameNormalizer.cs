using System.Text;
using Synect.Domain.Text;

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

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        // 1. Lower case and Cyrillic to Latin (shared with the payment purpose parser); apostrophes (o', g', tutuq
        //    belgisi) are dropped, so "O'zbekiston", "Oʻzbekiston" and "Ўзбекистон" all give "ozbekiston".
        var latin = LatinTransliterator.ToLatinLower(name);

        // 2. Letters and digits only; everything else (quotes «» " „, dots, dashes, commas) separates words.
        var cleaned = new StringBuilder(latin.Length);
        foreach (var c in latin)
        {
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue; // combining accents
            cleaned.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        var text = LatinTransliterator.ApplyLatinVariants(cleaned.ToString());

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
