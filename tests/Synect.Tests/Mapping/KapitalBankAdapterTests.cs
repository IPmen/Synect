using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Synect.Application.Adapters;
using Synect.Application.Secrets;
using Synect.Domain.Banking;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters;
using Synect.Infrastructure.Adapters.KapitalBank;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.Mapping;

/// <summary>
/// A stand-in for the Kapital Bank API inside the test process (no network, no real bank): one POST endpoint, methods in
/// the request body, answers in the { error, id, result } envelope (errors with HTTP 200). Every request is recorded.
/// </summary>
public sealed class StubKapitalBank : HttpMessageHandler
{
    public const string Endpoint = "https://kapital.test/api";
    public const string Login = "client-login";
    public const string Account = "20208000900123456001";

    public string Password { get; set; } = "correct-password";
    public int Logins;
    public HashSet<string> Sessions { get; } = [];
    public ConcurrentQueue<JsonObject> Requests { get; } = new();
    public ConcurrentQueue<Func<HttpResponseMessage>> Overrides { get; } = new();

    /// <summary>Operations per operational day (yyyy-MM-dd).</summary>
    public Dictionary<string, List<JsonObject>> Days { get; } = new(StringComparer.Ordinal);

    public static JsonObject Operation(long id, int dir, long amount, string inn = "302222222") => new()
    {
        ["b2_id"] = id, ["num"] = "N" + id, ["ddate"] = null, ["dir"] = dir, ["amount"] = amount, ["state"] = 3, ["err"] = 0,
        [dir == 1 ? "inn_dt" : "inn_ct"] = inn, [dir == 1 ? "name_dt" : "name_ct"] = "PARTNER LLC",
        [dir == 1 ? "acc_dt" : "acc_ct"] = "20208000100000000111", [dir == 1 ? "mfo_dt" : "mfo_ct"] = "00444", ["purpose"] = "Оплата " + id,
    };

    private static string Envelope(JsonNode? id, JsonNode? result) =>
        new JsonObject { ["error"] = null, ["id"] = id?.DeepClone(), ["result"] = result }.ToJsonString();

    public static string Error(int code, string message) =>
        new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message }, ["id"] = 1, ["result"] = null }.ToJsonString();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        Requests.Enqueue(body);
        if (Overrides.TryDequeue(out var forced)) return forced();
        if (request.Method != HttpMethod.Post || request.RequestUri!.ToString() != Endpoint) return Json(HttpStatusCode.NotFound, "{}");

        var method = (string)body["method"]!;
        var p = body["params"]!.AsObject();
        switch (method)
        {
            case "APILogin":
                Interlocked.Increment(ref Logins);
                if ((string?)p["login"] != Login || (string?)p["password"] != Password) return Json(HttpStatusCode.OK, Error(1001, "Неверный логин или пароль"));
                var sid = "sid-" + Guid.NewGuid().ToString("N");
                lock (Sessions) Sessions.Add(sid);
                return Json(HttpStatusCode.OK, Envelope(body["id"], new JsonObject
                {
                    ["sid"] = sid, ["login"] = Login,
                    ["clients"] = new JsonArray(new JsonObject { ["id"] = "c1", ["branch"] = "00974", ["accounts"] = new JsonArray(new JsonObject { ["account"] = Account }) }),
                }));
            case "GetAccounts":
            case "GetDoc1C":
                bool known;
                lock (Sessions) known = Sessions.Contains((string?)p["sid"] ?? "");
                if (!known) return Json(HttpStatusCode.Unauthorized, "{}");
                if (method == "GetAccounts")
                    return Json(HttpStatusCode.OK, Envelope(body["id"], new JsonArray(new JsonObject
                    {
                        ["aid"] = 5501, ["branch"] = "00974", ["account"] = Account, ["name"] = "CLIENT LLC", ["val"] = "000",
                    })));
                var day = (string)p["date"]!;
                var ops = Days.GetValueOrDefault(day) ?? [];
                var debit = ops.Where(o => (int)o["dir"]! == 2).Sum(o => (long)o["amount"]!);
                var credit = ops.Where(o => (int)o["dir"]! == 1).Sum(o => (long)o["amount"]!);
                var content = new JsonArray(ops.Select(o =>
                {
                    var copy = o.DeepClone().AsObject();
                    copy["ddate"] = day;
                    return (JsonNode)copy;
                }).ToArray());
                return Json(HttpStatusCode.OK, Envelope(body["id"], new JsonObject
                {
                    ["content"] = content, ["total_debit"] = debit, ["total_credit"] = credit, ["saldo_in"] = 1_000_000,
                    ["saldo_out"] = 1_000_000 + credit - debit, ["fin"] = 1, ["oper_day"] = day,
                }));
            default:
                return Json(HttpStatusCode.OK, Error(-32601, "Method not found"));
        }
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>The real Kapital Bank adapter against <see cref="StubKapitalBank"/>: requests, sessions, mapping and errors.</summary>
public class KapitalBankAdapterTests
{
    private static readonly DateOnly Day = new(2026, 11, 3);

