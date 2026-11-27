using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Synect.Application.Adapters;
using Synect.Domain.Sync;

namespace Synect.Infrastructure.Adapters.KapitalBank;

/// <summary>
/// Adapters:KapitalBank:Api. The corporate API of Kapital Bank is not publicly documented. Known from the documentation
/// description used for <see cref="KapitalBankMapper"/>: every answer is an envelope { "error", "id", "result" } (errors
/// come with HTTP 200), the methods APILogin, GetAccounts / GetAcc1C and GetDoc1C, the session id "sid", and the error
/// code 61002 "IP address not allowed". NOT known and therefore settings, to be taken from the bank's documentation:
/// the endpoint (no default: without it nothing is sent), whether it is JSON-RPC 2.0, the parameter names and the date
/// format. The defaults below are assumptions.
/// </summary>
public sealed class KapitalBankApiOptions
{
    /// <summary>The bank's API URL (one endpoint for every method). Required; there is deliberately no default.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Add "jsonrpc": "2.0" to every request (the envelope with "id" suggests JSON-RPC). Assumption.</summary>
    public bool JsonRpc { get; set; } = true;

    public string LoginMethod { get; set; } = "APILogin";
    public string AccountsMethod { get; set; } = "GetAccounts";
    public string StatementMethod { get; set; } = "GetDoc1C";

    public string LoginParameter { get; set; } = "login";
    public string PasswordParameter { get; set; } = "password";
    public string SidParameter { get; set; } = "sid";
    public string AccountParameter { get; set; } = "account";
    public string DateParameter { get; set; } = "date";

    /// <summary>Format of the statement day in the request (.NET date format).</summary>
    public string DateFormat { get; set; } = "yyyy-MM-dd";

    /// <summary>
    /// Envelope error codes meaning "the session expired": the adapter logs in again once. Empty by default (the code is
    /// not documented); HTTP 401 always counts.
    /// </summary>
    public int[] SessionExpiredErrorCodes { get; set; } = [];

    /// <summary>Operational days back the bank returns (reported to the sync as the history depth).</summary>
    public int HistoryDepthDays { get; set; } = 365;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>Sends the HTTP requests of <see cref="KapitalBankAdapter"/>; replaced by a stub in tests (no real calls).</summary>
public sealed class KapitalBankHttp(HttpClient client)
{
    public HttpClient Client => client;

    public static KapitalBankHttp CreateDefault() =>
        new(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan });
}

