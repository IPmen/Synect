using System.Reflection;
using System.Text;
using Synect.Application.Statements;
using Synect.Domain.Banking;

namespace Synect.Tests.Statements;

internal static class StatementFiles
{
    public static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Statements");

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(Directory, name));

    public static ParsedStatementFile Mt940(string name, StatementProfile? profile = null) =>
        new Mt940Parser().Parse(Read(name), profile ?? StatementProfiles.GenericMt940);

    public static ParsedStatementFile Csv(string name, StatementProfile? profile = null) =>
        new CsvStatementParser().Parse(Read(name), profile ?? StatementProfiles.GenericCsv);

    public static StatementProfile CustomCsvProfile() => StatementProfile.FromJson(File.ReadAllText(Path.Combine(Directory, "profile_cp866_bank.json")));
}

/// <summary>Statement parsers on the synthetic fixtures (no database).</summary>
public class StatementParserTests
{
    private static readonly DateOnly Nov2 = new(2026, 11, 2), Nov3 = new(2026, 11, 3), Nov4 = new(2026, 11, 4), Nov5 = new(2026, 11, 5);

    [Fact]
    public void Mt940_single_statement_with_multiline_86_and_markers()
    {
        var file = StatementFiles.Mt940("mt940_single_utf8.sta");
        Assert.Equal("utf-8", file.Encoding);
        var s = Assert.Single(file.Statements);
        Assert.Equal("20208000900100001001", s.AccountNumber);
        Assert.Equal("UZS", s.Currency);
        Assert.Equal(new ParsedBalance(Nov2, 10_000_000_00), s.Opening);
        Assert.Equal(new ParsedBalance(Nov3, 10_249_999_50), s.Closing);
        Assert.Equal((Nov2, Nov3), (s.PeriodFrom, s.PeriodTo));
        Assert.Equal(3, s.Operations.Count);

        var first = s.Operations[0];
        Assert.Equal((Nov2, TransactionDirection.Incoming, 1_500_000_00L), (first.PostingDate, first.Direction, first.Amount));
        Assert.Equal("301234567", first.CounterpartyInn);
        Assert.Equal("20208000100200300401", first.CounterpartyAccount);
        Assert.Equal("00440", first.CounterpartyMfo);
        Assert.Equal("145", first.DocumentNumber);
        Assert.Contains("в т.ч. НДС 12%", first.Purpose); // lines joined with a space
        Assert.Equal("NTRF", first.TransactionType);
        Assert.Null(first.FileOperationId); // generic profile: computed ids

        var second = s.Operations[1];
        Assert.Equal((TransactionDirection.Outgoing, 250_000_50L), (second.Direction, second.Amount));
        Assert.Equal("302345678", second.CounterpartyInn); // INN at the start of :86:
        Assert.Equal("Д-17", second.DocumentNumber);

        var third = s.Operations[2];
        Assert.Equal(Nov3, third.PostingDate); // no entry date: the value date
        Assert.Null(third.CounterpartyInn);
        Assert.Equal("11", third.DocumentNumber);
        Assert.Empty(file.Warnings);
    }

    [Fact]
    public void Mt940_several_statements_windows_1251_reversal_and_pinfl()
    {
        var file = StatementFiles.Mt940("mt940_multi_cp1251.sta");
        Assert.Equal("windows-1251", file.Encoding);
        Assert.Equal(3, file.Statements.Count);
        Assert.Equal(["20208000900100001001", "20208000900100001001", "20208840900100002002"], file.Statements.Select(s => s.AccountNumber));
        Assert.Equal("USD", file.Statements[2].Currency);

        var day1 = file.Statements[0].Operations;
        Assert.Contains("ООО «Альфа Трейд»", day1[0].Purpose);
        Assert.Equal("301111111", day1[0].CounterpartyInn);
        Assert.Equal("СФ-210", day1[0].DocumentNumber);

        var reversal = file.Statements[1].Operations[0];
        Assert.True(reversal.IsReversal);
        Assert.Equal(TransactionDirection.Outgoing, reversal.Direction); // RC: a credit taken back
        Assert.Equal(3_000_000_00, reversal.Amount);
        var pinfl = file.Statements[1].Operations[1];
        Assert.Equal("41234567890123", pinfl.CounterpartyInn); // 14-digit PINFL of a sole proprietor
        Assert.Equal("ИП Каримов А.Б.", pinfl.CounterpartyName);
        Assert.Equal("7/2026", pinfl.DocumentNumber);
        Assert.Equal("20208840100200300401", file.Statements[2].Operations[0].CounterpartyAccount);
    }