    private sealed class FixedCredentials(string login, string password) : ICredentialProvider
    {
        public Task<SecretValue> GetCredentialsAsync(AdapterContext context, CancellationToken ct) => Task.FromResult(LoginPasswordCredentials.Create(login, password));
    }

    private static (KapitalBankAdapter Adapter, AdapterContext Context) Create(StubKapitalBank bank, string? endpoint = StubKapitalBank.Endpoint,
        string password = "correct-password", int[]? sessionExpired = null)
    {
        var mapper = new KapitalBankMapper(Options.Create(new KapitalBankMapperOptions()));
        var adapter = new KapitalBankAdapter(new KapitalBankHttp(new HttpClient(bank)), mapper, new KapitalSessionManager(mapper),
            Options.Create(new KapitalBankApiOptions { Endpoint = endpoint, SessionExpiredErrorCodes = sessionExpired ?? [] }), NullLogger<KapitalBankAdapter>.Instance);
        var context = new AdapterContext(Guid.NewGuid(), Guid.NewGuid(), KapitalBankAdapter.Code, new Dictionary<string, string>(), "connection_secrets:x",
            new FixedCredentials(StubKapitalBank.Login, password));
        return (adapter, context);
    }

    private static StubKapitalBank WithOperations()
    {
        var bank = new StubKapitalBank();
        bank.Days["2026-11-03"] = [StubKapitalBank.Operation(101, 1, 500_000), StubKapitalBank.Operation(102, 2, 200_000, "303333333")];
        return bank;
    }

    [Fact]
    public async Task Logs_in_and_reads_the_accounts_with_the_session()
    {
        var bank = WithOperations();
        var (adapter, context) = Create(bank);
        var accounts = await adapter.GetAccountsAsync(context, CancellationToken.None);

        var account = Assert.Single(accounts);
        Assert.Equal(StubKapitalBank.Account, account.AccountNumber);
        Assert.Equal("UZS", account.Currency);
        Assert.Equal("00974", account.Mfo);
        var requests = bank.Requests.ToList();
        Assert.Equal("2.0", (string?)requests[0]["jsonrpc"]);
        Assert.Equal("APILogin", (string?)requests[0]["method"]);
        Assert.Equal(StubKapitalBank.Login, (string?)requests[0]["params"]!["login"]);
        Assert.Equal("GetAccounts", (string?)requests[1]["method"]);
        Assert.StartsWith("sid-", (string?)requests[1]["params"]!["sid"]);
        Assert.NotEqual((long)requests[0]["id"]!, (long)requests[1]["id"]!);

        await adapter.GetAccountsAsync(context, CancellationToken.None);
        Assert.Equal(1, bank.Logins); // the sid is reused
    }

    [Fact]
    public async Task Reads_the_statement_of_a_day_with_operations_and_balance()
    {
        var bank = WithOperations();
        var (adapter, context) = Create(bank);
        var (day, balance) = await adapter.GetDayAsync(context, StubKapitalBank.Account, Day, CancellationToken.None);
        Assert.True(day.IsDayClosed);
        Assert.Equal(["101", "102"], day.Transactions.Select(t => t.OperationId).Order());
        var incoming = day.Transactions.Single(t => t.OperationId == "101");
        Assert.Equal(TransactionDirection.Incoming, incoming.Direction);
        Assert.Equal(500_000, incoming.Amount);
        Assert.Equal("302222222", incoming.CounterpartyInn);
        var outgoing = day.Transactions.Single(t => t.OperationId == "102");
        Assert.Equal("303333333", outgoing.CounterpartyInn);

        var statement = bank.Requests.Last();
        Assert.Equal("GetDoc1C", (string?)statement["method"]);
        Assert.Equal(StubKapitalBank.Account, (string?)statement["params"]!["account"]);
        Assert.Equal("2026-11-03", (string?)statement["params"]!["date"]);
        // Operations and totals come from one GetDoc1C request.
        Assert.Single(bank.Requests, r => (string?)r["method"] == "GetDoc1C");

        Assert.Equal(1_000_000, balance!.OpeningBalance);
        Assert.Equal(1_300_000, balance.ClosingBalance);
        Assert.Equal(200_000, balance.TotalDebit);
        Assert.Equal(500_000, balance.TotalCredit);
    }

