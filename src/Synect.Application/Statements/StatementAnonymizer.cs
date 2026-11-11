using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Synect.Domain.Banking;

namespace Synect.Application.Statements;

public sealed record AnonymizationResult(byte[] Content, string Encoding, int Names, int Inns, int Accounts);

/// <summary>
/// Makes a statement file shareable for tests: every counterparty name and INN (as read by the profile) is replaced by a
/// stable pseudonym everywhere in the file (also inside payment texts), every account number (20 digits) is masked the
/// same way everywhere; amounts, dates and the rest of the payment text stay. Output keeps the file's encoding.
/// The result must still be reviewed by a person before it leaves the company (names of people, phone numbers or
/// addresses inside payment texts are not recognized).
/// </summary>
public static class StatementAnonymizer
{
    private static readonly Regex AccountNumber = new(@"(?<!\d)\d{20}(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex Inn = new(@"(?<!\d)(\d{9}|\d{14})(?!\d)", RegexOptions.CultureInvariant);

    public static AnonymizationResult Anonymize(byte[] content, StatementFormat format, StatementProfile profile, IStatementParser parser)
    {
        var parsed = parser.Parse(content, profile);
        var (text, encoding) = StatementText.Decode(content, profile.Encoding);

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var inns = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var op in parsed.Statements.SelectMany(s => s.Operations))
        {
            if (op.CounterpartyInn is { } inn && !inns.ContainsKey(inn))
                inns[inn] = Pseudo(inn, inns.Count + 1);
            if (op.CounterpartyName is { Length: >= 3 } name && !names.ContainsKey(name))
                names[name] = $"COUNTERPARTY {names.Count + 1:D3}";
            // Raw values as written in the file (a name column with quotes, an INN column with spaces).
            foreach (var (field, value) in op.Source)
            {
                if (field.Equals("counterpartyName", StringComparison.OrdinalIgnoreCase) && value.Trim().Length >= 3 && !names.ContainsKey(value.Trim()))
                    names[value.Trim()] = op.CounterpartyName is { } n && names.TryGetValue(n, out var p) ? p : $"COUNTERPARTY {names.Count + 1:D3}";
            }
        }

        // Longest names first, so that "OOO Romashka Plus" is not half replaced by "OOO Romashka".
        foreach (var (name, pseudonym) in names.OrderByDescending(n => n.Key.Length))
            text = text.Replace(name, pseudonym, StringComparison.Ordinal);
        // Every 9 / 14 digit number already known as an INN, wherever it appears.
        text = Inn.Replace(text, m => inns.TryGetValue(m.Value, out var p) ? p : m.Value);

        var accounts = new Dictionary<string, string>(StringComparer.Ordinal);
        text = AccountNumber.Replace(text, m =>
        {
            if (!accounts.TryGetValue(m.Value, out var masked))
            {
                // Balance account and currency (first 8 digits) stay: they say what kind of account it is.
                masked = m.Value[..8] + (accounts.Count + 1).ToString("D12", CultureInfo.InvariantCulture);
                accounts[m.Value] = masked;
            }
            return masked;
        });

        var enc = StatementText.EncodingOf(encoding);
        var bom = encoding == StatementText.Utf8 && content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;
        var bytes = enc.GetBytes(text);
        if (bom) bytes = [.. Encoding.UTF8.GetPreamble(), .. bytes];
        return new AnonymizationResult(bytes, encoding, names.Count, inns.Count, accounts.Count);
    }

    /// <summary>A pseudonym of the same length: "9" followed by a counter (stable within one file; not checked against real INNs).</summary>
    private static string Pseudo(string inn, int n) => inn.Length == 14
        ? "9" + n.ToString("D13", CultureInfo.InvariantCulture)
        : "9" + n.ToString("D8", CultureInfo.InvariantCulture);
}
