namespace Synect.Application.Statements;

/// <summary>Counterparty details found in free payment text by the profile's <see cref="StatementProfile.TextFields"/>.</summary>
public sealed record TextFields(
    string? Inn, string? Name, string? Account, string? Mfo, string? DocumentNumber, string? PurposeCode, string? Purpose);

/// <summary>Applies the text field rules of a profile. Pure function of the profile and the text.</summary>
public static class TextFieldExtractor
{
    public static TextFields Extract(StatementProfile profile, string? text)
    {
        string? Find(string field) =>
            profile.TextFields.TryGetValue(field, out var rule) ? rule.Find(field, profile.Separators, text) : null;

        var inn = StatementText.Digits(Find("inn"));
        if (inn is not null && inn.Length is not (9 or 14)) inn = null;
        var account = StatementText.Digits(Find("account"));
        var mfo = StatementText.Digits(Find("mfo"));
        return new TextFields(
            inn,
            StatementText.Clean(Find("name"), Limits.Name),
            account,
            mfo is { Length: <= Limits.Mfo } ? mfo : null,
            StatementText.Clean(Find("documentNumber"), Limits.DocumentNumber),
            StatementText.Clean(Find("purposeCode"), Limits.PurposeCode),
            Find("purpose"));
    }
}

/// <summary>Column limits of bank_transactions / bank_accounts: longer values from files are cut.</summary>
public static class Limits
{
    public const int Name = 512;
    public const int Account = 34;
    public const int Mfo = 16;
    public const int Inn = 16;
    public const int DocumentNumber = 64;
    public const int PurposeCode = 16;
    public const int Purpose = 4000;
    public const int State = 32;
}