    [Fact]
    public async Task An_expired_session_logs_in_again_once()
    {
        var bank = WithOperations();
        var (adapter, context) = Create(bank);
        await adapter.GetAccountsAsync(context, CancellationToken.None);
        lock (bank.Sessions) bank.Sessions.Clear();
        Assert.Single(await adapter.GetAccountsAsync(context, CancellationToken.None));
        Assert.Equal(2, bank.Logins);

        // A configured envelope code counts as an expired session too.
        var (configured, configuredContext) = Create(bank, sessionExpired: [70001]);
        await configured.GetAccountsAsync(configuredContext, CancellationToken.None);
        bank.Overrides.Enqueue(() => StubKapitalBank.Json(HttpStatusCode.OK, StubKapitalBank.Error(70001, "Session expired")));
        Assert.Single(await configured.GetAccountsAsync(configuredContext, CancellationToken.None));

        // Twice in a row is an authentication failure.
        bank.Overrides.Enqueue(() => StubKapitalBank.Json(HttpStatusCode.Unauthorized, "{}"));
        bank.Overrides.Enqueue(() => StubKapitalBank.Json(HttpStatusCode.OK, """{"error":null,"id":1,"result":{"sid":"sid-new"}}"""));
        bank.Overrides.Enqueue(() => StubKapitalBank.Json(HttpStatusCode.Unauthorized, "{}"));
        await Assert.ThrowsAsync<AdapterAuthenticationException>(() => adapter.GetAccountsAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_password_and_ip_restriction_are_authentication_failures()
    {
        var bank = WithOperations();
        var (adapter, context) = Create(bank, password: "wrong-password");
        var ex = await Assert.ThrowsAsync<AdapterAuthenticationException>(() => adapter.GetAccountsAsync(context, CancellationToken.None));
        Assert.Contains("1001", ex.Message);
        Assert.DoesNotContain("wrong-password", ex.Message);
        var test = await adapter.TestConnectionAsync(context, CancellationToken.None);
        Assert.False(test.Success);

        var (good, goodContext) = Create(bank);
        Assert.True((await good.TestConnectionAsync(goodContext, CancellationToken.None)).Success);
        bank.Overrides.Enqueue(() => StubKapitalBank.Json(HttpStatusCode.OK, StubKapitalBank.Error(KapitalBankMapper.IpNotAllowedErrorCode, "IP not allowed")));
        bank.Overrides.Enqueue(() => StubKapitalBank.Json(HttpStatusCode.OK, """{"error":null,"id":1,"result":{"sid":"sid-other"}}"""));
        bank.Overrides.Enqueue(() => StubKapitalBank.Json(HttpStatusCode.OK, StubKapitalBank.Error(KapitalBankMapper.IpNotAllowedErrorCode, "IP not allowed")));
        var ip = await Assert.ThrowsAsync<AdapterAuthenticationException>(() => good.GetAccountsAsync(goodContext, CancellationToken.None));
        Assert.Contains("61002", ip.Message);
    }

    [Fact]
    public async Task Server_errors_rate_limits_and_network_errors_are_retryable_and_other_errors_fail()
    {
        var bank = WithOperations();
        var (adapter, context) = Create(bank);
        await adapter.GetAccountsAsync(context, CancellationToken.None);

        bank.Overrides.Enqueue(() => StubKapitalBank.Json(HttpStatusCode.BadGateway, "{}"));
        await Assert.ThrowsAsync<AdapterTransientException>(() => adapter.GetAccountsAsync(context, CancellationToken.None));
        bank.Overrides.Enqueue(() =>
        {
            var r = StubKapitalBank.Json(HttpStatusCode.TooManyRequests, "{}");
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return r;
        });
        Assert.Equal(TimeSpan.FromSeconds(5), (await Assert.ThrowsAsync<AdapterRateLimitException>(() => adapter.GetAccountsAsync(context, CancellationToken.None))).RetryAfter);
        bank.Overrides.Enqueue(() => throw new HttpRequestException("refused"));
        await Assert.ThrowsAsync<AdapterTransientException>(() => adapter.GetAccountsAsync(context, CancellationToken.None));

        bank.Overrides.Enqueue(() => StubKapitalBank.Json(HttpStatusCode.OK, StubKapitalBank.Error(5000, "Счёт не найден")));
        var other = await Assert.ThrowsAsync<AdapterException>(() => adapter.GetDayAsync(context, StubKapitalBank.Account, Day, CancellationToken.None));
        Assert.IsNotType<AdapterAuthenticationException>(other);
        Assert.IsNotType<AdapterTransientException>(other);
        bank.Overrides.Enqueue(() => StubKapitalBank.Json(HttpStatusCode.OK, "not json"));
        Assert.IsNotType<AdapterTransientException>(await Assert.ThrowsAnyAsync<AdapterException>(() => adapter.GetAccountsAsync(context, CancellationToken.None)));
    }

    [Fact]
    public async Task Without_an_endpoint_nothing_is_sent_and_it_is_not_blamed_on_the_client()
    {
        var bank = WithOperations();
        var (adapter, context) = Create(bank, endpoint: null);
        var ex = await Assert.ThrowsAsync<AdapterException>(() => adapter.GetAccountsAsync(context, CancellationToken.None));
        Assert.IsNotType<AdapterAuthenticationException>(ex);
        Assert.Contains("endpoint", ex.Message);
        Assert.Empty(bank.Requests);
    }

    [Fact]
    public async Task The_sync_service_loads_kapital_bank_data_through_the_real_adapter()
    {
        var bank = WithOperations();
        bank.Days["2026-11-04"] = [StubKapitalBank.Operation(103, 1, 100_000)];
        await using var host = await TestHost.CreateSqliteAsync(services =>
        {
            // The registered Kapital Bank adapter takes the last KapitalBankHttp registration: the stub, never the network.
            services.AddSingleton(new KapitalBankHttp(new HttpClient(bank)));
            services.Configure<KapitalBankApiOptions>(o => o.Endpoint = StubKapitalBank.Endpoint);
        });
        var tenant = await host.CreateTenantAsync("Kapital Bank adapter sync");
        var created = await host.InTenantAsync(tenant, sp => sp.GetRequiredService<ConnectionCredentialsService>()
            .CreateAsync(KapitalBankAdapter.Code, null, StubKapitalBank.Login, new SecretValue("correct-password"), test: true, CancellationToken.None));
        Assert.Equal(Synect.Application.Queue.ReactivationStatus.Reactivated, created.Test!.Status);

        var outcome = await host.SyncBankAsync(tenant, created.ConnectionId, new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 4));
        Assert.Equal(SyncRunResult.Succeeded, outcome.Result);
        // One account, two days: one GetDoc1C request per day, not one for operations and another for totals.
        Assert.Equal(2, bank.Requests.Count(r => (string?)r["method"] == "GetDoc1C"));
        Assert.Equal(3, await host.CountAsync(tenant, db => db.BankTransactions));
        Assert.Equal(1, await host.CountAsync(tenant, db => db.BankAccounts));
        var balances = await host.QueryAsync(tenant, db => db.BankDailyBalances.AsNoTracking().OrderBy(b => b.Date).ToListAsync());
        Assert.Equal([1_300_000L, 1_100_000L], balances.Select(b => b.ClosingBalance));
        Assert.All(balances, b => Assert.True(b.IsDayClosed));
        Assert.Equal("KapitalBank", created.Name);

        // A second load of the same days changes nothing.
        var again = await host.SyncBankAsync(tenant, created.ConnectionId, new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 4));
        Assert.Equal(SyncRunResult.Succeeded, again.Result);
        Assert.Equal(3, await host.CountAsync(tenant, db => db.BankTransactions));
    }
}
