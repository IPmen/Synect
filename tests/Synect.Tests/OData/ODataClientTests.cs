using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Synect.Application.Adapters;
using Synect.Application.OData;
using Synect.Application.Secrets;
using Synect.Infrastructure.Adapters.OData;
using Synect.Tests.Infrastructure;

namespace Synect.Tests.OData;

/// <summary>The OData client, URL building, base_url checks (SSRF), the profile and the record mapping. No database, no network.</summary>
public sealed class ODataClientTests
{
    private static (ODataHttpClient Client, FakeODataServer Server, CapturingLoggerProvider Logs) Create(Action<ODataOptions>? configure = null)
    {
        var server = new FakeODataServer();
        var options = new ODataOptions { RequestsPerSecond = 0, RetryBaseDelay = TimeSpan.FromMilliseconds(1), MaxRetryDelay = TimeSpan.FromMilliseconds(20) };
        configure?.Invoke(options);
        var logs = new CapturingLoggerProvider();
        var client = new ODataHttpClient(server, new Uri(server.BaseUrl + "/odata/standard.odata/"), server.Login, new SecretValue(server.Password), options, TimeProvider.System, logs.CreateLogger("odata"));
        return (client, server, logs);
    }

    // ---- URL ----

    [Fact]
    public void Url_encodes_cyrillic_names_filters_and_literals()
    {
        var url = ODataUrl.Build(new ODataQuery("Catalog_Контрагенты")
        {
            Filter = $"Ref_Key eq {ODataLiteral.Guid("6F9619FF-8B86-D011-B42D-00C04FC964FF")} and Date ge {ODataLiteral.DateTime(new DateOnly(2026, 10, 10))} and Description eq {ODataLiteral.String("O'Brien & Co")}",
            Select = ["Ref_Key", "DataVersion"], OrderBy = "Ref_Key", Top = 500, Skip = 1000, InlineCount = true,
        });
        Assert.StartsWith("Catalog_%D0%9A%D0%BE%D0%BD%D1%82%D1%80%D0%B0%D0%B3%D0%B5%D0%BD%D1%82%D1%8B?$format=json&$filter=", url);
        var filter = Uri.UnescapeDataString(url.Split("$filter=")[1].Split('&')[0]);
        Assert.Equal("Ref_Key eq guid'6f9619ff-8b86-d011-b42d-00c04fc964ff' and Date ge datetime'2026-10-10T00:00:00' and Description eq 'O''Brien & Co'", filter);
        Assert.DoesNotContain(" ", url);
        Assert.DoesNotContain("&Co", url); // '&' inside a value is encoded
        Assert.Contains("$select=Ref_Key%2CDataVersion", url);
        Assert.EndsWith("$top=500&$skip=1000&$inlinecount=allpages", url);
        Assert.Equal("Document_%D0%A0%D0%B5%D0%B0%D0%BB%D0%B8%D0%B7%D0%B0%D1%86%D0%B8%D1%8F/$count?$filter=Posted%20eq%20true",
            ODataUrl.Count("Document_Реализация", "Posted eq true"));
    }

    [Theory]
    [InlineData("Catalog_X?$top=1")]
    [InlineData("Catalog_X/../$metadata")]
    [InlineData("Catalog X")]
    [InlineData("")]
    public void Url_refuses_names_that_could_inject(string resource) =>
        Assert.Throws<ArgumentException>(() => ODataUrl.Build(new ODataQuery(resource)));

    [Fact]
    public void Url_refuses_bad_select_orderby_and_guid()
    {
        Assert.Throws<ArgumentException>(() => ODataUrl.Build(new ODataQuery("Catalog_X") { Select = ["Ref_Key,Password"] }));
        Assert.Throws<ArgumentException>(() => ODataUrl.Build(new ODataQuery("Catalog_X") { OrderBy = "Ref_Key; drop" }));
        Assert.Throws<ArgumentException>(() => ODataLiteral.Guid("1' or 1 eq 1"));
    }

    // ---- base_url (SSRF) ----

