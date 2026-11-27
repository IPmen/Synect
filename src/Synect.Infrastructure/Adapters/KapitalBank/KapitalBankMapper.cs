using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Synect.Application.Adapters;
using Synect.Domain.Banking;
using Synect.Domain.Common;

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
    /// Envelope error code meaning "IP address not allowed" (documented as an IP restriction error).
    /// For data methods it is the only code treated as an access (authentication) problem; other codes
    /// are not guessed and not recognised by message text.
    /// </summary>
    public const int IpNotAllowedErrorCode = 61002;

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
    /// An operation that cannot be read (no b2_id or amount, unknown dir, bad date or amount) is left out and listed in
    /// <see cref="BankDayTransactions.Rejected"/>; the other operations of the day still load. Errors of the answer as a
    /// whole (envelope error, no result) still fail the call.
    /// </summary>
    public BankDayTransactions MapTransactions(string json, string accountNumber, DateOnly day)
    {
        using var doc = JsonDocument.Parse(json);
        var result = Result(doc.RootElement);
        var currency = CurrencyCodes.FromAccount(accountNumber);
        var byId = new Dictionary<string, BankTransactionInfo>(StringComparer.Ordinal);
        var rejected = new List<string>();

        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in content.EnumerateArray())
            {
                index++;
                try
                {
                    var tx = MapOperation(item, currency, day);
                    byId[tx.OperationId] = tx;
                }
                catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
                {
                    // The reason names the position and the bank id, never the operation's content (names, INNs, purpose).
                    var id = item.ValueKind == JsonValueKind.Object ? SourceJson.Str(item, "b2_id") : null;
                    rejected.Add($"operation #{index}{(id is null ? "" : $" (b2_id {id})")}: {ex.Message}");
                }
            }
        }

        bool? closed = SourceJson.Int(result, "fin") is { } fin ? fin == 1 : null;
        return new BankDayTransactions(accountNumber, day, byId.Values.ToList(), closed) { Rejected = rejected };
    }

    /// <summary>The four day totals of GetDoc1C; a day balance is stored only when all of them are present.</summary>
    public static readonly IReadOnlyList<string> DayTotalFields = ["saldo_in", "saldo_out", "total_debit", "total_credit"];

    /// <summary>
    /// Maps the day totals of a statement (GetDoc1C). Null when any of <see cref="DayTotalFields"/> is missing or not a
    /// number: a missing value is never stored as 0 (a false closing balance or turnover), and a day balance stored
    /// earlier is then left as it was.
    /// </summary>
    public BankBalanceInfo? MapBalance(string json, string accountNumber, DateOnly day)
    {
        using var doc = JsonDocument.Parse(json);
        var result = Result(doc.RootElement);
        if (SourceJson.Dec(result, "saldo_in") is not { } saldoIn
            || SourceJson.Dec(result, "saldo_out") is not { } saldoOut
            || SourceJson.Dec(result, "total_debit") is not { } totalDebit
            || SourceJson.Dec(result, "total_credit") is not { } totalCredit)
            return null;

        // Raw copy of the day header without the operations (they are stored as separate raw objects).
        var header = JsonNode.Parse(result.GetRawText())!.AsObject();
        header.Remove("content");
        var operDay = SourceJson.Date(result, "oper_day") ?? day;

        return new BankBalanceInfo(
            accountNumber,
            operDay,
            Minor(saldoIn, "saldo_in"),
            Minor(saldoOut, "saldo_out"),
            Minor(totalDebit, "total_debit"),
            Minor(totalCredit, "total_credit"),
            SourceJson.Int(result, "fin") == 1,
            new RawRecord(DayBalanceObjectType, $"{accountNumber}:{operDay:yyyy-MM-dd}", header.ToJsonString()));
    }

    /// <summary>
    /// Maps the APILogin response. ANY envelope error of APILogin is an authentication failure.
    /// Format (taken from the documentation description, NOT verified on real responses):
    /// result = { "sid": "...", "login": "...", "clients": [ { "id", "branch", "accounts": [ ...accounts with balances... ] } ] }.
    /// </summary>
    public KapitalLoginResult MapLogin(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new AdapterException("Kapital Bank login response is not a JSON object.");
        if (EnvelopeError(root) is { } error)
            throw new AdapterAuthenticationException($"Kapital Bank login failed: error {error.Code?.ToString() ?? "?"}: {error.Message ?? "no message"}");

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
            || SourceJson.Str(result, "sid") is not { } sid)
            throw new AdapterException("Kapital Bank login response has no result.sid.");

        var clients = new List<KapitalLoginClient>();
        if (result.TryGetProperty("clients", out var clientArray) && clientArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var client in clientArray.EnumerateArray())
            {
                var accounts = new List<string>();
                if (client.TryGetProperty("accounts", out var accountArray) && accountArray.ValueKind == JsonValueKind.Array)
                    accounts.AddRange(accountArray.EnumerateArray().Select(acc => SourceJson.Str(acc, "account")).OfType<string>());
                clients.Add(new KapitalLoginClient(SourceJson.Str(client, "id"), SourceJson.Str(client, "branch"), accounts));
            }
        }
        return new KapitalLoginResult(sid, SourceJson.Str(result, "login"), clients);
    }

    /// <summary>Validates the envelope of a data method and returns "result". Throws when the bank reported an error.</summary>
    private static JsonElement Result(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new AdapterException("Kapital Bank response is not a JSON object.");

        if (EnvelopeError(root) is { } error)
        {
            var text = $"Kapital Bank error {error.Code?.ToString() ?? "?"}: {error.Message ?? "no message"}";
            if (error.Code == IpNotAllowedErrorCode) throw new AdapterAuthenticationException(text);
            throw new AdapterException(text);
        }

        if (!root.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null)
            throw new AdapterException("Kapital Bank response has neither error nor result.");
        return result;
    }

    private static (int? Code, string? Message)? EnvelopeError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null) return null;
        return error.ValueKind == JsonValueKind.Object
            ? (SourceJson.Int(error, "code"), SourceJson.Str(error, "message"))
            : (null, error.GetRawText());
    }

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

/// <summary>An organisation available to the logged-in Kapital Bank user, with its account numbers.</summary>
public sealed record KapitalLoginClient(string? Id, string? Branch, IReadOnlyList<string> Accounts);

/// <summary>Parsed APILogin result. The sid is a secret-like session token: never log it.</summary>
public sealed record KapitalLoginResult(string Sid, string? Login, IReadOnlyList<KapitalLoginClient> Clients)
{
    public override string ToString() => $"KapitalLoginResult {{ Login = {Login}, Clients = {Clients.Count} }}";
}

/// <summary>Currency helpers for Kapital Bank accounts.</summary>
public static class CurrencyCodes
{
    /// <summary>
    /// Account currency: the bank's "val" field first; if empty, digits 6-8 of the 20-digit account number.
    /// Numeric ISO 4217 codes become alphabetic (see <see cref="IsoCurrency"/>); unknown codes are kept as is.
    /// </summary>
    public static string Resolve(string? val, string accountNumber) =>
        string.IsNullOrWhiteSpace(val) ? FromAccount(accountNumber) : Normalize(val);

    public static string FromAccount(string accountNumber) =>
        accountNumber.Length == 20 ? Normalize(accountNumber.Substring(5, 3)) : string.Empty;

    public static string Normalize(string code) => IsoCurrency.Normalize(code);
}
