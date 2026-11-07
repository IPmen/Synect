using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Synect.Application.Adapters;
using Synect.Domain.Banking;

namespace Synect.Infrastructure.Adapters.KapitalBank;

public sealed class KapitalBankMapperOptions
{
    /// <summary>
    /// Unit of amount, saldo_in, saldo_out, total_debit, total_credit.
    /// NOT CONFIRMED by Kapital Bank documentation (sum or tiyin). Default: Minor (tiyin).
    /// </summary>
    public AmountUnit AmountUnit { get; set; } = AmountUnit.Minor;
}

/// <summary>
/// Converts Kapital Bank JSON into the common bank model.
/// Every response is an envelope { "error": null | { "code", "message" }, "id": long, "result": ... };
/// the bank reports errors with HTTP 200, so the envelope is checked before anything else.
/// Result shapes: GetDoc1C -> { content: [operations], total_debit, total_credit, saldo_in, saldo_out, fin, oper_day };
/// GetAcc1C / GetAccounts -> [ { aid, branch, account, name, val, o_date, l_date, s_in, s_out, dt, ct, canpay, state, notif_in, notif_out } ].
/// </summary>
public sealed class KapitalBankMapper(IOptions<KapitalBankMapperOptions> options)
{
    public const string AccountObjectType = "kapitalbank.account";
    public const string TransactionObjectType = "kapitalbank.transaction";
    public const string DayBalanceObjectType = "kapitalbank.day_balance";

    /// <summary>
    /// Error codes treated as authentication failures.
    /// ASSUMPTION: 61002 is documented as an IP restriction error ("IP not allowed"); it needs the same
    /// user action as bad credentials, so it is reported as an authentication problem.
    /// Login error codes are not documented yet; they are recognised by message (see IsLoginError).
    /// </summary>
    private static readonly HashSet<int> AuthErrorCodes = [61002];

    private readonly KapitalBankMapperOptions _options = options.Value;

    public IReadOnlyList<BankAccountInfo> MapAccounts(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = Result(doc.RootElement);
        if (result.ValueKind != JsonValueKind.Array)
            throw new AdapterException("Kapital Bank account list: result is not an array.");

        var accounts = new List<BankAccountInfo>();
        foreach (var item in result.EnumerateArray())
        {
            var account = SourceJson.Str(item, "account") ?? throw new FormatException("Account without number.");
            accounts.Add(new BankAccountInfo(
                AccountNumber: account,
                Mfo: SourceJson.Str(item, "branch") ?? string.Empty,
                Currency: CurrencyCodes.Resolve(SourceJson.Str(item, "val"), account),
                Name: SourceJson.Str(item, "name"),
                ExternalId: SourceJson.Str(item, "aid"),
                Raw: new RawRecord(AccountObjectType, account, item.GetRawText())));
        }
        return accounts;
    }

