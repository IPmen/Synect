using Synect.Application.Reconciliation;

namespace Synect.Tests.Reconciliation;

/// <summary>Payment purpose parsing on realistic Russian and Uzbek (Latin and Cyrillic) purposes, including errors and garbage.</summary>
public class PurposeParserTests
{
    private static readonly PurposeParser Parser = PurposeParser.Default;

    /// <summary>purpose | document keys (first key of each reference) | contract keys.</summary>
    public static TheoryData<string, string, string> Purposes => new()
    {
        // Russian.
        { "Оплата по счет-фактуре №СФ-101 от 01.10.2026 за товар", "101", "" },
        { "Оплата по с/ф № 15 от 12.10.2026", "15", "" },
        { "Оплата по сч/ф 15/2026", "152026", "" },
        { "Оплата по ЭСФ №000123", "123", "" },
        { "Оплата по счету №45 от 05.10.2026г.", "45", "" },
        { "Оплата по договору №7/2026 от 01.02.2026", "", "72026" },
        { "Оплата по дог. № 12-А", "", "12A" },
        { "Оплата по договору поставки №15 и счет-фактуре №21", "21", "15" },
        { "Оплата за услуги согласно акту выполненных работ №3 от 30.09.2026", "3", "" },
        { "Предоплата по договору №ДП-45/26 за оборудование", "", "DP4526" },
        { "Оплата по инвойсу INV-0021", "21", "" },
        { "Оплата по INV-2026-0042", "202642", "" },
        { "Оплата по счету-фактуре N123 от 01.10.26", "123", "" },
        { "Оплата по с/ф №№ 10, 11, 12", "10,11,12", "" },
        { "Оплата по счетам-фактурам G-10 и G-11", "G10,G11", "" },
        { "оплата сф12 и сф13", "12,13", "" },
        { "Оплата по счету 1234567890123456789 по договору 5", "", "5" },
        { "Оплата за товар по накладной №ТТН-77 от 03.10.2026", "77", "" },
        { "Оплата по счёт-фактуре № 0045 от 2026-10-02", "45", "" },
        { "За услуги связи по договору №ABC-12 (НДС не облагается)", "", "ABC12" },
        { "Оплата по счету-фактуре 15 от 01.10.2026 и счету-фактуре 16 от 02.10.2026", "15,16", "" },
        { "Оплата по договору № 15 от 01.02.2026 по счету-фактуре № 21 от 01.10.2026", "21", "15" },
        { "ООО «Ромашка» оплата по с\\ф 56", "56", "" },
        { "оплата по счет фактуре 57", "57", "" },
        { "Oplata po schet-fakture №55", "55", "" },
        { "Payment for invoice No. 778 under contract 12", "778", "12" },
        { "Частичная оплата по с/ф №СФ-103", "103", "" },
        // Uzbek Latin.
        { "hisob-faktura №45 bo'yicha to'lov", "45", "" },
        { "45-sonli hisob-faktura bo'yicha to'lov", "45", "" },
        { "15-sonli shartnoma asosida 21-son hisob-fakturaga ko'ra", "21", "15" },
        { "Shartnoma №12/2026 bo'yicha xizmatlar uchun to'lov", "", "122026" },
        { "2026 yil 1 oktyabrdagi 7-sonli shartnomaga asosan to'lov", "", "7" },
        { "N 34 hisob-faktura uchun", "34", "" },
        { "To'lov 3-oktyabr 2026 yildagi 88-sonli hisobvaraq bo‘yicha", "88", "" },
        { "Xisob-faktura № 61 bo`yicha", "61", "" },
        // Uzbek Cyrillic.
        { "ҳисоб-фактура №21 бўйича тўлов", "21", "" },
        { "21-сонли ҳисобварақ-фактура учун", "21", "" },
        { "Шартнома №5 асосида", "", "5" },
        { "45-сонли ҳисоб-фактура бўйича тўлов", "45", "" },
        // No references, errors and garbage.
        { "Возврат излишне перечисленных средств", "", "" },
        { "Перечисление заработной платы за октябрь 2026", "", "" },
        { "Комиссия банка за обслуживание счета 20208000900123456001", "", "" },
        { "НДС 12% 107142.86 сум", "", "" },
        { "Оплата по акту сверки", "", "" },
        { "Оплата по договору б/н", "", "" },
        { "СФ-", "", "" },
        { "№№№ ,,, ;;; 123", "", "" },
        { "<script>alert(1)</script> Оплата по сф 99", "99", "" },
        { "Оплата за услуги", "", "" },
        { "   ", "", "" },
    };

