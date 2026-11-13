using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Synect.Application.Abstractions;
using Synect.Application.Adapters;
using Synect.Application.Secrets;
using Synect.Domain.Edo;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters;
using Synect.Infrastructure.Adapters.Didox;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.Mapping;

/// <summary>
/// A stand-in for the Didox partner API inside the test process (no network, no real Didox): checks the partner and user
/// tokens, the login path and body, and serves documents page by page. Every request is recorded.
/// </summary>
public sealed class StubDidoxServer : HttpMessageHandler
{
    public const string PartnerToken = "partner-test-token";
    public const string Tin = "301234567";

    public string Password { get; set; } = "correct-password";
    public List<JsonObject> Incoming { get; } = [];
    public List<JsonObject> Outgoing { get; } = [];
    public ConcurrentQueue<(HttpMethod Method, string PathAndQuery, Dictionary<string, string> Headers, string? Body)> Requests { get; } = new();
    public int Logins;
    public HashSet<string> ValidTokens { get; } = [];

    /// <summary>Answers to give before the normal behaviour, in order (e.g. 503, 429, 401).</summary>
    public ConcurrentQueue<Func<HttpResponseMessage>> Overrides { get; } = new();

    /// <summary>Use { "data": [...], "meta": { "total": n } } instead of { "data": [...], "total": n }.</summary>
    public bool MetaTotal { get; set; }