    [Fact]
    public void Mt940_uzbek_latin_and_cyrillic_in_a_swift_envelope()
    {
        var file = StatementFiles.Mt940("mt940_uzbek_utf8bom.sta");
        Assert.Equal("utf-8", file.Encoding);
        var s = Assert.Single(file.Statements);
        Assert.Equal("20208000900100003003", s.AccountNumber);
        Assert.Equal("00440", s.BankCode);
        var (latin, cyrillic, storno) = (s.Operations[0], s.Operations[1], s.Operations[2]);

        Assert.Equal("305678901", latin.CounterpartyInn);
        Assert.Equal("Bog'bon Savdo MCHJ", latin.CounterpartyName);
        Assert.Equal("77", latin.DocumentNumber); // "77-sonli"

        Assert.Equal("306789012", cyrillic.CounterpartyInn); // СТИР
        Assert.Equal("«Боғбон» МЧЖ", cyrillic.CounterpartyName);
        Assert.Equal("12", cyrillic.DocumentNumber); // "12-сонли"
        Assert.Equal("20208000500600700801", cyrillic.CounterpartyAccount); // Ҳ/Р

        Assert.True(storno.IsReversal);
        Assert.Equal(TransactionDirection.Incoming, storno.Direction); // RD: a debit given back
        Assert.Equal("306789012", storno.CounterpartyInn);
        Assert.DoesNotContain(file.Warnings, w => w.Code == "text_outside_tags");
    }

    [Fact]
    public void Assumed_kapitalbank_profile_joins_continued_statement_and_wrapped_lines()
    {
        // The Kapital Bank GetMT940 structure in this fixture and profile is ASSUMED, not verified on a real file.
        var file = StatementFiles.Mt940("mt940_kapitalbank_assumed.sta", StatementProfiles.KapitalBankMt940Assumed);
        var s = Assert.Single(file.Statements); // :62M: + :60M: joined
        Assert.Equal(5_000_000_00, s.Opening!.Amount);
        Assert.Equal(5_600_000_00, s.Closing!.Amount);
        Assert.Equal(2, s.Operations.Count);

        var a = s.Operations[0];
        Assert.Equal(("301111111", "ALFA TREYD MCHJ", "20208000100100100101", "00440", "СФ-311", "00668"),
            (a.CounterpartyInn, a.CounterpartyName, a.CounterpartyAccount, a.CounterpartyMfo, a.DocumentNumber, a.PurposeCode));
        Assert.Equal("Оплата по счет-фактуре СФ-311 от 03.11.2026", a.Purpose);
        var b = s.Operations[1];
        Assert.Equal("20208000800000000007", b.CounterpartyAccount); // wrapped in the middle of the number
        Assert.Equal("45", b.DocumentNumber);
        Assert.Equal("Оплата по договору 15/2026", b.Purpose);
    }

    [Fact]
    public void Bank_reference_profile_gives_file_operation_ids()
    {
        var profile = StatementProfiles.KapitalBankMt940Assumed;
        profile.OperationId = "bank_reference";
        var file = StatementFiles.Mt940("mt940_kapitalbank_assumed.sta", profile);
        Assert.Equal(["KB0000123", "KB0000124"], file.Statements[0].Operations.Select(o => o.FileOperationId));

        // NONREF is no id: the import computes one and says so.
        var generic = StatementProfiles.GenericMt940;
        generic.OperationId = "owner_reference";
        var single = StatementFiles.Mt940("mt940_single_utf8.sta", generic);
        Assert.All(single.Statements[0].Operations, o => Assert.Null(o.FileOperationId));
        Assert.Equal(3, single.Warnings.Count(w => w.Code == "no_reference"));
    }