    /// <summary>
    /// Maps statement operations (GetDoc1C) for one account and day. Repeated operations (same b2_id)
    /// are collapsed, the last occurrence wins. Operations with err != 0 are kept and flagged.
    /// </summary>
    public BankDayTransactions MapTransactions(string json, string accountNumber, DateOnly day)
    {
        using var doc = JsonDocument.Parse(json);
        var result = Result(doc.RootElement);
        var currency = CurrencyCodes.FromAccount(accountNumber);
        var byId = new Dictionary<string, BankTransactionInfo>(StringComparer.Ordinal);

        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                var tx = MapOperation(item, currency, day);
                byId[tx.OperationId] = tx;
            }
        }

        bool? closed = SourceJson.Int(result, "fin") is { } fin ? fin == 1 : null;
        return new BankDayTransactions(accountNumber, day, byId.Values.ToList(), closed);
    }

    /// <summary>Maps the day totals of a statement (GetDoc1C); null when the payload has no totals.</summary>
    public BankBalanceInfo? MapBalance(string json, string accountNumber, DateOnly day)
    {
        using var doc = JsonDocument.Parse(json);
        var result = Result(doc.RootElement);
        if (SourceJson.Dec(result, "saldo_in") is not { } saldoIn) return null;

        // Raw copy of the day header without the operations (they are stored as separate raw objects).
        var header = JsonNode.Parse(result.GetRawText())!.AsObject();
        header.Remove("content");
        var operDay = SourceJson.Date(result, "oper_day") ?? day;

        return new BankBalanceInfo(
            accountNumber,
            operDay,
            Minor(saldoIn, "saldo_in"),
            Minor(SourceJson.Dec(result, "saldo_out") ?? 0, "saldo_out"),
            Minor(SourceJson.Dec(result, "total_debit") ?? 0, "total_debit"),
            Minor(SourceJson.Dec(result, "total_credit") ?? 0, "total_credit"),
            SourceJson.Int(result, "fin") == 1,
            new RawRecord(DayBalanceObjectType, $"{accountNumber}:{operDay:yyyy-MM-dd}", header.ToJsonString()));
    }

    /// <summary>Validates the envelope and returns "result". Throws when the bank reported an error.</summary>
    private static JsonElement Result(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new AdapterException("Kapital Bank response is not a JSON object.");

        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            var code = error.ValueKind == JsonValueKind.Object ? SourceJson.Int(error, "code") : null;
            var message = error.ValueKind == JsonValueKind.Object ? SourceJson.Str(error, "message") : error.GetRawText();
            var text = $"Kapital Bank error {code?.ToString() ?? "?"}: {message ?? "no message"}";
            if (code is { } c && AuthErrorCodes.Contains(c) || IsLoginError(message))
                throw new AdapterAuthenticationException(text);
            throw new AdapterException(text);
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null)
            throw new AdapterException("Kapital Bank response has neither error nor result.");
        return result;
    }

    /// <summary>
    /// ASSUMPTION: login error codes are not documented; a message mentioning login, password,
    /// authorization or token is treated as an authentication failure.
    /// </summary>
    private static bool IsLoginError(string? message) =>
        message is not null && new[] { "login", "password", "auth", "token", "логин", "парол", "авториз" }
            .Any(k => message.Contains(k, StringComparison.OrdinalIgnoreCase));

    private BankTransactionInfo MapOperation(JsonElement item, string currency, DateOnly day)
    {
        var id = SourceJson.Str(item, "b2_id") ?? throw new FormatException("Operation without b2_id.");
        var direction = SourceJson.Int(item, "dir") switch
        {
            1 => TransactionDirection.Incoming,
            2 => TransactionDirection.Outgoing,
            var other => throw new FormatException($"Unknown operation direction '{other}' for b2_id {id}."),
        };
        var amount = Math.Abs(Minor(SourceJson.Dec(item, "amount") ?? throw new FormatException($"Operation {id} without amount."), "amount"));

        // Incoming: the counterparty is the payer (debit side). Outgoing: the payee (credit side).
        var side = direction == TransactionDirection.Incoming ? "dt" : "ct";
        var errorCode = SourceJson.Int(item, "err") is { } err && err != 0 ? err : (int?)null;

        return new BankTransactionInfo(
            OperationId: id,
            Amount: amount,
            Currency: currency,
            Direction: direction,
            CounterpartyInn: SourceJson.Str(item, "inn_" + side),
            CounterpartyName: SourceJson.Str(item, "name_" + side),
            CounterpartyAccount: SourceJson.Str(item, "acc_" + side),
            CounterpartyMfo: SourceJson.Str(item, "mfo_" + side),
            Purpose: SourceJson.Str(item, "purpose"),
            PurposeCode: SourceJson.Str(item, "purp_code"),
            DocumentNumber: SourceJson.Str(item, "num"),
            PostingDate: SourceJson.Date(item, "ddate") ?? day,
            ValueDate: SourceJson.Date(item, "vdate"),
            SourceState: SourceJson.Str(item, "state"),
            ErrorCode: errorCode,
            ErrorMessage: errorCode is null ? null : SourceJson.Str(item, "err_msg"),
            Raw: new RawRecord(TransactionObjectType, id, item.GetRawText()));
    }

    private long Minor(decimal value, string field) => SourceJson.ToMinorUnits(value, _options.AmountUnit, field);
}

/// <summary>Currency helpers for Kapital Bank accounts.</summary>
public static class CurrencyCodes
{
    /// <summary>
    /// Account currency: the bank's "val" field first; if empty, digits 6-8 of the 20-digit account number.
    /// "000" and "UZS" become "UZS"; any other code is kept as is.
    /// </summary>
    public static string Resolve(string? val, string accountNumber) =>
        string.IsNullOrWhiteSpace(val) ? FromAccount(accountNumber) : Normalize(val);

    public static string FromAccount(string accountNumber) =>
        accountNumber.Length == 20 ? Normalize(accountNumber.Substring(5, 3)) : string.Empty;

    public static string Normalize(string code)
    {
        var trimmed = code.Trim();
        return trimmed == "000" || trimmed.Equals("UZS", StringComparison.OrdinalIgnoreCase) ? "UZS" : trimmed;
    }
}