    [Theory]
    [InlineData("http://clobus.uz/a/sbm/1", "HTTPS")]
    [InlineData("https://evil.example/a/sbm/1", "not allowed")]
    [InlineData("https://clobus.uz.evil.example/a/1", "not allowed")]
    [InlineData("https://notclobus.uz/a/1", "not allowed")]
    [InlineData("https://user:secret@clobus.uz/a/sbm/1", "user name or password")]
    [InlineData("https://clobus.uz/a/sbm/1?x=1", "query")]
    [InlineData("https://185.1.2.3/a/sbm/1", "not an IP address")]
    [InlineData("https://[::1]/a/1", "not an IP address")]
    [InlineData("https://clobus.uz:8443/a/sbm/1", "default HTTPS port")]
    [InlineData("http://localhost:5000/odata", "HTTPS")]
    [InlineData("https://localhost/odata", "not allowed")]
    [InlineData("ftp://clobus.uz/a/1", "HTTPS")]
    [InlineData("not a url", "absolute")]
    public void Base_url_is_refused(string url, string reason)
    {
        var ex = Assert.Throws<ODataEndpointException>(() => ODataEndpoint.Normalize(url, new ODataOptions()));
        Assert.Contains(reason, ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public void Base_url_is_normalized_and_labelled()
    {
        var o = new ODataOptions();
        Assert.Equal("https://clobus.uz/a/sbm/12345/odata/standard.odata/", ODataEndpoint.Normalize("https://clobus.uz/a/sbm/12345", o).ToString());
        Assert.Equal("https://clobus.uz/a/sbm/12345/odata/standard.odata/", ODataEndpoint.Normalize("https://CLOBUS.uz/a/sbm/12345/odata/standard.odata/", o).ToString());
        Assert.Equal("https://app.clobus.uz/a/1/odata/standard.odata/", ODataEndpoint.Normalize("https://app.clobus.uz/a/1", o).ToString());
        Assert.Equal("clobus", ODataEndpoint.HostLabel(ODataEndpoint.Normalize("https://clobus.uz/a/1", o)));
        var other = new ODataOptions { AllowedHosts = ["1c.example.uz"] };
        Assert.Equal("other", ODataEndpoint.HostLabel(ODataEndpoint.Normalize("https://1c.example.uz/base", other)));
        Assert.Throws<ODataEndpointException>(() => ODataEndpoint.Normalize("https://clobus.uz/a/1", other));
    }

    [Fact]
    public void Localhost_over_http_only_when_development_allowed_it()
    {
        var o = new ODataOptions { AllowHttpLocalhostInDevelopment = true };
        Assert.Throws<ODataEndpointException>(() => ODataEndpoint.Normalize("http://localhost:5000/x", o)); // flag alone is not enough
        o.AllowLocalDevelopment();
        Assert.Equal("http://localhost:5000/x/odata/standard.odata/", ODataEndpoint.Normalize("http://localhost:5000/x", o).ToString());
        Assert.Throws<ODataEndpointException>(() => ODataEndpoint.Normalize("http://10.0.0.5/x", o));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("185.74.5.10", false)]
    [InlineData("2a00:1450:4001::1", false)]
    public void Internal_addresses_are_recognised(string address, bool expected) =>
        Assert.Equal(expected, ODataEndpoint.IsInternalAddress(IPAddress.Parse(address)));

    [Fact]
    public void Factory_checks_the_url_again()
    {
        var factory = new ODataHttpClientFactory(new ODataTransport(new FakeODataServer()), Microsoft.Extensions.Options.Options.Create(new ODataOptions()),
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        Assert.Throws<ODataEndpointException>(() => factory.Create(new Uri("https://169.254.169.254/latest/"), "u", new SecretValue("p")));
    }

    // ---- client ----

    [Fact]
    public async Task Only_GET_is_allowed()
    {
        var (client, server, _) = Create();
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Patch, HttpMethod.Put, HttpMethod.Delete })
            await Assert.ThrowsAsync<ODataMethodNotAllowedException>(() => client.SendAsync(method, "x", "Catalog_X(guid'6f9619ff-8b86-d011-b42d-00c04fc964ff')", "application/json", CancellationToken.None));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Reads_pages_counts_metadata_and_cyrillic_resources()
    {
        var (client, server, _) = Create();
        for (var i = 0; i < 5; i++) server.Add("Catalog_Контрагенты", FakeODataServer.Counterparty(Guid.NewGuid().ToString("D"), $"Контрагент {i}", "30100000" + i, "ЮридическоеЛицо"));
        var page = await client.GetAsync(new ODataQuery("Catalog_Контрагенты") { Top = 2, Skip = 2, OrderBy = "Ref_Key", InlineCount = true, Select = ["Ref_Key", "DataVersion"] }, CancellationToken.None);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(5, page.Count); // odata.count came as a string
        Assert.Equal(2, page.Items[0].EnumerateObject().Count());
        Assert.Equal(5, await client.CountAsync("Catalog_Контрагенты", null, CancellationToken.None));
        Assert.Contains("EntitySet Name=\"Catalog_Контрагенты\"", await client.GetMetadataAsync(CancellationToken.None));
        Assert.Contains("Catalog_Контрагенты", await client.ListResourcesAsync(CancellationToken.None));
        Assert.All(server.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.All(server.Requests, r => Assert.StartsWith("Basic ", r.Authorization));
        Assert.Contains(server.Requests, r => r.Resource == "Catalog_Контрагенты" && r.RawUrl.Contains("%D0%9A", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Access_errors_are_not_retried(HttpStatusCode status)
    {
        var (client, server, logs) = Create();
        server.Add("Catalog_X", new JsonObject());
        if (status == HttpStatusCode.Unauthorized) server.Password = "another-" + server.Password; else server.Forbidden = true;
        var ex = await Assert.ThrowsAsync<AdapterAuthenticationException>(() => client.GetAsync(new ODataQuery("Catalog_X"), CancellationToken.None));
        Assert.Single(server.Requests);
        Assert.Contains(((int)status).ToString(), ex.Message);
        AssertNoSecrets(server, logs, ex.Message);
    }

    [Fact]
    public async Task Rate_limit_and_server_errors_are_retried()
    {
        var (client, server, _) = Create(o => o.MaxRetries = 3);
        server.Add("Catalog_X", new JsonObject());
        server.Overrides.Enqueue(() => FakeODataServer.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMilliseconds(10)));
        server.Overrides.Enqueue(() => FakeODataServer.Status(HttpStatusCode.ServiceUnavailable));
        server.Overrides.Enqueue(() => FakeODataServer.Status(HttpStatusCode.BadGateway));
        var page = await client.GetAsync(new ODataQuery("Catalog_X"), CancellationToken.None);
        Assert.Single(page.Items);
        Assert.Equal(4, server.Requests.Count);
        Assert.Equal(3, client.Stats.Retries);
    }

    [Fact]
    public async Task Retries_end_with_a_transient_error()
    {
        var (client, server, _) = Create(o => o.MaxRetries = 2);
        for (var i = 0; i < 5; i++) server.Overrides.Enqueue(() => FakeODataServer.Status(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<AdapterTransientException>(() => client.GetAsync(new ODataQuery("Catalog_X"), CancellationToken.None));
        Assert.Equal(3, server.Requests.Count);

        var (client2, server2, _) = Create(o => o.MaxRetries = 1);
        for (var i = 0; i < 5; i++) server2.Overrides.Enqueue(() => FakeODataServer.Status(HttpStatusCode.TooManyRequests));
        await Assert.ThrowsAsync<AdapterRateLimitException>(() => client2.GetAsync(new ODataQuery("Catalog_X"), CancellationToken.None));
    }

    [Fact]
    public async Task A_slow_answer_times_out_and_is_retried()
    {
        var (client, server, _) = Create(o => { o.RequestTimeout = TimeSpan.FromMilliseconds(100); o.MaxRetries = 1; });
        server.Add("Catalog_X", new JsonObject());
        server.Delay = TimeSpan.FromSeconds(5);
        var ex = await Assert.ThrowsAsync<AdapterTransientException>(() => client.GetAsync(new ODataQuery("Catalog_X"), CancellationToken.None));
        Assert.Contains("no answer", ex.Message);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Redirects_and_bad_requests_are_refused_without_retry()
    {
        var (client, server, _) = Create();
        server.Overrides.Enqueue(() =>
        {
            var r = FakeODataServer.Status(HttpStatusCode.Found);
            r.Headers.Location = new Uri("http://169.254.169.254/");
            return r;
        });
        await Assert.ThrowsAsync<ODataProtocolException>(() => client.GetAsync(new ODataQuery("Catalog_X"), CancellationToken.None));
        server.Overrides.Enqueue(() => FakeODataServer.Status(HttpStatusCode.BadRequest));
        await Assert.ThrowsAsync<ODataProtocolException>(() => client.GetAsync(new ODataQuery("Catalog_X"), CancellationToken.None));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Requests_per_second_are_limited()
    {
        var (client, server, _) = Create(o => o.RequestsPerSecond = 20);
        server.Add("Catalog_X", new JsonObject());
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 6; i++) await client.CountAsync("Catalog_X", null, CancellationToken.None);
        // 6 requests at 20 per second: at least 5 intervals of 50 ms.
        Assert.True(watch.ElapsedMilliseconds >= 230, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Password_never_reaches_logs_or_errors()
    {
        var (client, server, logs) = Create(o => o.MaxRetries = 1);
        server.Add("Catalog_X", new JsonObject());
        await client.GetAsync(new ODataQuery("Catalog_X"), CancellationToken.None);
        server.Overrides.Enqueue(() => FakeODataServer.Status(HttpStatusCode.InternalServerError));
        server.Overrides.Enqueue(() => FakeODataServer.Status(HttpStatusCode.InternalServerError));
        var ex = await Assert.ThrowsAsync<AdapterTransientException>(() => client.GetAsync(new ODataQuery("Catalog_X"), CancellationToken.None));
        Assert.NotEmpty(logs.Lines);
        AssertNoSecrets(server, logs, ex.Message);
    }

    private static void AssertNoSecrets(FakeODataServer server, CapturingLoggerProvider logs, string message)
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(server.Login + ":" + server.Password));
        foreach (var text in logs.Lines.Append(message))
        {
            Assert.DoesNotContain(server.Password, text);
            Assert.DoesNotContain(basic, text);
            Assert.DoesNotContain("Authorization", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("clobus.uz", text); // neither the URL nor the host of the client's application
        }
    }

    // ---- profile and mapping ----

    [Fact]
    public void Built_in_profile_is_valid_and_marks_what_to_verify()
    {
        var profile = ODataProfileResolver.ByName("clobus-bp-uz", new ODataOptions());
        Assert.Equal(["counterparties", "contracts", "bank_accounts", "documents", "bank_documents"], profile.Entities.Select(e => e.Entity));
        Assert.All(profile.Entities.SelectMany(e => e.Sources), s => Assert.True(s.Resource.Verify));
        // Every mapping of the starting profile states explicitly whether it must be checked on a real application.
        var json = ODataProfile.BuiltIn["clobus-bp-uz"];
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        foreach (var entity in doc.RootElement.GetProperty("entities").EnumerateArray())
            foreach (var source in entity.GetProperty("sources").EnumerateArray())
            {
                foreach (var f in source.GetProperty("fields").EnumerateObject()) Assert.True(f.Value.TryGetProperty("verify", out _), f.Name);
                if (source.TryGetProperty("children", out var ch))
                    foreach (var t in ch.GetProperty("tables").EnumerateArray())
                        foreach (var f in t.GetProperty("fields").EnumerateObject()) Assert.True(f.Value.TryGetProperty("verify", out _), f.Name);
            }
    }

    [Theory]
    [InlineData("""{"name":"x","entities":[{"entity":"nope","sources":[{"resource":{"name":"Catalog_X"},"fields":{}}]}]}""", "unknown entity")]
    [InlineData("""{"name":"x","entities":[{"entity":"counterparties","sources":[{"resource":{"name":"Catalog_X"},"fields":{"colour":{"source":"A"}}}]}]}""", "not a field")]
    [InlineData("""{"name":"x","entities":[{"entity":"counterparties","sources":[{"resource":{"name":"Catalog X?"},"fields":{}}]}]}""", "not a valid resource")]
    [InlineData("""{"name":"x","entities":[{"entity":"counterparties","sources":[{"resource":{"name":"Catalog_X"},"fields":{"name":{"source":"A","transform":"eval"}}}]}]}""", "unknown transform")]
    [InlineData("""{"name":"x","entities":[{"entity":"settlement_balances","sources":[{"resource":{"name":"Catalog_X"},"fields":{}}]}]}""", "not keyed by ref")]
    [InlineData("""{"name":""", "not valid JSON")]
    public void Invalid_profiles_are_refused(string json, string reason)
    {
        var ex = Assert.Throws<ODataProfileException>(() => ODataProfile.Parse(json, "test"));
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void Profile_of_the_connection_settings_wins()
    {
        var inline = """{"profile_json":{"name":"mine","entities":[{"entity":"counterparties","sources":[{"resource":{"name":"Catalog_Партнеры"},"fields":{"name":{"source":"Description"}}}]}]}}""";
        Assert.Equal("mine", ODataProfileResolver.Resolve(inline, new ODataOptions()).Name);
        Assert.Equal("clobus-bp-uz", ODataProfileResolver.Resolve("""{"base_url":"x"}""", new ODataOptions()).Name);
        Assert.Throws<ODataProfileException>(() => ODataProfileResolver.Resolve("""{"profile":"missing"}""", new ODataOptions()));
    }

    [Fact]
    public void Records_are_mapped_like_the_extension_writes_them()
    {
        var profile = ODataProfileResolver.ByName("clobus-bp-uz", new ODataOptions());
        var lookups = new ODataLookupCache();
        lookups.Add("Catalog_Валюты", FakeODataServer.Uzs, new() { ["Description"] = "сўм", ["Code"] = "860" });
        lookups.Add("Catalog_Номенклатура", FakeODataServer.Item, new() { ["Description"] = "Товар А", ["Артикул"] = "06401" });
        lookups.Add("Catalog_ДоговорыКонтрагентов", FakeODataServer.Contract, new() { ["Номер"] = "7", ["Description"] = "Договор 7", ["Дата"] = "2026-01-10T00:00:00" });
        var mapper = new ODataRecordMapper(profile, lookups);
        var sale = FakeODataServer.Sale(FakeODataServer.Sale1, "0001", new DateOnly(2026, 11, 20), 1_120_000m);
        sale["DataVersion"] = "AAAAAAAAAAE=";
        sale["DeletionMark"] = false;
        var source = profile.Entities.Single(e => e.Entity == "documents").Sources[0];
        var item = mapper.Map(JsonSerializer.SerializeToElement(sale), source);

        Assert.Equal(FakeODataServer.Sale1, (string)item["ref"]!);
        Assert.Equal("Sales", (string)item["doc_kind"]!);
        Assert.Equal("2026-11-20T10:30:00", (string)item["date"]!);
        Assert.Equal("1120000", (string)item["amount_total"]!);   // amounts as decimal strings
        Assert.Equal("120000", (string)item["vat_amount"]!);       // sum of the lines' VAT
        Assert.Equal("UZS", (string)item["currency"]!);            // "сўм" is not a code: the numeric Code 860 gives UZS
        Assert.Equal("7", (string)item["contract_number"]!);
        Assert.Equal("AAAAAAAAAAE=", (string)item["data_version"]!);
        var lines = item["lines"]!.AsArray();
        Assert.Equal(2, lines.Count);
        Assert.Equal("12", (string)lines[0]!["vat_rate"]!);
        Assert.Equal("1120000", (string)lines[0]!["amount"]!);     // VAT included: the amount as is
        Assert.Equal("Без НДС", (string)lines[1]!["vat_rate"]!);
        Assert.Equal("Доставка", (string)lines[1]!["name"]!);
        Assert.Equal(2, (int)lines[1]!["line_no"]!);              // numbered across both tables
        Assert.Equal("Товар А", (string)lines[0]!["name"]!);
    }

    [Theory]
    [InlineData("НДС12", "12")]
    [InlineData("НДС12_112", "12")]
    [InlineData("НДС0", "0")]
    [InlineData("НДС15", "15")]
    [InlineData("НДС6", "6")]
    [InlineData("НДС6_106", "6")]
    [InlineData("БезНДС", "Без НДС")]
    [InlineData("12", "12")]
    public void Vat_rate_enum_names_become_numbers(string value, string expected) => Assert.Equal(expected, ODataRecordMapper.VatRate(value));

    [Fact]
    public void Empty_values_composite_refs_and_vat_added_to_lines()
    {
        var profile = ODataProfileResolver.ByName("clobus-bp-uz", new ODataOptions());
        var mapper = new ODataRecordMapper(profile, new ODataLookupCache());
        var payment = new JsonObject
        {
            ["Ref_Key"] = FakeODataServer.Payment1, ["Number"] = "15", ["Date"] = "2026-11-20T11:00:00", ["Posted"] = true,
            ["СчетОрганизации_Key"] = FakeODataServer.ODataNull, ["Контрагент"] = Guid.NewGuid().ToString(), ["Контрагент_Type"] = "StandardODATA.Catalog_ФизическиеЛица",
            ["СуммаДокумента"] = 10.5, ["НазначениеПлатежа"] = "  ", ["ДатаВходящегоДокумента"] = "0001-01-01T00:00:00",
        };
        var source = profile.Entities.Single(e => e.Entity == "bank_documents").Sources[1];
        var item = mapper.Map(JsonSerializer.SerializeToElement(payment), source);
        Assert.False(item.ContainsKey("bank_account_ref"));   // the empty reference
        Assert.False(item.ContainsKey("counterparty_ref"));   // a person, not a counterparty
        Assert.False(item.ContainsKey("purpose"));            // blank text
        Assert.False(item.ContainsKey("bank_document_date")); // the empty date
        Assert.Equal("10.5", (string)item["amount"]!);
        Assert.Contains("Document_СписаниеСРасчетногоСчета.РасшифровкаПлатежа", mapper.MissingProperties);

        var purchase = profile.Entities.Single(e => e.Entity == "documents").Sources[1];
        var doc = new JsonObject
        {
            ["Ref_Key"] = FakeODataServer.Purchase1, ["СуммаВключаетНДС"] = false,
            ["Товары"] = new JsonArray(new JsonObject { ["LineNumber"] = "1", ["Сумма"] = 100, ["СуммаНДС"] = 12, ["Цена"] = 100 }),
        };
        var mapped = mapper.Map(JsonSerializer.SerializeToElement(doc), purchase);
        Assert.Equal("112", (string)mapped["lines"]![0]!["amount"]!); // VAT not included: amount plus VAT
    }

    [Fact]
    public void Vat_rate_attribute_is_recognised_as_an_enumeration_or_a_reference()
    {
        static System.Text.Json.JsonElement Line(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;
        const string Doc = "Document_РеализацияТоваровУслуг";
        Assert.Equal(ODataVatRateKind.Enumeration, ODataVatRateKind.Detect(null, Doc, "Товары", "СтавкаНДС", Line("""{"СтавкаНДС":"НДС12"}""")).Kind);
        Assert.Equal(ODataVatRateKind.Reference, ODataVatRateKind.Detect(null, Doc, "Товары", "СтавкаНДС", Line("""{"СтавкаНДС_Key":"6f9619ff-8b86-d011-b42d-00c04fc964ff"}""")).Kind);

        string Metadata(string property, string type) =>
            $"""<edmx:Edmx xmlns:edmx="http://schemas.microsoft.com/ado/2007/06/edmx"><edmx:DataServices><Schema xmlns="http://schemas.microsoft.com/ado/2009/11/edm" Namespace="StandardODATA"><EntityType Name="{Doc}_Товары_RowType"><Property Name="LineNumber" Type="Edm.Int64"/><Property Name="{property}" Type="{type}"/></EntityType></Schema></edmx:DataServices></edmx:Edmx>""";
        Assert.Equal(ODataVatRateKind.Enumeration, ODataVatRateKind.Detect(Metadata("СтавкаНДС", "Edm.String"), Doc, "Товары", "СтавкаНДС", null).Kind);
        Assert.Equal(ODataVatRateKind.Reference, ODataVatRateKind.Detect(Metadata("СтавкаНДС_Key", "Edm.Guid"), Doc, "Товары", "СтавкаНДС", null).Kind);
        Assert.Equal(ODataVatRateKind.NotFound, ODataVatRateKind.Detect(Metadata("Цена", "Edm.Double"), Doc, "Товары", "СтавкаНДС", null).Kind);
        Assert.Equal(ODataVatRateKind.NotFound, ODataVatRateKind.Detect(Metadata("СтавкаНДС", "Edm.String"), Doc, "Услуги", "СтавкаНДС", null).Kind);
    }

    // ---- numbers: one canonical form, so the same value always gives the same item and the same content hash ----

    private static readonly string[] SixForms = ["6", "\"6\"", "\"6.0000\"", "6.00"];

    private static ODataSource Source(string field, ODataFieldMap map) => new()
    {
        Resource = new ODataNamed { Name = "Document_Test" },
        Fields = new Dictionary<string, ODataFieldMap> { [field] = map },
    };

    private static JsonObject MapOne(ODataSource source, string json) =>
        new ODataRecordMapper(ODataProfileResolver.ByName("clobus-bp-uz", new ODataOptions()), new ODataLookupCache())
            .Map(JsonDocument.Parse(json).RootElement, source);

    private static void AssertOneForm(Func<string, JsonObject> map, string field, string expected)
    {
        var items = SixForms.Select(map).ToList();
        Assert.All(items, i => Assert.Equal(expected, (string)i[field]!));
        var hashes = items.Select(i => Synect.Application.Ingest.IngestHash.Compute(JsonSerializer.SerializeToElement(i))).Distinct().ToList();
        Assert.Single(hashes);
    }

    [Theory]
    [InlineData("money")]
    [InlineData("decimal")]
    public void Money_and_decimal_have_one_form_and_one_hash(string transform)
    {
        var source = Source("amount", new ODataFieldMap { Source = "Сумма", Transform = transform });
        AssertOneForm(v => MapOne(source, $$"""{"Ref_Key":"{{FakeODataServer.Sale1}}","Сумма":{{v}}}"""), "amount", "6");
    }

    [Fact]
    public void Amount_with_vat_has_one_form_and_one_hash()
    {
        var source = Source("amount", new ODataFieldMap { Source = "Сумма", VatSource = "СуммаНДС", HeaderFlag = "СуммаВключаетНДС", Transform = "amount_with_vat" });
        // VAT included: the amount as is; and VAT added: 6 + 0.
        AssertOneForm(v => MapOne(source, $$"""{"Ref_Key":"{{FakeODataServer.Sale1}}","СуммаВключаетНДС":true,"Сумма":{{v}},"СуммаНДС":1}"""), "amount", "6");
        AssertOneForm(v => MapOne(source, $$"""{"Ref_Key":"{{FakeODataServer.Sale1}}","СуммаВключаетНДС":false,"Сумма":{{v}},"СуммаНДС":"0.00"}"""), "amount", "6");
    }

    [Fact]
    public void Numeric_vat_rate_has_the_same_form_as_other_numbers()
    {
        var source = Source("vat_rate", new ODataFieldMap { Source = "СтавкаНДС", Transform = "vat_rate" });
        AssertOneForm(v => MapOne(source, $$"""{"Ref_Key":"{{FakeODataServer.Sale1}}","СтавкаНДС":{{v}}}"""), "vat_rate", "6");
        Assert.Equal("12", (string)MapOne(source, $$"""{"Ref_Key":"{{FakeODataServer.Sale1}}","СтавкаНДС":"НДС12_112"}""")["vat_rate"]!);
        Assert.Equal("Без НДС", (string)MapOne(source, $$"""{"Ref_Key":"{{FakeODataServer.Sale1}}","СтавкаНДС":"БезНДС"}""")["vat_rate"]!);
        Assert.Equal("12%", (string)MapOne(source, $$"""{"Ref_Key":"{{FakeODataServer.Sale1}}","СтавкаНДС":"12%"}""")["vat_rate"]!); // not a plain number: as received
    }
}
