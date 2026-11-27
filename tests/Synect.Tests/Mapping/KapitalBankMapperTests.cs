using Microsoft.Extensions.Options;
using Synect.Application.Adapters;
using Synect.Domain.Banking;
using Synect.Infrastructure.Adapters;
using Synect.Infrastructure.Adapters.KapitalBank;

namespace Synect.Tests.Mapping;

public class KapitalBankMapperTests
{
    private const string Account = "20208000900123456001";
    private static readonly DateOnly Day = new(2026, 11, 3);

    private static KapitalBankMapper Mapper(AmountUnit unit = AmountUnit.Minor) =>
        new(Options.Create(new KapitalBankMapperOptions { AmountUnit = unit }));

    private static string Envelope(string result) => $$"""{ "error": null, "id": 1001, "result": {{result}} }""";

    private static string ErrorEnvelope(int code, string message) =>
        $$"""{ "error": { "code": {{code}}, "message": "{{message}}" }, "id": 1002, "result": null }""";

    private static string Operation(string b2Id, int dir, string amount, int err = 0, string errMsg = "") => $$"""
        {
          "b2_id": {{b2Id}}, "general_id": 9{{b2Id}}, "uniq": "U{{b2Id}}", "num": "17",
          "ddate": "2026-11-03", "vdate": "03.11.2026",
          "mfo_dt": "00444", "acc_dt": "20208000100000000111", "name_dt": "PAYER LLC", "inn_dt": "301111111",
          "mfo_ct": "00974", "acc_ct": "20208000200000000222", "name_ct": "PAYEE LLC", "inn_ct": "302222222",
          "purpose": "Payment for goods", "purp_code": "00668",
          "amount": {{amount}}, "dtype": "01", "state": 3, "dir": {{dir}}, "err": {{err}}, "err_msg": "{{errMsg}}"
        }
        """;

    private static string Statement(int fin, params string[] operations) => Envelope($$"""
        {
          "content": [{{string.Join(",", operations)}}],
          "total_debit": 25000, "total_credit": 75000, "saldo_in": 100000, "saldo_out": 150000,
          "fin": {{fin}}, "oper_day": "2026-11-03"
        }
        """);

    private static string Accounts(string val) => Envelope($$"""
        [{
          "aid": 5501, "branch": "00974", "account": "{{Account}}", "name": "FAKE CLIENT LLC", "val": "{{val}}",
          "o_date": "2024-01-15", "l_date": "2026-11-06", "s_in": 100000, "s_out": 150000, "dt": 25000, "ct": 75000,
          "canpay": 1, "state": 1, "notif_in": 0, "notif_out": 0
        }]
        """);

    [Fact]
    public void Minor_unit_amount_is_kept_as_is()
    {
        var result = Mapper().MapTransactions(Statement(1, Operation("1", 1, "1500000")), Account, Day);
        Assert.Equal(1_500_000L, Assert.Single(result.Transactions).Amount);
    }

    [Theory]
    [InlineData("15000", 1_500_000L)]
    [InlineData("15000.5", 1_500_050L)]
    [InlineData("\"15000.25\"", 1_500_025L)]
    public void Major_unit_amount_is_converted_to_tiyin(string amount, long expected)
    {
        var mapper = Mapper(AmountUnit.Major);
        var result = mapper.MapTransactions(Statement(1, Operation("1", 1, amount)), Account, Day);
        Assert.Equal(expected, Assert.Single(result.Transactions).Amount);

        var balance = mapper.MapBalance(Statement(1), Account, Day)!;
        Assert.Equal(10_000_000L, balance.OpeningBalance);
        Assert.Equal(15_000_000L, balance.ClosingBalance);
    }

    [Fact]
    public void Fractional_minor_amount_is_rejected_instead_of_rounded()
    {
        var result = Mapper().MapTransactions(Statement(1, Operation("1", 1, "10.5")), Account, Day);
        Assert.Empty(result.Transactions);
        Assert.Contains("precision", Assert.Single(result.Rejected));
    }

