using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Postgres;

namespace Synect.Tests.Ingest;

/// <summary>The HTTP contract of POST /ingest/1c/v1/batches: keys, organizations, limits, envelope and schema versions.</summary>
public class IngestApiTests(IngestFixture f) : IClassFixture<IngestFixture>
{
    private void RequirePostgres() => Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);

    private static DateTimeOffset T(int minutes) => new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(5)).AddMinutes(minutes);

    private static object[] Counterparties(params string[] refs) =>
        refs.Select(r => (object)OneCSampleClient.Counterparty(r, "Name " + r, "301000001", T(0))).ToArray();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, string? authorization, string body, string contentType = "application/json", string path = OneCSampleClient.Route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task Missing_malformed_unknown_and_revoked_keys_get_401_without_details()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest auth");
        var body = JsonSerializer.Serialize(f.Client(org).Envelope("counterparties", "auth-1", 1, Counterparties("a1")));
        var http = f.Api.CreateClient();
        var revoked = await f.CreateKeyAsync(org.TenantId, "to revoke");
        await f.Host.InTenantAsync(org.TenantId, sp => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Synect.Application.Ingest.IngestKeyService>(sp).RevokeAsync(revoked.Id.ToString(), CancellationToken.None));
        var unknown = Synect.Application.Ingest.IngestKeyFormat.Generate();

        foreach (var auth in new[] { null, "Bearer", "Basic abc", "Bearer not-a-key", "Bearer " + unknown, "Bearer " + revoked.Key, "Bearer " + org.Key.Key + "x" })
        {
            using var response = await PostAsync(http, auth, body);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
            Assert.Equal("", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0L, await f.CountAsync("ingest_batches", org.TenantId));
        // The valid key works.
        using var ok = await PostAsync(http, "Bearer " + org.Key.Key, body);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Ingest_keys_never_work_on_mcp_and_mcp_keys_never_work_on_ingest()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest vs MCP");
        var mcp = await f.CreateMcpKeyAsync(org.TenantId);
        var http = f.Api.CreateClient();
        var body = JsonSerializer.Serialize(f.Client(org).Envelope("counterparties", "cross-1", 1, Counterparties("x1")));

        using var mcpOnIngest = await PostAsync(http, "Bearer " + mcp.Key, body);
        Assert.Equal(HttpStatusCode.Unauthorized, mcpOnIngest.StatusCode);

        const string initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(initialize, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", org.Key.Key);
        using var ingestOnMcp = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, ingestOnMcp.StatusCode);
        Assert.Equal(0L, await f.CountAsync("ingest_batches", org.TenantId));
    }

    [Fact]
    public async Task Organization_comes_from_the_key_and_a_foreign_tin_is_refused_with_403()
    {
        RequirePostgres();
        var a = await f.NewOrganizationAsync("Ingest org A");
        var b = await f.NewOrganizationAsync("Ingest org B");

        var r = await f.Client(a).SendAsync(f.Client(a).Envelope("counterparties", "org-1", 1, Counterparties("same-ref")));
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(1L, await f.CountAsync("accounting_counterparties", a.TenantId));
        Assert.Equal(0L, await f.CountAsync("accounting_counterparties", b.TenantId));

        // A's key with B's TIN in the envelope: refused, nothing about B in the answer, nothing stored anywhere.
        var foreign = await f.Client(a.Key.Key, b.Tin).SendAsync(f.Client(a.Key.Key, b.Tin).Envelope("counterparties", "org-2", 1, Counterparties("evil")));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.Status);
        Assert.Equal("organization_mismatch", foreign.Get("error"));
        Assert.DoesNotContain(b.Tin, foreign.Body.GetRawText());
        Assert.DoesNotContain("Ingest org B", foreign.Body.GetRawText());
        Assert.Equal(0L, await f.CountAsync("accounting_counterparties", b.TenantId, "ref = 'evil'"));
        Assert.Equal(0L, await f.CountAsync("accounting_counterparties", a.TenantId, "ref = 'evil'"));
        Assert.Equal("Rejected", await f.ValueAsync("ingest_batches", a.TenantId, "batch_id = 'org-2'", "status"));

        // An organization without a TIN cannot confirm any package.
        var noTin = await f.NewOrganizationAsync("Ingest no TIN");
        await f.ExecAdminAsync($"UPDATE tenants SET tin = NULL WHERE id = '{noTin.TenantId}'");
        var refused = await f.Client(noTin).SendAsync(f.Client(noTin).Envelope("counterparties", "org-3", 1, Counterparties("n1")));
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
    }

    [Fact]
    public async Task Packages_per_key_are_limited_with_429_and_retry_after()
    {
        RequirePostgres();
        var api = f.CreateApi(("Ingest:RateLimit:PackagesPerMinute", "2"));
        var org = await f.NewOrganizationAsync("Ingest rate");
        var other = await f.NewOrganizationAsync("Ingest rate other");
        var http = api.CreateClient();
        var client = f.Client(org, api);
        for (var i = 1; i <= 2; i++)
        {
            using var ok = await PostAsync(http, "Bearer " + org.Key.Key, JsonSerializer.Serialize(client.Envelope("counterparties", "rate-" + i, 1, Counterparties("r" + i))));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var limited = await PostAsync(http, "Bearer " + org.Key.Key, JsonSerializer.Serialize(client.Envelope("counterparties", "rate-3", 1, Counterparties("r3"))));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Contains("rate_limited", await limited.Content.ReadAsStringAsync());
        // Another key is not affected.
        using var otherOk = await PostAsync(http, "Bearer " + other.Key.Key,
            JsonSerializer.Serialize(f.Client(other, api).Envelope("counterparties", "rate-o", 1, Counterparties("o1"))));
        Assert.Equal(HttpStatusCode.OK, otherOk.StatusCode);
    }

    [Fact]
    public async Task Failed_authentications_per_ip_are_limited_strictly()
    {
        RequirePostgres();
        var api = f.CreateApi(("Ingest:RateLimit:FailedAuthPerMinutePerIp", "3"));
        var org = await f.NewOrganizationAsync("Ingest failed auth");
        var http = api.CreateClient();
        var body = JsonSerializer.Serialize(f.Client(org, api).Envelope("counterparties", "fa-1", 1, Counterparties("f1")));
        for (var i = 0; i < 3; i++)
        {
            using var bad = await PostAsync(http, "Bearer " + Synect.Application.Ingest.IngestKeyFormat.Generate(), body);
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }
        // Blocked for the rest of the window, even with a valid key.
        using var blocked = await PostAsync(http, "Bearer " + org.Key.Key, body);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Contains("too_many_failed_authentications", await blocked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Too_many_items_or_bytes_give_413_and_gzip_is_accepted()
    {
        RequirePostgres();
        var api = f.CreateApi(("Ingest:MaxItems", "3"), ("Ingest:MaxBytes", "4000"));
        var org = await f.NewOrganizationAsync("Ingest size");
        var client = f.Client(org, api);

        var many = await client.SendAsync(client.Envelope("counterparties", "size-1", 1, Counterparties("s1", "s2", "s3", "s4")));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, many.Status);
        Assert.Equal("too_many_items", many.Get("error"));
        Assert.Contains("at most 3", many.Get("message"));

        var big = OneCSampleClient.Counterparty("big", new string('x', 3990), null, T(0));
        var large = await client.SendAsync(client.Envelope("counterparties", "size-2", 1, new object[] { big }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.Status);
        Assert.Equal("payload_too_large", large.Get("error"));

        // The same oversized body compressed is still refused: the limit counts decompressed bytes.
        var zipped = await client.SendAsync(client.Envelope("counterparties", "size-3", 1, new object[] { big }), gzip: true);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, zipped.Status);

        var small = await client.SendAsync(client.Envelope("counterparties", "size-4", 1, Counterparties("z1", "z2")), gzip: true);
        Assert.Equal(HttpStatusCode.OK, small.Status);
        Assert.Equal(2, small.Int("accepted"));

        var wrongType = await client.SendRawAsync(Encoding.UTF8.GetBytes("{}"), contentType: "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.Status);
        // The package with too many items is journaled as refused (its envelope was readable); oversized bodies are never parsed.
        Assert.Equal("Rejected", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'size-1'", "status"));
        Assert.Equal(0L, await f.CountAsync("ingest_batches", org.TenantId, "batch_id IN ('size-2', 'size-3')"));
        Assert.Equal(2L, await f.CountAsync("accounting_counterparties", org.TenantId));
    }

    [Fact]
    public async Task Invalid_gzip_and_invalid_json_are_refused_with_400()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest bad body");
        var http = f.Api.CreateClient();
        using (var request = new HttpRequestMessage(HttpMethod.Post, OneCSampleClient.Route) { Content = new ByteArrayContent([1, 2, 3, 4]) })
        {
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content.Headers.ContentEncoding.Add("gzip");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", org.Key.Key);
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var json = await f.Client(org).SendRawAsync(Encoding.UTF8.GetBytes("{\"schema_version\": 1, "));
        Assert.Equal(HttpStatusCode.BadRequest, json.Status);
        Assert.Equal("invalid_json", json.Get("error"));
    }

    [Fact]
    public async Task Schema_versions_come_from_settings_and_others_get_a_clear_error()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest versions");
        var client = f.Client(org);

        var v2 = await client.SendAsync(client.Envelope("counterparties", "ver-1", 1, Counterparties("v1"), schemaVersion: 2));
        Assert.Equal(HttpStatusCode.BadRequest, v2.Status);
        Assert.Equal("unsupported_schema_version", v2.Get("error"));
        Assert.Equal("[1]", v2.Get("supported_schema_versions"));
        Assert.Equal("Rejected", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'ver-1'", "status"));
        Assert.Equal("2", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'ver-1'", "schema_version"));

        // "1.0" is version 1; the refused (batch_id, sequence) may be sent again and is then processed.
        var envelope = client.Envelope("counterparties", "ver-1", 1, Counterparties("v1"));
        envelope["schema_version"] = "1.0";
        var ok = await client.SendAsync(envelope);
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal("Processed", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'ver-1'", "status"));
        Assert.Equal("2", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'ver-1'", "attempts"));
        Assert.Equal("1", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'v1'", "schema_version"));

        var api = f.CreateApi(("Ingest:SupportedSchemaVersions:0", "1"), ("Ingest:SupportedSchemaVersions:1", "2"));
        var accepted = await f.Client(org, api).SendAsync(client.Envelope("counterparties", "ver-2", 1, Counterparties("v2"), schemaVersion: 2));
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Equal("2", await f.ValueAsync("accounting_counterparties", org.TenantId, "ref = 'v2'", "schema_version"));
        Assert.Equal("2", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'ver-2'", "schema_version"));
    }

    [Theory]
    [InlineData("entity", "goods", "unknown_entity")]
    [InlineData("mode", "partial", "invalid_envelope")]
    [InlineData("batch_id", "", "invalid_envelope")]
    [InlineData("sequence", "0", "invalid_envelope")]
    [InlineData("exported_at", "yesterday", "invalid_envelope")]
    [InlineData("organization_tin", "12", "invalid_envelope")]
    public async Task Invalid_envelopes_are_refused_with_400(string field, string value, string code)
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest envelope " + field);
        var client = f.Client(org);
        var envelope = client.Envelope("counterparties", "env-" + field, 1, Counterparties("e1"));
        envelope[field] = field == "sequence" ? 0 : value;
        var r = await client.SendAsync(envelope);
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Equal(code, r.Get("error"));
        Assert.Equal(0L, await f.CountAsync("accounting_counterparties", org.TenantId));
    }

    [Fact]
    public async Task Full_mode_requires_full_sync_id()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest full id");
        var client = f.Client(org);
        var r = await client.SendAsync(client.Envelope("counterparties", "full-noid", 1, Counterparties("f1"), mode: "full"));
        Assert.Equal(HttpStatusCode.BadRequest, r.Status);
        Assert.Contains("full_sync_id", r.Get("message"));
    }

    [Fact]
    public async Task Gzip_body_is_decompressed()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Ingest gzip");
        var client = f.Client(org);
        var json = JsonSerializer.SerializeToUtf8Bytes(client.Envelope("counterparties", "gz-1", 1, Counterparties("g1", "g2", "g3")));
        using var buffer = new MemoryStream();
        await using (var zip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) await zip.WriteAsync(json);
        Assert.True(buffer.Length < json.Length);
        var r = await client.SendRawAsync(json, gzip: true);
        Assert.Equal(HttpStatusCode.OK, r.Status);
        Assert.Equal(3, r.Int("accepted"));
    }
}