/// <summary>
/// The real Kapital Bank adapter (source code "kapitalbank"). Logs in with the connection's login and password
/// (APILogin), keeps the sid per connection in <see cref="KapitalSessionManager"/> and logs in again once after an
/// authentication error; reads the accounts and, per account and operational day, the statement (GetDoc1C), mapped by
/// the existing <see cref="KapitalBankMapper"/>. Errors: HTTP 401/403, a refused login and the IP restriction (61002) are
/// authentication failures (the connection needs attention); HTTP 429 is a rate limit; 5xx, timeouts and network errors
/// are transient; other envelope errors fail the load. Messages carry the method and HTTP status or the bank's error code
/// and message, never the password, the sid or the request.
/// </summary>
public sealed class KapitalBankAdapter(
    KapitalBankHttp http,
    KapitalBankMapper mapper,
    KapitalSessionManager sessions,
    IOptions<KapitalBankApiOptions> options,
    ILogger<KapitalBankAdapter> logger) : IBankAdapter
{
    public const string Code = "kapitalbank";
    public const string Provider = "KapitalBank";

    private long _requestId;

    private KapitalBankApiOptions O => options.Value;

    public string SourceCode => Code;
    public string ProviderName => Provider;
    public AdapterKind Kind => AdapterKind.Bank;

    public BankAdapterCapabilities Capabilities => new(
        SupportsDateRange: false,
        HistoryDepthDays: O.HistoryDepthDays,
        HasFinalityFlag: true,
        HasStableOperationId: true);

    public async Task<ConnectionTestResult> TestConnectionAsync(AdapterContext context, CancellationToken ct)
    {
        try
        {
            await GetAccountsAsync(context, ct);
            return ConnectionTestResult.Ok();
        }
        catch (AdapterException ex) when (ex is not AdapterTransientException)
        {
            return ConnectionTestResult.Failed(ex.Message);
        }
    }

    public Task<BankAccountList> GetAccountsAsync(AdapterContext context, CancellationToken ct) =>
        sessions.WithSessionAsync(context, Login, async (sid, token) =>
        {
            var json = await Checked(O.AccountsMethod, CallAsync(O.AccountsMethod, new JsonObject { [O.SidParameter] = sid }, token));
            return Map(O.AccountsMethod, () => mapper.MapAccounts(json));
        }, ct);

    /// <summary>One GetDoc1C call per account and day: the operations and the day totals come from the same answer.</summary>
    public Task<BankDayStatement> GetDayAsync(AdapterContext context, string accountNumber, DateOnly day, CancellationToken ct) =>
        sessions.WithSessionAsync(context, Login, async (sid, token) =>
        {
            var json = await StatementAsync(sid, accountNumber, day, token);
            var statement = Map(O.StatementMethod, () => new BankDayStatement(
                mapper.MapTransactions(json, accountNumber, day), mapper.MapBalance(json, accountNumber, day)));
            if (statement.Balance is null)
                logger.LogWarning("Kapital Bank {Method}: no complete day totals ({Fields}) for {Day}, connection {ConnectionId}; the day balance is not stored",
                    O.StatementMethod, string.Join(", ", KapitalBankMapper.DayTotalFields), day, context.ConnectionId);
            return statement;
        }, ct);

    private Task<string> StatementAsync(string sid, string accountNumber, DateOnly day, CancellationToken ct) =>
        Checked(O.StatementMethod, CallAsync(O.StatementMethod, new JsonObject
        {
            [O.SidParameter] = sid,
            [O.AccountParameter] = accountNumber,
            [O.DateParameter] = day.ToString(O.DateFormat, CultureInfo.InvariantCulture),
        }, ct));

    /// <summary>APILogin; the mapper reads the sid and turns any envelope error into an authentication failure.</summary>
    private async Task<string> Login(string login, string password, CancellationToken ct)
    {
        var (status, body) = await CallAsync(O.LoginMethod, new JsonObject { [O.LoginParameter] = login, [O.PasswordParameter] = password }, ct);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AdapterAuthenticationException($"Kapital Bank {O.LoginMethod}: HTTP {(int)status}, the access was refused.");
        return body;
    }

    /// <summary>
    /// HTTP status checks of a data method, and the envelope codes configured as "session expired" (so that the session
    /// manager logs in again); the envelope itself is validated by the mapper.
    /// </summary>
    private async Task<string> Checked(string method, Task<(HttpStatusCode Status, string Body)> call)
    {
        var (status, body) = await call;
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new AdapterAuthenticationException($"Kapital Bank {method}: HTTP {(int)status}, the session or the access was refused.");
        if (O.SessionExpiredErrorCodes.Length > 0 && EnvelopeCode(body) is { } code && O.SessionExpiredErrorCodes.Contains(code))
            throw new AdapterAuthenticationException($"Kapital Bank {method}: error {code}, the session expired.");
        return body;
    }

    private static int? EnvelopeCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                ? SourceJson.Int(e, "code")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T Map<T>(string method, Func<T> map)
    {
        try
        {
            return map();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            throw new AdapterException($"Kapital Bank {method}: the response could not be read ({ex.GetType().Name}).");
        }
    }

    /// <summary>POST of one method; returns the status (2xx, 401 or 403) and the body. Other statuses throw.</summary>
    private async Task<(HttpStatusCode Status, string Body)> CallAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(O.Endpoint))
            throw new AdapterException("Kapital Bank API endpoint is not configured (Adapters:KapitalBank:Api:Endpoint); no request was sent.");
        var request = new JsonObject();
        if (O.JsonRpc) request["jsonrpc"] = "2.0";
        request["id"] = Interlocked.Increment(ref _requestId);
        request["method"] = method;
        request["params"] = parameters;

        using var message = new HttpRequestMessage(HttpMethod.Post, O.Endpoint)
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(O.Timeout);
        HttpResponseMessage response;
        try
        {
            response = await http.Client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AdapterTransientException($"Kapital Bank {method}: no answer within {O.Timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            throw new AdapterTransientException($"Kapital Bank {method}: network error ({ex.HttpRequestError}).");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new AdapterRateLimitException($"Kapital Bank {method}: HTTP 429, too many requests.", response.Headers.RetryAfter?.Delta);
            if (status >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                throw new AdapterTransientException($"Kapital Bank {method}: HTTP {status}.");
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && !response.IsSuccessStatusCode)
                throw new AdapterException($"Kapital Bank {method}: HTTP {status}.");
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogDebug("Kapital Bank {Method}: HTTP {Status}, {Bytes} bytes", method, status, body.Length);
            return (response.StatusCode, body);
        }
    }
}