    [Fact]
    public void Dir_1_is_incoming_with_payer_as_counterparty_and_positive_sign()
    {
        var tx = Assert.Single(Mapper().MapTransactions(Statement(1, Operation("1", 1, "500")), Account, Day).Transactions);

        Assert.Equal(TransactionDirection.Incoming, tx.Direction);
        Assert.Equal(500, tx.Amount);
        Assert.Equal("301111111", tx.CounterpartyInn);
        Assert.Equal("PAYER LLC", tx.CounterpartyName);
        Assert.Equal("20208000100000000111", tx.CounterpartyAccount);
        Assert.Equal("00444", tx.CounterpartyMfo);
        Assert.Equal(500, new BankTransaction { Amount = tx.Amount, Direction = tx.Direction }.SignedAmount);
    }

    [Fact]
    public void Dir_2_is_outgoing_with_payee_as_counterparty_and_negative_sign()
    {
        var tx = Assert.Single(Mapper().MapTransactions(Statement(1, Operation("1", 2, "500")), Account, Day).Transactions);

        Assert.Equal(TransactionDirection.Outgoing, tx.Direction);
        Assert.Equal(500, tx.Amount);
        Assert.Equal("302222222", tx.CounterpartyInn);
        Assert.Equal("PAYEE LLC", tx.CounterpartyName);
        Assert.Equal(-500, new BankTransaction { Amount = tx.Amount, Direction = tx.Direction }.SignedAmount);
    }

    [Fact]
    public void Unknown_direction_is_rejected()
    {
        var result = Mapper().MapTransactions(Statement(1, Operation("1", 3, "500")), Account, Day);
        Assert.Empty(result.Transactions);
        Assert.Contains("direction", Assert.Single(result.Rejected));
    }

    [Fact]
    public void A_bad_operation_is_left_out_and_the_rest_of_the_day_loads()
    {
        var noId = Operation("1", 1, "100").Replace("\"b2_id\": 1,", "");
        var badDate = Operation("4", 1, "100").Replace("\"ddate\": \"2026-11-03\"", "\"ddate\": \"not a date\"");
        var json = Statement(1, Operation("2", 1, "100"), noId, Operation("3", 9, "100"), badDate, "\"not an object\"", Operation("5", 2, "200"));
        var result = Mapper().MapTransactions(json, Account, Day);

        Assert.Equal(["2", "5"], result.Transactions.Select(t => t.OperationId).Order());
        Assert.Equal(4, result.Rejected.Count);
        Assert.StartsWith("operation #2:", result.Rejected[0]);
        Assert.StartsWith("operation #3 (b2_id 3):", result.Rejected[1]);
        Assert.StartsWith("operation #4 (b2_id 4):", result.Rejected[2]);
        Assert.StartsWith("operation #5:", result.Rejected[3]);
        // The reasons never carry the operation's content.
        Assert.All(result.Rejected, r => Assert.DoesNotContain("LLC", r));
        Assert.True(result.IsDayClosed); // the bank's own flag; the sync keeps such a day open

        Assert.Empty(Mapper().MapTransactions(Statement(1, Operation("6", 1, "100")), Account, Day).Rejected);
    }

    [Fact]
    public void Operations_repeated_with_same_b2_id_are_collapsed()
    {
        var json = Statement(1, Operation("77", 1, "100"), Operation("78", 2, "200"), Operation("77", 1, "100"));
        var result = Mapper().MapTransactions(json, Account, Day);

        Assert.Equal(2, result.Transactions.Count);
        Assert.Equal(["77", "78"], result.Transactions.Select(t => t.OperationId).Order());
    }

