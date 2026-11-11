using Synect.Domain.Banking;

namespace Synect.Application.Statements;

/// <summary>
/// Built-in profiles. They read the standard structure only and take counterparty details from the payment text by
/// common markers; bank-specific dialects belong in profile JSON files (docs/import-profiles.md), never in the parsers.
/// </summary>
public static class StatementProfiles
{
    public const string GenericMt940Name = "generic_mt940";
    public const string GenericCsvName = "generic_csv";

    /// <summary>
    /// ASSUMED structure of Kapital Bank MT940 (GetMT940): ":86:" as "/CODE/value" pairs. The real format is NOT verified:
    /// no example from the bank documentation was available; replace or fix this profile when a real (anonymized) file is seen.
    /// </summary>
    public const string KapitalBankMt940AssumedName = "kapitalbank_mt940_assumed";

    /// <summary>Common markers of the counterparty details in Russian, Uzbek Latin and Uzbek Cyrillic payment texts.</summary>
    private static Dictionary<string, TextFieldRule> CommonTextFields() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["inn"] = new()
        {
            Markers = ["ИНН/ПИНФЛ", "ИНН", "INN", "STIR", "СТИР", "ПИНФЛ", "PINFL", "JSHSHIR", "ЖШШИР"],
            Pattern = @"\d{14}|\d{9}",
        },
        ["account"] = new()
        {
            Markers = ["Р/С", "Р/СЧ", "СЧЕТ", "СЧЁТ", "СЧ", "ACCOUNT", "ACC", "H/R", "Х/Р", "Ҳ/Р", "HISOB RAQAMI", "ҲИСОБ РАҚАМИ"],
            Pattern = @"\d{20}",
        },
        ["mfo"] = new() { Markers = ["МФО", "MFO"], Pattern = @"\d{5}" },
        ["documentNumber"] = new()
        {
            // Uzbek puts the number before the word: "77-sonli hisob-faktura", "12-сонли шартнома".
            Regex = [@"(?<![\p{L}\d\-])(?<v>\p{L}{0,6}-?\d[\p{L}\d/]{0,40})\s*-\s*(?:sonli|сонли)(?![\p{L}])"],
            Markers = ["№", "No", "N", "DOC", "ДОК", "HUJJAT", "ҲУЖЖАТ"],
            Pattern = @"\p{L}{0,6}-?\d[\p{L}\d\-/]{0,40}",
        },
        ["name"] = new() { Markers = ["NAME", "НАИМ", "НАИМЕНОВАНИЕ", "NOMI", "НОМИ"] },
    };

    public static StatementProfile GenericMt940 => new()
    {
        Name = GenericMt940Name,
        Format = StatementFormat.Mt940,
        Description = "Standard MT940 structure; INN, account, MFO, document number and name by common markers in :86:.",
        TextFields = CommonTextFields(),
    };

    public static StatementProfile KapitalBankMt940Assumed => new()
    {
        Name = KapitalBankMt940AssumedName,
        Format = StatementFormat.Mt940,
        Description = "ASSUMED, NOT VERIFIED: Kapital Bank GetMT940 with ':86:/INN/.../NAME/.../ACC/.../MFO/.../DOC/.../PURP/...'.",
        Separators = "/",
        Mt940 = new Mt940Settings { LineJoin = "" },
        TextFields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["inn"] = new() { Regex = [@"/INN/(?<v>\d{9}|\d{14})(?=/|$)"] },
            ["name"] = new() { Regex = [@"/NAME/(?<v>[^/]+)"] },
            ["account"] = new() { Regex = [@"/ACC/(?<v>\d{20})"] },
            ["mfo"] = new() { Regex = [@"/MFO/(?<v>\d{5})"] },
            ["documentNumber"] = new() { Regex = [@"/DOC/(?<v>[^/]+)"] },
            ["purposeCode"] = new() { Regex = [@"/PURP/(?<v>\d{5})(?!\d)"] },
            ["purpose"] = new() { Regex = [@"/PURP/(?:\d{5}\s*)?(?<v>.+)$"] },
        },
    };

    public static StatementProfile GenericCsv => new()
    {
        Name = GenericCsvName,
        Format = StatementFormat.Csv,
        Description = "Header row with common Russian, Uzbek or English column names; debit/credit columns or a signed amount.",
        TextFields = CommonTextFields(),
        Csv = new CsvSettings
        {
            Columns = new(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = ["account", "счет", "счёт", "номер счета", "hisob raqami", "hisob"],
                ["postingDate"] = ["date", "posting date", "дата", "дата проводки", "дата операции", "sana", "o'tkazish sanasi"],
                ["valueDate"] = ["value date", "дата валютирования"],
                ["debit"] = ["debit", "дебет", "расход", "списание", "chiqim"],
                ["credit"] = ["credit", "кредит", "приход", "поступление", "kirim"],
                ["amount"] = ["amount", "сумма", "summa"],
                ["direction"] = ["direction", "d/c", "дт/кт", "д/к"],
                ["currency"] = ["currency", "валюта", "valyuta"],
                ["documentNumber"] = ["document number", "doc no", "номер документа", "№ документа", "номер док", "hujjat raqami"],
                ["counterpartyName"] = ["counterparty", "counterparty name", "контрагент", "наименование контрагента", "корреспондент", "kontragent"],
                ["counterpartyInn"] = ["counterparty inn", "inn", "инн", "инн контрагента", "stir", "kontragent stir"],
                ["counterpartyAccount"] = ["counterparty account", "счет контрагента", "счёт контрагента", "корр. счет", "kontragent hisobi"],
                ["counterpartyMfo"] = ["counterparty mfo", "mfo", "мфо", "мфо контрагента"],
                ["purpose"] = ["purpose", "назначение", "назначение платежа", "to'lov maqsadi", "tolov maqsadi"],
                ["purposeCode"] = ["purpose code", "код назначения"],
                ["operationId"] = ["operation id", "id операции"],
                ["openingBalance"] = ["opening balance", "входящий остаток", "остаток на начало"],
                ["closingBalance"] = ["closing balance", "исходящий остаток", "остаток на конец"],
            },
            SkipRowsMatching = [@"^\W*(итого|всего|jami|total|обороты)\b"],
        },
    };

    public static IReadOnlyList<StatementProfile> BuiltIn => [GenericMt940, KapitalBankMt940Assumed, GenericCsv];

    /// <summary>A fresh copy of a built-in profile, or null.</summary>
    public static StatementProfile? Find(string name) =>
        BuiltIn.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static StatementProfile Default(StatementFormat format) => format == StatementFormat.Csv ? GenericCsv : GenericMt940;
}