    public static JsonObject Document(string id, int owner, int status, string number, DateTime updated, decimal total, string doctype = "002") => new()
    {
        ["doc_id"] = id, ["doctype"] = doctype, ["owner"] = owner, ["doc_status"] = status, ["name"] = number, ["doc_date"] = updated.ToString("yyyy-MM-dd"),
        ["partnerTin"] = "302222222", ["partnerCompany"] = "Partner LLC", ["total_sum"] = total, ["total_vat_sum"] = Math.Round(total * 12 / 112, 2),
        ["total_delivery_sum"] = total, ["total_delivery_sum_with_vat"] = total, ["created"] = updated.ToString("yyyy-MM-dd HH:mm:ss"),
        ["updated"] = updated.ToString("yyyy-MM-dd HH:mm:ss"), ["sellerAccount"] = "20208000900000000001",
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((request.Method, request.RequestUri!.PathAndQuery, headers, body));
        if (Overrides.TryDequeue(out var forced)) return forced();
        if (!headers.TryGetValue("Partner-Authorization", out var partner) || partner != PartnerToken) return Status(HttpStatusCode.Forbidden);

        var path = request.RequestUri.AbsolutePath;
        if (request.Method == HttpMethod.Post && path.StartsWith("/v1/auth/", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref Logins);
            var password = JsonDocument.Parse(body!).RootElement.GetProperty("password").GetString();
            if (path != $"/v1/auth/{Tin}/password/ru" || password != Password) return Status(HttpStatusCode.UnprocessableEntity, """{"error":"wrong"}""");
            var token = Guid.NewGuid().ToString();
            lock (ValidTokens) ValidTokens.Add(token);
            return Status(HttpStatusCode.OK, JsonSerializer.Serialize(new { token, related_companies = (object?)null }));
        }

        bool known;
        lock (ValidTokens) known = headers.TryGetValue("user-key", out var user) && ValidTokens.Contains(user);
        if (!known) return Status(HttpStatusCode.Unauthorized);

        if (request.Method == HttpMethod.Get && path == "/v2/documents")
        {
            var q = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var source = q["owner"] == "1" ? Outgoing : Incoming;
            var since = q["dateFromUpdated"] is { } d ? DateOnly.Parse(d) : (DateOnly?)null;
            var matching = source.Where(x => since is null || DateOnly.FromDateTime(DateTime.Parse((string)x["updated"]!)) >= since).ToList();
            var page = int.Parse(q["page"]!);
            var limit = int.Parse(q["limit"]!);
            var items = new JsonArray(matching.Skip((page - 1) * limit).Take(limit).Select(x => (JsonNode)x.DeepClone()).ToArray());
            var answer = MetaTotal
                ? new JsonObject { ["data"] = items, ["meta"] = new JsonObject { ["total"] = matching.Count } }
                : new JsonObject { ["data"] = items, ["total"] = matching.Count };
            return Status(HttpStatusCode.OK, answer.ToJsonString());
        }
        if (request.Method == HttpMethod.Get && path.StartsWith("/v1/documents/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/v1/documents/".Length..]);
            var doc = Incoming.Concat(Outgoing).FirstOrDefault(x => (string)x["doc_id"]! == id);
            return doc is null ? Status(HttpStatusCode.NotFound) : Status(HttpStatusCode.OK, new JsonObject { ["data"] = doc.DeepClone() }.ToJsonString());
        }
        return Status(HttpStatusCode.NotFound);
    }

    public static HttpResponseMessage Status(HttpStatusCode code, string? json = null) =>
        new(code) { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>The real Didox adapter against <see cref="StubDidoxServer"/>: HTTP contract, tokens, paging and errors.</summary>
public class DidoxAdapterTests
{
    private sealed class FixedCredentials(string login, string password) : ICredentialProvider
    {
        public Task<SecretValue> GetCredentialsAsync(AdapterContext context, CancellationToken ct) =>
            Task.FromResult(LoginPasswordCredentials.Create(login, password));
    }

    private static (DidoxEdoAdapter Adapter, AdapterContext Context, TestTimeProvider Time) Create(StubDidoxServer server, string? partnerToken = StubDidoxServer.PartnerToken,
        string password = "correct-password", int pageSize = 2)
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero));
        var adapter = new DidoxEdoAdapter(new DidoxHttp(new HttpClient(server)), new DidoxMapper(Options.Create(new DidoxMapperOptions())),
            new DidoxTokenCache(time, Options.Create(new DidoxTokenOptions())),
            Options.Create(new DidoxApiOptions { BaseUrl = "https://didox.test", PartnerToken = partnerToken, PageSize = pageSize }),
            NullLogger<DidoxEdoAdapter>.Instance);
        var context = new AdapterContext(Guid.NewGuid(), Guid.NewGuid(), DidoxEdoAdapter.Code, new Dictionary<string, string>(), "connection_secrets:x",
            new FixedCredentials(StubDidoxServer.Tin, password));
        return (adapter, context, time);
    }

    private static StubDidoxServer WithDocuments(int incoming, int outgoing)
    {
        var server = new StubDidoxServer();
        for (var i = 1; i <= incoming; i++) server.Incoming.Add(StubDidoxServer.Document($"in-{i}", 0, 2, $"IN-{i}", new DateTime(2026, 10, i, 9, 0, 0), 1000m * i));
        for (var i = 1; i <= outgoing; i++) server.Outgoing.Add(StubDidoxServer.Document($"out-{i}", 1, 3, $"OUT-{i}", new DateTime(2026, 10, i, 9, 0, 0), 500.5m * i));
        return server;
    }

    [Fact]
    public async Task Logs_in_with_tin_and_password_and_sends_both_tokens()
    {
        var server = WithDocuments(1, 0);
        var (adapter, context, _) = Create(server);
        var page = await adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None);

        Assert.Single(page.Items);
        var requests = server.Requests.ToList();
        var login = requests[0];
        Assert.Equal(HttpMethod.Post, login.Method);
        Assert.Equal($"/v1/auth/{StubDidoxServer.Tin}/password/ru", login.PathAndQuery);
        Assert.Equal("""{"password":"correct-password"}""", login.Body);
        Assert.Equal(StubDidoxServer.PartnerToken, login.Headers["Partner-Authorization"]);
        var list = requests[1];
        Assert.Equal("/v2/documents?owner=0&page=1&limit=2", list.PathAndQuery);
        Assert.Equal(StubDidoxServer.PartnerToken, list.Headers["Partner-Authorization"]);
        Assert.True(list.Headers.ContainsKey("user-key"));
        Assert.Null(list.Body);
    }

    [Fact]
    public async Task Maps_documents_and_walks_the_pages()
    {
        foreach (var metaTotal in new[] { false, true })
        {
            var server = WithDocuments(0, 5);
            server.MetaTotal = metaTotal;
            var (adapter, context, _) = Create(server);
            var all = new List<EdoDocumentInfo>();
            for (var p = 1; ; p++)
            {
                var page = await adapter.ListDocumentsAsync(context, EdoDocumentOwner.Outgoing, null, p, CancellationToken.None);
                all.AddRange(page.Items);
                if (!page.HasMore) break;
            }
            Assert.Equal(["out-1", "out-2", "out-3", "out-4", "out-5"], all.Select(d => d.ExternalId));
            var second = all[1];
            Assert.Equal(EdoDocumentOwner.Outgoing, second.Owner);
            Assert.Equal("OUT-2", second.Number);
            Assert.Equal("3", second.Status);
            Assert.Equal(100100, second.TotalSum);
            Assert.Equal("302222222", second.CounterpartyInn);
            Assert.Equal("20208000900000000001", second.SellerAccount);
            Assert.Equal(1, server.Logins); // the token is reused for every page
        }
    }

    [Fact]
    public async Task Updated_since_becomes_a_tashkent_date_filter()
    {
        var server = WithDocuments(5, 0);
        var (adapter, context, _) = Create(server, pageSize: 100);
        // 2026-10-02 20:00 UTC is already 2026-10-03 in Tashkent.
        var page = await adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, new DateTimeOffset(2026, 10, 2, 20, 0, 0, TimeSpan.Zero), 1, CancellationToken.None);
        Assert.Contains("dateFromUpdated=2026-10-03", server.Requests.Last().PathAndQuery);
        Assert.Equal(["in-3", "in-4", "in-5"], page.Items.Select(d => d.ExternalId));
    }

    [Fact]
    public async Task Gets_one_document_and_null_when_it_does_not_exist()
    {
        var server = WithDocuments(2, 0);
        var (adapter, context, _) = Create(server);
        var doc = await adapter.GetDocumentAsync(context, "in-2", CancellationToken.None);
        Assert.Equal("IN-2", doc!.Number);
        Assert.Equal("/v1/documents/in-2", server.Requests.Last().PathAndQuery);
        Assert.Null(await adapter.GetDocumentAsync(context, "missing/../x", CancellationToken.None));
        Assert.Equal("/v1/documents/missing%2F..%2Fx", server.Requests.Last().PathAndQuery);
    }

    [Fact]
    public async Task A_401_logs_in_again_once_and_a_second_401_is_an_authentication_failure()
    {
        var server = WithDocuments(1, 0);
        var (adapter, context, _) = Create(server);
        await adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None);
        lock (server.ValidTokens) server.ValidTokens.Clear(); // Didox dropped the token early

        var page = await adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None);
        Assert.Single(page.Items);
        Assert.Equal(2, server.Logins);

        server.Overrides.Enqueue(() => StubDidoxServer.Status(HttpStatusCode.Unauthorized));
        server.Overrides.Enqueue(() => StubDidoxServer.Status(HttpStatusCode.OK, """{"token":"t2"}"""));
        server.Overrides.Enqueue(() => StubDidoxServer.Status(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<AdapterAuthenticationException>(() =>
            adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None));
        Assert.Contains("HTTP 401", ex.Message);
    }

    [Fact]
    public async Task Wrong_password_is_an_authentication_failure_and_the_test_says_so()
    {
        var server = WithDocuments(1, 0);
        var (adapter, context, _) = Create(server, password: "wrong-password");
        var ex = await Assert.ThrowsAsync<AdapterAuthenticationException>(() =>
            adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None));
        Assert.Contains("HTTP 422", ex.Message);
        Assert.DoesNotContain("wrong-password", ex.Message);
        Assert.DoesNotContain(StubDidoxServer.Tin, ex.Message);

        var test = await adapter.TestConnectionAsync(context, CancellationToken.None);
        Assert.False(test.Success);
        Assert.Contains("not accepted", test.Error);

        var (good, goodContext, _) = Create(WithDocuments(1, 0));
        Assert.True((await good.TestConnectionAsync(goodContext, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Server_errors_rate_limits_and_timeouts_map_to_retryable_errors()
    {
        var server = WithDocuments(1, 0);
        var (adapter, context, _) = Create(server);
        await adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None);

        server.Overrides.Enqueue(() => StubDidoxServer.Status(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<AdapterTransientException>(() => adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None));

        server.Overrides.Enqueue(() =>
        {
            var r = StubDidoxServer.Status(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return r;
        });
        var limited = await Assert.ThrowsAsync<AdapterRateLimitException>(() => adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(7), limited.RetryAfter);

        server.Overrides.Enqueue(() => throw new HttpRequestException("connection refused", null, HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<AdapterTransientException>(() => adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None));

        server.Overrides.Enqueue(() => StubDidoxServer.Status(HttpStatusCode.BadRequest, """{"error":"bad"}"""));
        var other = await Assert.ThrowsAsync<AdapterException>(() => adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None));
        Assert.IsNotType<AdapterTransientException>(other);
        Assert.IsNotType<AdapterAuthenticationException>(other);

        server.Overrides.Enqueue(() => StubDidoxServer.Status(HttpStatusCode.OK, "not json"));
        await Assert.ThrowsAsync<AdapterException>(() => adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Without_a_partner_token_nothing_is_sent_and_it_is_not_blamed_on_the_client()
    {
        var server = WithDocuments(1, 0);
        var (adapter, context, _) = Create(server, partnerToken: null);
        var ex = await Assert.ThrowsAsync<AdapterException>(() => adapter.ListDocumentsAsync(context, EdoDocumentOwner.Incoming, null, 1, CancellationToken.None));
        Assert.IsNotType<AdapterAuthenticationException>(ex);
        Assert.Contains("partner token", ex.Message);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task The_sync_service_loads_didox_documents_through_the_real_adapter()
    {
        var server = WithDocuments(3, 4);
        await using var host = await TestHost.CreateSqliteAsync(services =>
        {
            services.AddSingleton(new DidoxHttp(new HttpClient(server)));
            // The registered Didox adapter takes the last DidoxHttp registration: the stub, never the network.
            services.Configure<DidoxApiOptions>(o => { o.BaseUrl = "https://didox.test"; o.PartnerToken = StubDidoxServer.PartnerToken; o.PageSize = 2; });
        });
        var tenant = await host.CreateTenantAsync("Didox adapter sync");
        var created = await host.InTenantAsync(tenant, sp => sp.GetRequiredService<ConnectionCredentialsService>()
            .CreateAsync(DidoxEdoAdapter.Code, null, StubDidoxServer.Tin, new SecretValue("correct-password"), test: false, CancellationToken.None));

        var outcome = await host.SyncEdoAsync(tenant, created.ConnectionId);
        Assert.Equal(SyncRunResult.Succeeded, outcome.Result);
        Assert.Equal(7, await host.CountAsync(tenant, db => db.EdoDocuments));
        Assert.Equal(7, await host.CountAsync(tenant, db => db.RawObjects.Where(r => r.ObjectType == DidoxMapper.DocumentObjectType)));
        Assert.Equal(1, server.Logins);

        // A second load asks only for documents updated since the last load (minus the overlap) and changes nothing.
        host.Time.Advance(TimeSpan.FromHours(2));
        var again = await host.SyncEdoAsync(tenant, created.ConnectionId);
        Assert.Equal(SyncRunResult.Succeeded, again.Result);
        Assert.Contains(server.Requests, r => r.PathAndQuery.Contains("dateFromUpdated=", StringComparison.Ordinal));
        Assert.Equal(7, await host.CountAsync(tenant, db => db.EdoDocuments));
    }
}