    [Fact]
    public void Operations_with_error_are_kept_and_flagged()
    {
        var json = Statement(1, Operation("1", 1, "100"), Operation("2", 2, "300", err: 5, errMsg: "Insufficient funds"));
        var txs = Mapper().MapTransactions(json, Account, Day).Transactions.ToDictionary(t => t.OperationId);

        Assert.Equal(2, txs.Count);
        Assert.False(txs["1"].HasError);
        Assert.Null(txs["1"].ErrorCode);
        Assert.Null(txs["1"].ErrorMessage);
        Assert.True(txs["2"].HasError);
        Assert.Equal(5, txs["2"].ErrorCode);
        Assert.Equal("Insufficient funds", txs["2"].ErrorMessage);
    }

    [Fact]
    public void Day_finality_and_dates_are_mapped()
    {
        var mapper = Mapper();
        var open = mapper.MapTransactions(Statement(0, Operation("1", 1, "100")), Account, Day);
        Assert.False(open.IsDayClosed);
        Assert.False(mapper.MapBalance(Statement(0), Account, Day)!.IsDayClosed);

        var closed = mapper.MapTransactions(Statement(1, Operation("1", 1, "100")), Account, Day);
        Assert.True(closed.IsDayClosed);
        var tx = Assert.Single(closed.Transactions);
        Assert.Equal(Day, tx.PostingDate);
        Assert.Equal(Day, tx.ValueDate);
        Assert.Equal("UZS", tx.Currency);
        Assert.Equal("1", tx.Raw.ExternalId);
        Assert.Equal(KapitalBankMapper.TransactionObjectType, tx.Raw.ObjectType);
    }

    [Fact]
    public void Balance_raw_record_excludes_operations()
    {
        var balance = Mapper().MapBalance(Statement(1, Operation("1", 1, "100")), Account, Day)!;
        Assert.DoesNotContain("content", balance.Raw.Json);
        Assert.DoesNotContain("b2_id", balance.Raw.Json);
        Assert.Equal($"{Account}:2026-11-03", balance.Raw.ExternalId);
        Assert.Equal(25_000, balance.TotalDebit);
        Assert.Equal(75_000, balance.TotalCredit);
    }

    [Theory]
    [InlineData("saldo_in")]
    [InlineData("saldo_out")]
    [InlineData("total_debit")]
    [InlineData("total_credit")]
    public void A_missing_day_total_gives_no_balance_instead_of_zero(string field)
    {
        var full = Statement(1, Operation("1", 1, "100"));
        var missing = full.Replace($"\"{field}\": ", $"\"{field}_absent\": ");
        var empty = full.Replace($"\"{field}\": ", $"\"{field}\": null, \"{field}_was\": ");
        Assert.NotEqual(full, missing);

        foreach (var json in new[] { missing, empty })
        {
            Assert.Null(Mapper().MapBalance(json, Account, Day));
            // The operations of the same answer are still read.
            Assert.Single(Mapper().MapTransactions(json, Account, Day).Transactions);
        }
    }

    [Fact]
    public void Account_fields_are_mapped()
    {
        var account = Assert.Single(Mapper().MapAccounts(Accounts("000")));
        Assert.Equal(Account, account.AccountNumber);
        Assert.Equal("00974", account.Mfo);
        Assert.Equal("5501", account.ExternalId);
        Assert.Equal("FAKE CLIENT LLC", account.Name);
        Assert.Equal("UZS", account.Currency);
    }

    [Theory]
    [InlineData("000", "UZS")]
    [InlineData("UZS", "UZS")]
    [InlineData("uzs", "UZS")]
    [InlineData("860", "UZS")]
    [InlineData("840", "USD")]
    [InlineData("978", "EUR")]
    [InlineData("643", "RUB")]
    [InlineData("USD", "USD")]
    [InlineData("eur", "EUR")]
    [InlineData("999", "999")] // unknown numeric code: kept as is
    public void Account_currency_comes_from_val(string val, string expected)
    {
        Assert.Equal(expected, Assert.Single(Mapper().MapAccounts(Accounts(val))).Currency);
    }