    [Fact]
    public void Mt940_entry_date_across_new_year_and_value_date()
    {
        var text = """
            :20:Y
            :25:20208000900100001001
            :60F:C261231UZS100,00
            :61:2612310102C10,00NTRFNONREF
            :86:Перенос на январь
            :61:2701021231D5,00NTRFNONREF
            :86:Проведено в декабре
            :62F:C270102UZS105,00
            """;
        var file = new Mt940Parser().Parse(Encoding.UTF8.GetBytes(text), StatementProfiles.GenericMt940);
        var ops = file.Statements[0].Operations;
        Assert.Equal((new DateOnly(2027, 1, 2), (DateOnly?)new DateOnly(2026, 12, 31)), (ops[0].PostingDate, ops[0].ValueDate));
        Assert.Equal((new DateOnly(2026, 12, 31), (DateOnly?)new DateOnly(2027, 1, 2)), (ops[1].PostingDate, ops[1].ValueDate));
    }

    [Fact]
    public void Fraction_of_a_tiyin_rejects_the_row_with_a_warning()
    {
        var mt = StatementFiles.Mt940("mt940_fractional_tiyin.sta");
        Assert.Equal(1, mt.RowsRejected);
        Assert.Equal([200_00L], mt.Statements[0].Operations.Select(o => o.Amount));
        var warning = Assert.Single(mt.Warnings, w => w.Code == "fractional_minor_units");
        Assert.Equal(5, warning.Line);

        var csv = StatementFiles.Csv("csv_fractional_tiyin.csv");
        Assert.Equal(1, csv.RowsRejected);
        Assert.Equal([200_00L], csv.Statements[0].Operations.Select(o => o.Amount));
        Assert.Contains(csv.Warnings, w => w.Code == "fractional_minor_units" && w.Line == 2);
    }

