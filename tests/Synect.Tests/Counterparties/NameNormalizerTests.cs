using Synect.Domain.Counterparties;

namespace Synect.Tests.Counterparties;

public class NameNormalizerTests
{
    private static string N(string s) => CounterpartyNameNormalizer.Normalize(s);

    [Theory]
    [InlineData("ООО «Ромашка»", "romashka")]
    [InlineData("ООО \"РОМАШКА\"", "romashka")]
    [InlineData("Romashka MChJ", "romashka")]
    [InlineData("ROMASHKA MCHJ", "romashka")]
    [InlineData("«Ромашка» МЧЖ", "romashka")]
    [InlineData("OOO Romashka", "romashka")]
    [InlineData("Общество с ограниченной ответственностью \"Ромашка\"", "romashka")]
    [InlineData("Ромашка mas'uliyati cheklangan jamiyati", "romashka")]
    [InlineData("ИП Иванов", "ivanov")]
    [InlineData("ЯТТ Каримов", "karimov")]
    [InlineData("YaTT Karimov", "karimov")]
    [InlineData("АО \"Узпромстройбанк\"", "uzpromstroybank")]
    [InlineData("\"Uzpromstroybank\" AJ", "uzpromstroybank")]
    [InlineData("  Альфа   Трейд,  ООО.  ", "alfa treyd")]
    public void Removes_legal_forms_quotes_case_and_extra_spaces(string input, string expected) => Assert.Equal(expected, N(input));

    [Theory]
    [InlineData("Ўзбекистон темир йўллари", "ozbekiston temir yollari")]
    [InlineData("O'zbekiston temir yo'llari", "ozbekiston temir yollari")]
    [InlineData("OʻZBEKISTON TEMIR YOʻLLARI", "ozbekiston temir yollari")]
    [InlineData("Қурилиш сервис ХК", "kurilish servis")]
    [InlineData("Qurilish servis XK", "kurilish servis")]
    [InlineData("Ғалаба", "galaba")]
    [InlineData("G'alaba", "galaba")]
    [InlineData("Ҳамкор", "hamkor")]
    [InlineData("Хамкор", "hamkor")]
    [InlineData("Xamkor", "hamkor")]
    [InlineData("Khamkor", "hamkor")]
    [InlineData("Шодлик", "shodlik")]
    [InlineData("Чорсу", "chorsu")]
    [InlineData("Ёшлик", "yoshlik")]
    [InlineData("Жасур", "jasur")]
    [InlineData("Toşkent Çorsu Ğalaba", "toshkent chorsu galaba")]
    [InlineData("Zhasur", "jasur")]
    public void Transliterates_russian_and_uzbek_cyrillic_and_uzbek_latin_to_one_form(string input, string expected) =>
        Assert.Equal(expected, N(input));

    [Fact]
    public void Every_apostrophe_variant_of_o_and_g_gives_the_same_key()
    {
        var apostrophes = new[] { '\'', '`', (char)0x2018, (char)0x2019, (char)0x02BB, (char)0x02BC };
        foreach (var a in apostrophes)
            Assert.Equal("ozbekiston galaba", N($"O{a}zbekiston G{a}alaba"));
    }

    [Theory]
    [InlineData("Рассвет", "Rassvet")]
    [InlineData("ООО «Ромашка»", "Romashka MChJ")]
    [InlineData("Тошкент Шаҳар Қурилиш", "Toshkent Shahar Qurilish")]
    [InlineData("ЧП Юлдуз", "Yulduz")]
    [InlineData("Ўрта Осиё Савдо", "O'rta Osiyo Savdo")]
    public void Cyrillic_and_latin_spellings_meet_in_both_directions(string cyrillic, string latin)
    {
        Assert.Equal(N(cyrillic), N(latin));
        Assert.Equal(N(cyrillic), N(N(latin)));   // normalizing again changes nothing
        Assert.Equal(N(latin), N(N(cyrillic)));
    }

    [Theory]
    [InlineData("ООО", "ooo")]
    [InlineData("\"MChJ\"", "mchj")]
    [InlineData("ИП", "ip")]
    public void A_name_made_only_of_a_legal_form_is_kept(string input, string expected) => Assert.Equal(expected, N(input));

    [Theory]
    [InlineData("Ромашка 2000", "romashka 2000")]
    [InlineData("Ромашка-Агро", "romashka agro")]
    [InlineData("Alpha Pharm", "alfa farm")]
    [InlineData("Café Ромашка", "cafe romashka")]
    public void Keeps_the_name_itself(string input, string expected) => Assert.Equal(expected, N(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("«»\"—")]
    public void Empty_or_sign_only_input_gives_an_empty_key(string? input) => Assert.Equal("", N(input!));
}