    [Theory]
    [MemberData(nameof(Purposes))]
    public void Extracts_document_and_contract_numbers(string purpose, string documents, string contracts)
    {
        var parsed = Parser.Parse(purpose);
        Assert.Equal(Split(documents), parsed.Documents.Select(r => r.Keys[0]).ToList());
        Assert.Equal(Split(contracts), parsed.Contracts.Select(r => r.Keys[0]).ToList());
        Assert.Equal(documents.Length > 0 || contracts.Length > 0, parsed.HasReference);
    }

    [Fact]
    public void Null_and_empty_purposes_have_no_references() =>
        Assert.All(new string?[] { null, "", " \t\r\n" }, p => Assert.Same(ParsedPurpose.Empty, Parser.Parse(p)));

    [Fact]
    public void Dates_are_taken_from_numbers_and_month_names()
    {
        var p = Parser.Parse("Оплата по счету-фактуре 15 от 01.10.2026 и счету-фактуре 16 от 02.10.2026");
        Assert.Equal([new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)], p.Documents.Select(d => d.Date!.Value));
        Assert.Contains(new DateOnly(2026, 10, 1), Parser.Parse("2026 yil 1 oktyabrdagi 7-sonli shartnomaga asosan to'lov").Dates);
        Assert.Contains(new DateOnly(2026, 10, 5), Parser.Parse("Оплата по счету от 5 октября 2026 года").Dates);
        Assert.Contains(new DateOnly(2026, 10, 1), Parser.Parse("Оплата по с/ф N123 от 01.10.26").Dates);
        Assert.Empty(Parser.Parse("Оплата по с/ф 1 от 45.13.2026").Dates);
    }

    [Fact]
    public void Kinds_distinguish_invoices_acts_other_documents_and_codes()
    {
        Assert.Equal(PurposeReferenceKind.Invoice, Parser.Parse("Оплата по счет-фактуре №5").Documents.Single().Kind);
        Assert.Equal(PurposeReferenceKind.Act, Parser.Parse("Оплата по акту №5").Documents.Single().Kind);
        Assert.Equal(PurposeReferenceKind.Act, Parser.Parse("5-sonli dalolatnoma bo'yicha").Documents.Single().Kind);
        Assert.Equal(PurposeReferenceKind.OtherDocument, Parser.Parse("Оплата по счету №5").Documents.Single().Kind);
        var code = Parser.Parse("Оплата за товар ABC-0042").Documents.Single();
        Assert.Equal((PurposeReferenceKind.Code, "ABC42"), (code.Kind, code.Keys[0]));
        Assert.Equal(PurposeReferenceKind.Invoice, Parser.Parse("Оплата за товар INV-0042").Documents.Single().Kind); // "inv" is a keyword
    }

    [Fact]
    public void Year_suffix_gives_a_second_key()
    {
        var reference = Parser.Parse("Оплата по договору №15/2026").Contracts.Single();
        Assert.Equal(["152026", "15"], reference.Keys);
    }

    [Theory]
    [InlineData("101", "СФ-101", true)]
    [InlineData("101", "SF-0101", true)]
    [InlineData("1", "0001", true)]
    [InlineData("INV21", "INV-0021", true)]
    [InlineData("21", "INV-0021", true)]
    [InlineData("12", "123", false)]
    [InlineData("101", null, false)]
    public void Reference_keys_match_document_numbers(string key, string? number, bool expected) =>
        Assert.Equal(expected, ReferenceKey.Matches(new PurposeReference(PurposeReferenceKind.Invoice, key, [key], null), number));

    [Fact]
    public void Codes_match_only_the_full_number() =>
        Assert.False(ReferenceKey.Matches(new PurposeReference(PurposeReferenceKind.Code, "x", ["21"], null), "INV-21"));

    [Theory]
    [InlineData("INV-0012", "INV12")]
    [InlineData("сф 0001", "SF1")]
    [InlineData("100", "100")]
    [InlineData("0", "0")]
    [InlineData("A-0/2026", "A02026")]
    public void Full_key_drops_signs_and_leading_zeros(string number, string expected) => Assert.Equal(expected, ReferenceKey.Full(number));

    [Fact]
    public void Dictionary_loads_from_the_embedded_resource()
    {
        var dictionary = PurposeDictionary.Load();
        Assert.Contains(dictionary.Keywords, k => k.Words.SequenceEqual(["hisob-faktura"]) && k.Kind == PurposeReferenceKind.Invoice);
        Assert.Contains(dictionary.Keywords, k => k.Words.SequenceEqual(["shartnoma"]) && k.Kind == PurposeReferenceKind.Contract);
        Assert.Contains("sonli", dictionary.UzbekSuffixes);
        Assert.Equal(10, dictionary.Months["oktyabr"]);
    }

    private static List<string> Split(string keys) => keys.Length == 0 ? [] : keys.Split(',').ToList();
}