    [Theory]
    [InlineData("empty.sta", "empty")]
    [InlineData("mt940_truncated.sta", "closing balance")]
    [InlineData("damaged_binary.sta", "not MT940")]
    public void Empty_truncated_and_binary_files_are_not_statements(string name, string expected)
    {
        var ex = Assert.Throws<StatementFormatException>(() => StatementFiles.Mt940(name));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Empty_or_header_only_csv_is_not_a_statement()
    {
        Assert.Throws<StatementFormatException>(() => new CsvStatementParser().Parse([], StatementProfiles.GenericCsv));
        Assert.Throws<StatementFormatException>(() => new CsvStatementParser().Parse(Encoding.UTF8.GetBytes("Счет;Дата;Дебет;Кредит\r\n"), StatementProfiles.GenericCsv));
        var noDate = Assert.Throws<StatementFormatException>(() => new CsvStatementParser().Parse(Encoding.UTF8.GetBytes("a;b\r\n1;2\r\n"), StatementProfiles.GenericCsv));
        Assert.Contains("date column", noDate.Message);
    }

    [Fact]
    public void Csv_semicolon_windows_1251_debit_credit_totals_row_and_balances()
    {
        var file = StatementFiles.Csv("csv_semicolon_cp1251.csv");
        Assert.Equal("windows-1251", file.Encoding);
        var s = Assert.Single(file.Statements);
        Assert.Equal("20208000900100007007", s.AccountNumber);
        Assert.Equal(3, s.Operations.Count); // the "Итого" row is skipped
        Assert.Equal(0, file.RowsRejected);
        Assert.Equal(new ParsedBalance(Nov2, 10_000_000_00), s.Opening);
        Assert.Equal(new ParsedBalance(Nov3, 10_249_999_50), s.Closing);
        var first = s.Operations[0];
        Assert.Equal((TransactionDirection.Incoming, 1_500_000_00L, "301234567", "101"), (first.Direction, first.Amount, first.CounterpartyInn, first.DocumentNumber));
        Assert.Equal("ООО \"Ромашка\"", first.CounterpartyName);
        Assert.Equal("Оплата по счету-фактуре № 145; в т.ч. НДС", first.Purpose); // quoted delimiter
        Assert.Equal((TransactionDirection.Outgoing, 250_000_50L), (s.Operations[1].Direction, s.Operations[1].Amount));
    }

    [Fact]
    public void Csv_comma_utf8_bom_signed_amount_inn_and_document_from_purpose()
    {
        var file = StatementFiles.Csv("csv_comma_utf8bom_signed.csv");
        var s = Assert.Single(file.Statements);
        Assert.Null(s.Opening);
        var ops = s.Operations;
        Assert.Equal([(TransactionDirection.Incoming, 1_250_00L), (TransactionDirection.Outgoing, 300_25L), (TransactionDirection.Incoming, 99_10L)],
            ops.Select(o => (o.Direction, o.Amount)));
        Assert.Equal(("301111111", "77", "Alfa Treyd, MCHJ"), (ops[0].CounterpartyInn, ops[0].DocumentNumber, ops[0].CounterpartyName));
        Assert.Equal(("302222222", "5/2026", "20208000800000000007"), (ops[1].CounterpartyInn, ops[1].DocumentNumber, ops[1].CounterpartyAccount));
        Assert.Equal("303333333", ops[2].CounterpartyInn);
        Assert.Equal("UZS", s.Currency);
    }

    [Fact]
    public void Csv_custom_profile_cp866_title_line_and_direction_column()
    {
        var profile = StatementFiles.CustomCsvProfile();
        Assert.Equal("test_bank_cp866", profile.Name);
        var file = StatementFiles.Csv("csv_pipe_cp866.csv", profile);
        Assert.Equal("cp866", file.Encoding);
        var s = Assert.Single(file.Statements);
        Assert.Equal("20208000900100009009", s.AccountNumber); // from the profile
        Assert.Equal(2, s.Operations.Count);
        Assert.Equal((TransactionDirection.Incoming, 5_000_00L, "301234567", "ООО Ромашка", "501"),
            (s.Operations[0].Direction, s.Operations[0].Amount, s.Operations[0].CounterpartyInn, s.Operations[0].CounterpartyName, s.Operations[0].DocumentNumber));
        Assert.Equal((TransactionDirection.Outgoing, "302345678", "17"),
            (s.Operations[1].Direction, s.Operations[1].CounterpartyInn, s.Operations[1].DocumentNumber)); // INN and number from the purpose

        // Auto detection reads the same CP866 file.
        profile.Encoding = "auto";
        Assert.Equal("cp866", StatementFiles.Csv("csv_pipe_cp866.csv", profile).Encoding);
    }

    [Theory]
    [InlineData("utf-8-bom")]
    [InlineData("utf-8")]
    [InlineData("windows-1251")]
    [InlineData("cp866")]
    public void Encoding_is_detected(string encoding)
    {
        const string text = "Оплата по счету-фактуре № 145 от 28.10.2026 за строительные материалы, в т.ч. НДС; ИНН 301234567 ООО «Ромашка»";
        var bytes = encoding switch
        {
            "utf-8-bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
            "utf-8" => Encoding.UTF8.GetBytes(text),
            _ => StatementText.EncodingOf(encoding).GetBytes(text.Replace('«', '"').Replace('»', '"').Replace("№", encoding == "cp866" ? "N" : "№")),
        };
        var (decoded, detected) = StatementText.Decode(bytes, "auto");
        Assert.Equal(encoding == "utf-8-bom" ? "utf-8" : encoding, detected);
        Assert.Contains("Оплата по счету-фактуре", decoded);
        Assert.Contains("ИНН 301234567", decoded);
    }

    [Theory]
    [InlineData("1 234,56", null, 123456L)]
    [InlineData("1 500 000,00", ',', 150000000L)]
    [InlineData("100,500", ',', 10050L)]
    [InlineData("1,250.00", null, 125000L)]
    [InlineData("-300.25", null, -30025L)]
    [InlineData("(12,00)", null, -1200L)]
    [InlineData("15", null, 1500L)]
    [InlineData("1000,", ',', 100000L)]
    [InlineData("0,01", ',', 1L)]
    public void Amounts_become_minor_units_exactly(string text, char? separator, long expected)
    {
        Assert.Equal(StatementAmounts.Error.None, StatementAmounts.TryParse(text, separator, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("100,005", StatementAmounts.Error.FractionalMinorUnits)]
    [InlineData("0.001", StatementAmounts.Error.FractionalMinorUnits)]
    [InlineData("abc", StatementAmounts.Error.Invalid)]
    [InlineData("1,2,3", StatementAmounts.Error.Invalid)]
    [InlineData("", StatementAmounts.Error.Empty)]
    [InlineData("99999999999999999999", StatementAmounts.Error.TooLarge)]
    public void Bad_amounts_are_refused_not_rounded(string text, StatementAmounts.Error expected) =>
        Assert.Equal(expected, StatementAmounts.TryParse(text, null, out _));

    [Theory]
    [InlineData("ИНН 301234567 оплата", "301234567")]
    [InlineData("оплата за товар, INN:301234567.", "301234567")]
    [InlineData("STIR 301234567 to'lov", "301234567")]
    [InlineData("тўлов СТИР: 301234567", "301234567")]
    [InlineData("ИНН/ПИНФЛ 12345678901234", "12345678901234")]
    [InlineData("/INN/301234567/", "301234567")]
    [InlineData("PINN 301234567", null)]
    [InlineData("ИНН 20208000100200300401", null)]
    [InlineData("без ИНН", null)]
    public void Inn_is_found_by_markers_only(string text, string? expected) =>
        Assert.Equal(expected, TextFieldExtractor.Extract(StatementProfiles.GenericMt940, text).Inn);

    [Fact]
    public void Profiles_are_validated_and_round_trip_as_json()
    {
        foreach (var builtIn in StatementProfiles.BuiltIn)
        {
            var copy = StatementProfile.FromJson(builtIn.ToJson());
            Assert.Equal(builtIn.Name, copy.Name);
            Assert.Equal(builtIn.Format, copy.Format);
            Assert.Equal(builtIn.Csv.Columns.Count, copy.Csv.Columns.Count);
        }
        Assert.Contains("regular expression", Assert.Throws<ArgumentException>(() =>
            StatementProfile.FromJson("""{"name":"x","format":"mt940","textFields":{"inn":{"regex":["(unclosed"]}}}""")).Message);
        Assert.Contains("Unknown text field", Assert.Throws<ArgumentException>(() =>
            StatementProfile.FromJson("""{"name":"x","format":"mt940","textFields":{"tin":{"markers":["ИНН"]}}}""")).Message);
        Assert.Contains("encoding", Assert.Throws<ArgumentException>(() =>
            StatementProfile.FromJson("""{"name":"x","format":"csv","encoding":"koi8-r"}""")).Message);
        Assert.Throws<ArgumentException>(() => StatementProfile.FromJson("""{"format":"csv"}"""));
        Assert.Throws<ArgumentException>(() => StatementProfile.FromJson("not json"));
    }

    [Theory]
    [InlineData("mt940_single_utf8.sta", StatementFormat.Mt940, "Оплата по счету-фактуре № 145 от 28.10.2026 за товары")]
    [InlineData("mt940_multi_cp1251.sta", StatementFormat.Mt940, "Комиссия банка за ведение счета за октябрь 2026")]
    [InlineData("csv_semicolon_cp1251.csv", StatementFormat.Csv, "Аренда офиса за ноябрь")]
    public void Anonymizer_replaces_names_inns_and_accounts_and_keeps_amounts_dates_and_text(string name, StatementFormat format, string kept)
    {
        IStatementParser parser = format == StatementFormat.Mt940 ? new Mt940Parser() : new CsvStatementParser();
        var profile = StatementProfiles.Default(format);
        var original = parser.Parse(StatementFiles.Read(name), profile);
        var result = StatementAnonymizer.Anonymize(StatementFiles.Read(name), format, profile, parser);
        Assert.Equal(original.Encoding, result.Encoding);
        var anonymized = parser.Parse(result.Content, profile);
        var (text, _) = StatementText.Decode(result.Content, "auto");

        var before = original.Statements.SelectMany(s => s.Operations).ToList();
        var after = anonymized.Statements.SelectMany(s => s.Operations).ToList();
        Assert.Equal(before.Select(o => (o.PostingDate, o.Direction, o.Amount, o.IsReversal)), after.Select(o => (o.PostingDate, o.Direction, o.Amount, o.IsReversal)));
        Assert.Equal(original.Statements.Select(s => (s.Opening, s.Closing)), anonymized.Statements.Select(s => (s.Opening, s.Closing)));
        foreach (var inn in before.Select(o => o.CounterpartyInn).OfType<string>()) Assert.DoesNotContain(inn, text);
        foreach (var account in original.Statements.Select(s => s.AccountNumber).Concat(before.Select(o => o.CounterpartyAccount).OfType<string>()))
            Assert.DoesNotContain(account, text);
        foreach (var n in before.Select(o => o.CounterpartyName).OfType<string>()) Assert.DoesNotContain(n, text);
        // Same INN -> same pseudonym everywhere; the rest of the purpose stays.
        Assert.Equal(before.Select(o => o.CounterpartyInn is null).ToList(), after.Select(o => o.CounterpartyInn is null).ToList());
        Assert.Equal(before.Count(o => o.CounterpartyInn is not null) == 0 ? 0 : before.Select(o => o.CounterpartyInn).Distinct().Count(),
            after.Select(o => o.CounterpartyInn).Distinct().Count());
        Assert.Contains(kept, text);
        // Deterministic.
        Assert.Equal(result.Content, StatementAnonymizer.Anonymize(StatementFiles.Read(name), format, profile, parser).Content);
    }
}

/// <summary>Parsers and profiles are pure: no database, EF Core, sync services, raw layer or Infrastructure.</summary>
public class StatementArchitectureTests
{
    private static readonly string[] ForbiddenNamespaces =
    [
        "Synect.Infrastructure", "Synect.Application.Abstractions", "Synect.Application.Sync", "Synect.Application.Queue",
        "Synect.Application.Secrets", "Synect.Domain.Raw", "Microsoft.EntityFrameworkCore", "Npgsql",
    ];

    [Fact]
    public void Parsers_do_not_depend_on_the_database_or_infrastructure()
    {
        Assert.DoesNotContain(typeof(Mt940Parser).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Synect.Infrastructure", StringComparison.Ordinal));
        Type[] services = [typeof(BankStatementImportService), typeof(FileConnectionService)];
        static Type Outer(Type t) => t.DeclaringType is { } d ? Outer(d) : t;
        var types = typeof(Mt940Parser).Assembly.GetTypes()
            .Where(t => t.Namespace == "Synect.Application.Statements" && !services.Contains(Outer(t))).ToList();
        Assert.Contains(typeof(Mt940Parser), types);
        Assert.Contains(typeof(CsvStatementParser), types);
        Assert.Contains(typeof(StatementProfile), types);
        Assert.Contains(typeof(StatementAnonymizer), types);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var violations = new List<string>();
        foreach (var type in types)
        {
            var referenced = new List<Type>();
            if (type.BaseType is { } b) referenced.Add(b);
            referenced.AddRange(type.GetInterfaces());
            referenced.AddRange(type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));
            referenced.AddRange(type.GetFields(all).Select(f => f.FieldType));
            referenced.AddRange(type.GetProperties(all).Select(p => p.PropertyType));
            foreach (var method in type.GetMethods(all))
            {
                referenced.Add(method.ReturnType);
                referenced.AddRange(method.GetParameters().Select(p => p.ParameterType));
                referenced.AddRange(method.GetMethodBody()?.LocalVariables.Select(v => v.LocalType) ?? []);
            }
            foreach (var t in referenced.SelectMany(Expand))
                if (ForbiddenNamespaces.Any(ns => t.Namespace?.StartsWith(ns, StringComparison.Ordinal) == true))
                    violations.Add($"{type.FullName} -> {t.FullName}");
        }
        Assert.Empty(violations);
    }

    private static IEnumerable<Type> Expand(Type t)
    {
        yield return t;
        if (t.HasElementType) foreach (var e in Expand(t.GetElementType()!)) yield return e;
        if (t.IsGenericType) foreach (var g in t.GetGenericArguments().SelectMany(Expand)) yield return g;
    }
}