    [Theory]
    [InlineData("20208000900123456001", "UZS")]
    [InlineData("20208840900123456001", "USD")]
    [InlineData("20208978900123456001", "EUR")]
    [InlineData("20208643900123456001", "RUB")]
    [InlineData("20208156900123456001", "156")] // unknown numeric code: kept as is
    public void Empty_val_falls_back_to_account_number_digits(string account, string expected)
    {
        Assert.Equal(expected, CurrencyCodes.Resolve("", account));
        Assert.Equal(expected, CurrencyCodes.Resolve(null, account));
    }

    [Fact]
    public void Operations_take_the_normalized_account_currency_and_keep_the_raw_value()
    {
        var json = """{ "error": null, "id": 1, "result": { "content": [ { "b2_id": 7, "dir": 1, "amount": 100, "ddate": "2026-10-01", "err": 0 } ] } }""";
        var tx = Assert.Single(Mapper().MapTransactions(json, "20208840900123456001", new DateOnly(2026, 10, 1)).Transactions);
        Assert.Equal("USD", tx.Currency);
        var account = Assert.Single(Mapper().MapAccounts(Accounts("840")));
        Assert.Contains("\"val\":\"840\"", account.Raw.Json.Replace(" ", ""));
    }

    [Fact]
    public void Envelope_error_61002_is_an_authentication_error()
    {
        var ex = Assert.Throws<AdapterAuthenticationException>(() =>
            Mapper().MapTransactions(ErrorEnvelope(61002, "IP address is not allowed"), Account, Day));
        Assert.Contains("61002", ex.Message);
        Assert.Throws<AdapterAuthenticationException>(() => Mapper().MapAccounts(ErrorEnvelope(61002, "IP address is not allowed")));
    }

    [Theory]
    [InlineData(10001, "Invalid login or password")]
    [InlineData(50010, "Internal error")]
    [InlineData(61002, "IP address is not allowed")]
    public void Any_APILogin_error_is_an_authentication_error(int code, string message)
    {
        var ex = Assert.Throws<AdapterAuthenticationException>(() => Mapper().MapLogin(ErrorEnvelope(code, message)));
        Assert.Contains(code.ToString(), ex.Message);
    }

    [Fact]
    public void Login_returns_the_session_id()
    {
        var login = Mapper().MapLogin(Envelope("""
            {
              "sid": "abc", "login": "client1",
              "clients": [
                { "id": "77", "branch": "00974", "accounts": [ { "account": "20208000900123456001", "s_out": 100 } ] },
                { "id": "78", "branch": "00444", "accounts": [] }
              ]
            }
            """));
        Assert.Equal("abc", login.Sid);
        Assert.Equal("client1", login.Login);
        Assert.Equal(2, login.Clients.Count);
        Assert.Equal(["20208000900123456001"], login.Clients[0].Accounts);
        Assert.Equal("00444", login.Clients[1].Branch);
        Assert.DoesNotContain("abc", login.ToString());

        // A bare string or an object without sid is not the documented format.
        Assert.Throws<AdapterException>(() => Mapper().MapLogin(Envelope("\"xyz\"")));
        Assert.Throws<AdapterException>(() => Mapper().MapLogin(Envelope("{}")));
    }

    [Fact]
    public void Data_method_errors_other_than_61002_are_not_recognised_by_text()
    {
        var ex = Assert.Throws<AdapterException>(() => Mapper().MapAccounts(ErrorEnvelope(40001, "Invalid login or password")));
        Assert.IsNotType<AdapterAuthenticationException>(ex);
    }

    [Fact]
    public void Other_envelope_error_is_an_adapter_error_even_with_http_200()
    {
        var ex = Assert.Throws<AdapterException>(() => Mapper().MapBalance(ErrorEnvelope(50010, "Operational day not found"), Account, Day));
        Assert.IsNotType<AdapterAuthenticationException>(ex);
        Assert.Contains("50010", ex.Message);
    }

    [Fact]
    public void Data_wrapper_is_not_supported()
    {
        Assert.Throws<AdapterException>(() => Mapper().MapAccounts("""{ "data": [] }"""));
    }
}
