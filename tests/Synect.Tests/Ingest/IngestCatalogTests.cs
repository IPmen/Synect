using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Synect.Application.Abstractions;
using Synect.Application.Accounts;
using Synect.Application.Freshness;
using Synect.Application.Ingest;
using Synect.Domain.Accounting;
using Synect.Domain.Counterparties;
using Synect.Domain.Sync;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Infrastructure.Persistence.Migrations;
using Synect.Tests.Infrastructure;
using Synect.Tests.Postgres;

namespace Synect.Tests.Ingest;

/// <summary>Catalog of 1C for the Mailo bot: storage, validation, order, full exports of current-state tables, freshness per group.</summary>
public class IngestCatalogTests(IngestFixture f) : IClassFixture<IngestFixture>
{
    private void RequirePostgres() => Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);

    private static DateTimeOffset T(int minutes) => new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.FromHours(5)).AddMinutes(minutes);

    private static Dictionary<string, object?> Item(string r, string name = "Сок яблочный 1 л", int at = 0, params string[] barcodes) =>
        OneCSampleClient.CatalogItem(r, name, "cat-1", T(at), "Goods", barcodes);

    private static Dictionary<string, object?> Price(string item, decimal price, int at = 0, string type = "pt-1") =>
        OneCSampleClient.Price(item, type, price, T(at));

    private static Dictionary<string, object?> Stock(string item, decimal quantity, int at = 0, string warehouse = "wh-1") =>
        OneCSampleClient.Stock(item, warehouse, quantity, T(at));

    [Fact]
    public async Task Catalog_records_are_stored_with_types_defaults_unknown_fields_and_a_search_name()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Catalog storage");
        var client = f.Client(org);

        var juice = Item("it-1", "Сок «Яблочный» 1 л", 0, OneCSampleClient.Barcode(1), OneCSampleClient.Barcode(2), OneCSampleClient.Barcode(1));
        juice["ВесНетто"] = 1.05m;
        juice["vat_rate"] = "Без НДС";
        juice.Remove("is_archived");
        var camera = Item("it-2", "IP камера Hikvision");
        camera["item_type"] = "Комплект";
        camera["barcodes"] = "4780000000099";
        var service = OneCSampleClient.CatalogItem("it-3", "Доставка", null, T(0), "услуга");
        var items = await client.SendAsync(client.Envelope("items", "cat-items", 1, new object[] { juice, camera, service }));
        Assert.Equal("accepted", items.Get("status"));

        string? V(string column, string reference, string table = "catalog_items") => f.ValueAsync(table, org.TenantId, $"ref = '{reference}'", column).Result;
        Assert.Equal("Goods", V("item_type", "it-1"));
        Assert.Equal("{4780000000001,4780000000002}", V("barcodes", "it-1"));
        Assert.Null(V("vat_rate", "it-1"));
        Assert.Equal("false", V("is_archived", "it-1"));
        Assert.Equal("1.05", V("extra->>'ВесНетто'", "it-1"));
        Assert.Equal("Без НДС", V("extra->>'vat_rate'", "it-1"));
        Assert.Equal(CounterpartyNameNormalizer.NormalizeText("Сок «Яблочный» 1 л"), V("search_name", "it-1"));
        Assert.DoesNotMatch("[А-Яа-я«»]", V("search_name", "it-1")!);
        // Legal forms are not removed from goods names ("IP" stays), unlike counterparty names.
        Assert.StartsWith("ip kamera", V("search_name", "it-2"));
        Assert.Null(V("item_type", "it-2"));
        Assert.Equal("Комплект", V("extra->>'item_type'", "it-2"));
        Assert.Equal("{4780000000099}", V("barcodes", "it-2"));
        Assert.Equal("Service", V("item_type", "it-3"));
        Assert.Equal("{}", V("barcodes", "it-3"));

        await client.SendAsync(client.Envelope("price_types", "cat-pt", 1, new[] { OneCSampleClient.PriceType("pt-1", "Розничная", true, T(0)) }));
        Assert.Equal("UZS", V("currency", "pt-1", "catalog_price_types"));
        Assert.Equal("true", V("is_default", "pt-1", "catalog_price_types"));

        var prices = await client.SendAsync(client.Envelope("prices", "cat-pr", 1, new[] { Price("it-1", 15_000.5m), Price("it-1", 12_000m, type: "pt-2") }));
        Assert.Equal("accepted", prices.Get("status"));
        Assert.Equal("it-1|pt-1", IngestFixture.Item(prices.Body, 0).GetProperty("ref").GetString());
        Assert.Equal("1500050", await f.ValueAsync("catalog_prices", org.TenantId, "item_ref = 'it-1' AND price_type_ref = 'pt-1'", "price_minor"));

        var exported = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var noTime = Stock("it-1", 12.375m);
        noTime.Remove("as_of");
        noTime.Remove("modified_at");
        var stock = await client.SendAsync(client.Envelope("stock", "cat-st", 1, new object[] { noTime, Stock("it-2", -3m, warehouse: "wh-2") }, exportedAt: exported));
        Assert.Equal("accepted", stock.Get("status"));
        Assert.Equal("12.375000", await f.ValueAsync("catalog_stock", org.TenantId, "item_ref = 'it-1'", "quantity"));
        Assert.Null(await f.ValueAsync("catalog_stock", org.TenantId, "item_ref = 'it-1'", "reserved"));
        Assert.Equal(exported, DateTimeOffset.Parse(await f.ValueAsync("catalog_stock", org.TenantId, "item_ref = 'it-1'", "to_char(as_of at time zone 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS\"Z\"')") ?? ""));
        Assert.Equal("-3.000000", await f.ValueAsync("catalog_stock", org.TenantId, "item_ref = 'it-2'", "quantity"));

        // Raw layer: every item as received under source "1c".
        Assert.Equal(3L, await f.ScalarAsync<long>($"SELECT count(*) FROM raw_objects WHERE tenant_id = '{org.TenantId}' AND source_code = '1c' AND object_type = '1c.items'"));
        Assert.Equal(2L, await f.ScalarAsync<long>($"SELECT count(*) FROM raw_objects WHERE tenant_id = '{org.TenantId}' AND object_type = '1c.stock'"));
    }

    [Fact]
    public async Task Catalog_items_are_validated_one_by_one()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Catalog validation");
        var client = f.Client(org);
        var letters = Item("v-letters", barcodes: "4780A");
        var nested = Item("v-nested");
        nested["barcodes"] = new object[] { new { code = "1" } };
        var noName = Item("v-noname");
        noName.Remove("name");
        var items = await client.SendAsync(client.Envelope("items", "val-items", 1, new object[] { Item("v-ok", barcodes: "123"), letters, nested, noName, Item("") }));
        Assert.Equal([("accepted", null), ("rejected", "invalid_barcode"), ("rejected", "invalid_barcode"), ("rejected", "missing_field"), ("rejected", "empty_ref")],
            items.Items.ToList());

        var fractional = Price("v-ok", 0m);
        fractional["price"] = "100.001";
        var noPrice = Price("v-ok", 1m, type: "pt-2");
        noPrice.Remove("price");
        var noItem = Price("", 1m, type: "pt-3");
        var badCurrency = Price("v-ok", 1m, type: "pt-4");
        badCurrency["currency"] = "сумы?";
        var prices = await client.SendAsync(client.Envelope("prices", "val-prices", 1, new object[] { Price("v-ok", 10m), fractional, noPrice, noItem, badCurrency }));
        Assert.Equal([("accepted", null), ("rejected", "fractional_minor_units"), ("rejected", "missing_field"), ("rejected", "missing_field"), ("rejected", "invalid_currency")],
            prices.Items.ToList());

        var badQuantity = Stock("v-ok", 0m, warehouse: "wh-2");
        badQuantity["quantity"] = "много";
        var longRef = Stock(new string('x', 65), 1m);
        var stock = await client.SendAsync(client.Envelope("stock", "val-stock", 1, new object[] { Stock("v-ok", 1m), badQuantity, longRef }));
        Assert.Equal([("accepted", null), ("rejected", "invalid_number"), ("rejected", "ref_too_long")], stock.Items.ToList());
        Assert.Equal(1L, await f.CountAsync("catalog_stock", org.TenantId));
    }

    [Fact]
    public async Task Prices_and_stock_follow_order_idempotency_and_arrive_before_their_items()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Catalog order");
        var client = f.Client(org);

        // A price and stock before the item and its price type: accepted, linked later by ref.
        Assert.Equal("accepted", (await client.SendAsync(client.Envelope("prices", "ord-p1", 1, new[] { Price("late-item", 100m, at: 10) }))).Get("status"));
        Assert.Equal("accepted", (await client.SendAsync(client.Envelope("stock", "ord-s1", 1, new[] { Stock("late-item", 5m, at: 10) }))).Get("status"));
        Assert.Equal("accepted", (await client.SendAsync(client.Envelope("items", "ord-i1", 1, new[] { Item("late-item") }))).Get("status"));

        var late = await client.SendAsync(client.Envelope("prices", "ord-p2", 1, new[] { Price("late-item", 90m, at: 0) }));
        Assert.Equal([("unchanged", "older_than_stored")], late.Items.ToList());
        Assert.Equal("10000", await f.ValueAsync("catalog_prices", org.TenantId, "item_ref = 'late-item'", "price_minor"));

        var updated = await f.ValueAsync("catalog_prices", org.TenantId, "item_ref = 'late-item'", "updated_at");
        var same = await client.SendAsync(client.Envelope("prices", "ord-p3", 1, new[] { Price("late-item", 100m, at: 10) }));
        Assert.Equal([("unchanged", "same_content")], same.Items.ToList());
        Assert.Equal(updated, await f.ValueAsync("catalog_prices", org.TenantId, "item_ref = 'late-item'", "updated_at"));

        var newer = await client.SendAsync(client.Envelope("stock", "ord-s2", 1, new[] { Stock("late-item", 4m, at: 11), Stock("late-item", 3m, at: 12) }));
        Assert.Equal("accepted", newer.Get("status"));
        Assert.Equal("3.000000", await f.ValueAsync("catalog_stock", org.TenantId, "item_ref = 'late-item'", "quantity"));
        Assert.Equal(1L, await f.CountAsync("catalog_stock", org.TenantId));

        // The same package again is answered from the journal.
        var envelope = client.Envelope("stock", "ord-s3", 1, new[] { Stock("late-item", 2m, at: 13) });
        Assert.Equal("accepted", (await client.SendAsync(envelope)).Get("status"));
        Assert.Equal("duplicate", (await client.SendAsync(envelope)).Get("status"));

        // A deletion in a delta carries only the key and marks the row (it is not removed; only full exports remove rows).
        var deletion = await client.SendAsync(client.Envelope("stock", "ord-s4", 1, new object[]
        {
            new Dictionary<string, object?> { ["item_ref"] = "late-item", ["warehouse_ref"] = "wh-1", ["is_deleted"] = true, ["modified_at"] = "2026-11-01T10:00:00" },
            new Dictionary<string, object?> { ["item_ref"] = "never", ["warehouse_ref"] = "wh-1", ["is_deleted"] = true },
        }));
        Assert.Equal([("accepted", null), ("unchanged", "unknown_ref_deleted")], deletion.Items.ToList());
        Assert.Equal("true", await f.ValueAsync("catalog_stock", org.TenantId, "item_ref = 'late-item'", "is_deleted"));
        Assert.Equal("2.000000", await f.ValueAsync("catalog_stock", org.TenantId, "item_ref = 'late-item'", "quantity"));
    }

    [Fact]
    public async Task Full_exports_remove_missing_prices_and_stock_only_after_the_last_batch_of_the_same_export_and_journal_them()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Catalog full");
        var other = await f.NewOrganizationAsync("Catalog full other");
        var client = f.Client(org);
        var stock = Enumerable.Range(1, 5).Select(i => Stock($"f-{i}", i)).ToList();
        await client.SendAsync(client.Envelope("stock", "full-delta", 1, stock));
        await f.Client(other).SendAsync(f.Client(other).Envelope("stock", "full-delta", 1, stock));
        Assert.Equal(5L, await f.CountAsync("catalog_stock", org.TenantId));

        // A truncated export (no is_last_batch) removes nothing.
        var truncated = await client.SendAsync(client.Envelope("stock", "full-a", 1, stock.Take(2), "full", "A"));
        Assert.Equal(0, truncated.Int("removed"));
        Assert.Equal(5L, await f.CountAsync("catalog_stock", org.TenantId));

        // Export B in two packages; the first package of B says nothing is removed yet.
        var b1 = await client.SendAsync(client.Envelope("stock", "full-b", 1, stock.Take(2), "full", "B", false));
        Assert.Equal(0, b1.Int("removed"));
        // The last batch of ANOTHER export (C, which only had f-1..f-3) must not count the keys of B.
        var c = await client.SendAsync(client.Envelope("stock", "full-c", 1, stock.Take(3), "full", "C", true));
        Assert.Equal(2, c.Int("removed"));
        Assert.Equal(3L, await f.CountAsync("catalog_stock", org.TenantId));

        // The organization of another key is untouched.
        Assert.Equal(5L, await f.CountAsync("catalog_stock", other.TenantId));

        // Journal: identifiers and counters only.
        var batch = await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'full-c'", "id");
        Assert.Equal("2", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'full-c'", "removed_rows"));
        Assert.Equal("0", await f.ValueAsync("ingest_batches", org.TenantId, "batch_id = 'full-c'", "marked_deleted"));
        var removed = await f.QueryAdminAsync(
            $"SELECT entity, item_key, full_sync_id, ingest_batch_id::text FROM ingest_removed_rows WHERE tenant_id = '{org.TenantId}' ORDER BY item_key",
            r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        Assert.Equal([("stock", "f-4\u001fwh-1", "C", batch!), ("stock", "f-5\u001fwh-1", "C", batch!)], removed);
        var columns = await f.QueryAdminAsync("SELECT column_name FROM information_schema.columns WHERE table_name = 'ingest_removed_rows' ORDER BY 1",
            r => r.GetString(0));
        Assert.Equal(["entity", "full_sync_id", "id", "ingest_batch_id", "item_key", "removed_at", "tenant_id"], columns);

        // Prices: the same rule. Items: a completed full export MARKS them deleted, never removes them.
        await client.SendAsync(client.Envelope("prices", "fp-delta", 1, new[] { Price("p-1", 1m), Price("p-2", 2m), Price("p-3", 3m) }));
        var prices = await client.SendAsync(client.Envelope("prices", "fp-full", 1, new[] { Price("p-1", 1m) }, "full", "P", true));
        Assert.Equal(2, prices.Int("removed"));
        Assert.Equal(1L, await f.CountAsync("catalog_prices", org.TenantId));

        await client.SendAsync(client.Envelope("items", "fi-delta", 1, new[] { Item("i-1"), Item("i-2") }));
        var items = await client.SendAsync(client.Envelope("items", "fi-full", 1, new[] { Item("i-1") }, "full", "I", true));
        Assert.Equal(1, items.Int("marked_deleted"));
        Assert.Equal(0, items.Int("removed"));
        Assert.Equal("true", await f.ValueAsync("catalog_items", org.TenantId, "ref = 'i-2'", "is_deleted"));
    }

    [Fact]
    public async Task Freshness_is_kept_per_data_group_and_catalog_packages_never_refresh_accounting()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Catalog freshness");
        var client = f.Client(org);
        async Task<SourceFreshness> OneCAsync() =>
            (await f.Host.InTenantAsync(org.TenantId, sp => sp.GetRequiredService<ISourceFreshnessQuery>().GetAsync(org.TenantId, CancellationToken.None)))
            .Single(s => s.Kind == AdapterKind.Accounting1C);
        IngestGroupFreshness Group(SourceFreshness s, string name) => s.DataGroups!.Single(g => g.Group == name);

        var empty = await OneCAsync();
        Assert.Equal(IngestGroupFreshness.NeverReceived, Group(empty, "accounting").State);
        Assert.Equal(IngestGroupFreshness.NeverReceived, Group(empty, "catalog").State);
        Assert.True(empty.IsStale);

        await client.SendAsync(client.Envelope("stock", "fr-st", 1, new[] { Stock("x", 1m) }));
        var catalogOnly = await OneCAsync();
        Assert.Equal(IngestGroupFreshness.Fresh, Group(catalogOnly, "catalog").State);
        Assert.Equal(TimeSpan.FromMinutes(60), Group(catalogOnly, "catalog").StaleAfter);
        Assert.Equal(IngestGroupFreshness.NeverReceived, Group(catalogOnly, "accounting").State);
        Assert.False(catalogOnly.IsStale);

        await client.SendAsync(client.Envelope("counterparties", "fr-cp", 1, new[] { OneCSampleClient.Counterparty("c", "C", null, T(0)) }));
        var now = f.Host.Time.Now;
        await f.ExecAdminAsync($"UPDATE ingest_freshness SET last_accepted_at = '{now.AddHours(-49):O}' WHERE connection_id = '{org.Key.ConnectionId}' AND data_group = 'Accounting'");
        // Catalog packages keep coming every minute: the accounting data stays stale.
        await client.SendAsync(client.Envelope("stock", "fr-st2", 1, new[] { Stock("x", 2m, at: 1) }));
        await f.ExecAdminAsync($"UPDATE ingest_freshness SET last_accepted_at = '{now.AddMinutes(-1):O}' WHERE connection_id = '{org.Key.ConnectionId}' AND data_group = 'Catalog'");
        var mixed = await OneCAsync();
        Assert.Equal(IngestGroupFreshness.Stale, Group(mixed, "accounting").State);
        Assert.Equal(TimeSpan.FromHours(48), Group(mixed, "accounting").StaleAfter);
        Assert.Equal(IngestGroupFreshness.Fresh, Group(mixed, "catalog").State);
        Assert.True(mixed.IsStale);

        await f.ExecAdminAsync($"UPDATE ingest_freshness SET last_accepted_at = '{now.AddHours(-2):O}' WHERE connection_id = '{org.Key.ConnectionId}'");
        // Two hours: within 48 h for accounting, beyond 60 minutes for the catalog.
        var both = await OneCAsync();
        Assert.Equal(IngestGroupFreshness.Fresh, Group(both, "accounting").State);
        Assert.Equal(IngestGroupFreshness.Stale, Group(both, "catalog").State);

        // The portfolio rows (get_connections_overview) carry the same groups.
        var summary = await f.Host.InTenantAsync(org.TenantId, sp => sp.GetRequiredService<PortfolioQueries>().SummaryAsync(false, CancellationToken.None));
        var groups = summary.Data.Connections.Single(c => c.Kind == AdapterKind.Accounting1C).DataGroups!;
        Assert.Equal([("accounting", IngestGroupFreshness.Fresh), ("catalog", IngestGroupFreshness.Stale)], groups.Select(g => (g.Group, g.State)).ToList());
    }

    [Fact]
    public void Accounting_threshold_reads_the_former_setting_name_as_a_fallback()
    {
        static IngestOptions Bind(params (string Key, string Value)[] settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build();
            var options = new IngestOptions();
            configuration.GetSection("Ingest").Bind(options);
            return options;
        }

        Assert.Equal(TimeSpan.FromHours(48), Bind().AccountingStaleAfterOrDefault);
        Assert.Equal(TimeSpan.FromMinutes(60), Bind().CatalogStaleAfter);
        Assert.Equal(TimeSpan.FromHours(30), Bind(("Ingest:StaleAfter", "1.06:00:00")).AccountingStaleAfterOrDefault);
        Assert.Equal(TimeSpan.FromHours(12),
            Bind(("Ingest:StaleAfter", "1.06:00:00"), ("Ingest:AccountingStaleAfter", "12:00:00")).AccountingStaleAfterOrDefault);
        Assert.Equal(TimeSpan.FromMinutes(15), Bind(("Ingest:CatalogStaleAfter", "00:15:00")).StaleAfterOf(IngestDataGroup.Catalog));

        // Defaults of the limits, in code and in the shipped appsettings.json (which must not set the accounting threshold,
        // so that a former Ingest:StaleAfter configured elsewhere still applies).
        Assert.Equal(120, new IngestRateLimitOptions().PackagesPerMinute);
        Assert.Equal(20, new IngestRateLimitOptions().FailedAuthPerMinutePerIp);
        var shipped = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false).Build();
        var fromFile = new IngestOptions();
        shipped.GetSection("Ingest").Bind(fromFile);
        Assert.Equal(120, fromFile.RateLimit.PackagesPerMinute);
        Assert.Equal(20, fromFile.RateLimit.FailedAuthPerMinutePerIp);
        Assert.Null(fromFile.AccountingStaleAfter);
        Assert.Null(fromFile.StaleAfter);
        Assert.Equal(TimeSpan.FromMinutes(60), fromFile.CatalogStaleAfter);
    }

    [Fact]
    public async Task With_the_default_limits_120_packages_per_key_pass_and_key_guessing_is_still_stopped_after_20_failures_per_ip()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Catalog limits");
        var defaults = new[]
        {
            ("Ingest:RateLimit:PackagesPerMinute", IngestRateLimitOptions.DefaultPackagesPerMinute.ToString()),
            ("Ingest:RateLimit:FailedAuthPerMinutePerIp", IngestRateLimitOptions.DefaultFailedAuthPerMinutePerIp.ToString()),
        };

        // A valid key: 120 packages in the window, the 121st is refused.
        var api = f.CreateApi(defaults);
        var http = api.CreateClient();
        var client = f.Client(org, api);
        for (var i = 1; i <= 120; i++)
        {
            using var ok = await PostAsync(http, org.Key.Key, client.Envelope("stock", "lim-" + i, 1, Array.Empty<object>()));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using (var limited = await PostAsync(http, org.Key.Key, client.Envelope("stock", "lim-121", 1, Array.Empty<object>())))
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        // Guessing: 20 wrong keys from one IP get 401, then the IP gets 429 even with a valid key (the higher per-key limit
        // does not loosen the per-IP limit, which is checked before the key).
        var guessing = f.CreateApi(defaults).CreateClient();
        for (var i = 0; i < 20; i++)
        {
            using var denied = await PostAsync(guessing, IngestKeyFormat.Generate(), client.Envelope("stock", "g-" + i, 1, Array.Empty<object>()));
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using (var blocked = await PostAsync(guessing, IngestKeyFormat.Generate(), client.Envelope("stock", "g-21", 1, Array.Empty<object>())))
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        using (var blockedValid = await PostAsync(guessing, org.Key.Key, client.Envelope("stock", "g-22", 1, Array.Empty<object>())))
            Assert.Equal(HttpStatusCode.TooManyRequests, blockedValid.StatusCode);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient http, string key, object envelope)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, OneCSampleClient.Route)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http.SendAsync(request);
    }

    [Fact]
    public async Task Receiving_catalog_and_accounting_packages_recalculates_nothing()
    {
        RequirePostgres();
        var org = await f.NewOrganizationAsync("Catalog no recalculation");
        var tables = new[] { "reconciliation_runs", "reconciliation_matches", "attention_items", "attention_item_events", "analytics_counterparty_monthly", "sync_jobs", "sync_runs" };
        async Task<long[]> CountsAsync() => await Task.WhenAll(tables.Select(t => f.CountAsync(t, org.TenantId)));
        var before = await CountsAsync();

        var steps = new OneCSampleScenarios(f.Client(org));
        await steps.BasicAsync();
        Assert.All(steps.Steps, s => Assert.True(s.Ok, s.Name + ": " + s.Actual));
        Assert.Equal(before, await CountsAsync());

        // By construction: the receiver depends on nothing that recalculates.
        var parameters = typeof(IngestService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Namespace ?? "").ToList();
        Assert.DoesNotContain(parameters, n => n.Contains("Reconciliation") || n.Contains("Attention") || n.Contains("Analytics") || n.Contains("Queue"));
    }

    [Fact]
    public async Task New_tables_are_isolated_by_row_level_security_and_organizations_of_one_account_never_mix()
    {
        RequirePostgres();
        var a = await f.NewOrganizationAsync("Catalog RLS A");
        var b = await f.NewOrganizationAsync("Catalog RLS B");
        // One account holding both organizations (stage 6.5); the same 1C refs in both.
        var account = Guid.NewGuid();
        await f.ExecAdminAsync($"""
            INSERT INTO accounts (id, name, status, created_at) VALUES ('{account}', 'Catalog account', 'Active', now());
            INSERT INTO account_tenants (account_id, tenant_id, added_at) VALUES ('{account}', '{a.TenantId}', now()), ('{account}', '{b.TenantId}', now());
            """);
        foreach (var (org, price) in new[] { (a, 100m), (b, 200m) })
        {
            var client = f.Client(org);
            await new OneCSampleScenarios(client).BasicAsync();
            await client.SendAsync(client.Envelope("prices", "rls-p", 1, new[] { Price("same-item", price) }));
            await client.SendAsync(client.Envelope("stock", "rls-s", 1, new[] { Stock("gone", 1m) }));
            await client.SendAsync(client.Envelope("stock", "rls-full", 1, new[] { Stock("same-item", 1m) }, "full", "R", true));
        }
        Assert.Equal("10000", await f.ValueAsync("catalog_prices", a.TenantId, "item_ref = 'same-item'", "price_minor"));
        Assert.Equal("20000", await f.ValueAsync("catalog_prices", b.TenantId, "item_ref = 'same-item'", "price_minor"));

        foreach (var table in CatalogIngest.ForcedTables)
        {
            Assert.Equal("true,true", await f.ScalarAsync<string>(
                $"SELECT relrowsecurity::text || ',' || relforcerowsecurity::text FROM pg_class WHERE relname = '{table}'"));
            Assert.True(await f.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE tenant_id = '{a.TenantId}'") > 0, table);
            Assert.Equal(0, await CountAsAppAsync(null, table));
            Assert.True(await CountAsAppAsync(a.TenantId, table) > 0, table);
            Assert.Equal(0, await CountAsAppAsync(a.TenantId, table, $"tenant_id <> '{a.TenantId}'"));
        }

        // Writing into another organization is refused by the database.
        await using var scope = f.Host.Scope(a.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<ISynectDbContext>();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.InTransactionAsync(ct => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ingest_freshness (tenant_id, connection_id, data_group, last_accepted_at) VALUES ({b.TenantId}, {b.Key.ConnectionId}, 'Catalog', now())
            ON CONFLICT DO NOTHING
            """, ct), CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        // The ingest key of A never writes into B (the organization comes from the key; B's TIN in the package is refused).
        var mixed = await f.Client(a.Key.Key, b.Tin).SendAsync(f.Client(a.Key.Key, b.Tin).Envelope("prices", "mix", 1, new[] { Price("same-item", 1m) }));
        Assert.Equal(HttpStatusCode.Forbidden, mixed.Status);
        Assert.Equal("20000", await f.ValueAsync("catalog_prices", b.TenantId, "item_ref = 'same-item'", "price_minor"));

        // Indexes of the new tables lead with tenant_id (GIN search indexes included).
        var nonTenantFirst = await f.ScalarAsync<long>($"""
            SELECT count(*) FROM pg_index i JOIN pg_class c ON c.oid = i.indrelid
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = i.indkey[0]
            WHERE c.relname = ANY(ARRAY['{string.Join("','", CatalogIngest.ForcedTables)}']) AND a.attname <> 'tenant_id'
            """);
        Assert.Equal(0L, nonTenantFirst);
        var search = await f.QueryAdminAsync("SELECT indexdef FROM pg_indexes WHERE tablename = 'catalog_items' AND indexdef LIKE '%USING gin%' ORDER BY indexname",
            r => r.GetString(0));
        Assert.Equal(2, search.Count);
        Assert.Contains(search, d => d.Contains("barcodes"));
        Assert.Contains(search, d => d.Contains("gin_trgm_ops"));
    }

    private Task<int> CountAsAppAsync(Guid? tenant, string table, string where = "true") =>
        f.Host.QueryAsync(tenant, db => db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table} WHERE {where}").SingleAsync());
}
