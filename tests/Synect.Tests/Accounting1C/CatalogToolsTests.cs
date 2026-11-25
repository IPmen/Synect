using System.Text.Json;
using Synect.Infrastructure.Adapters.Fake;
using Synect.Tests.Postgres;

namespace Synect.Tests.Accounting1C;

/// <summary>
/// get_1c_stock, get_1c_stock_summary and get_1c_prices over a small pharmacy catalog sent through the real 1C receiver:
/// 2 pharmacies, 3 medicines, a default retail and a wholesale price type, stock with zero and negative rows.
/// </summary>
public class CatalogToolsTests(AccountingFixture f) : IClassFixture<AccountingFixture>
{
    private static readonly DateTimeOffset T = new(2026, 11, 20, 9, 0, 0, TimeSpan.FromHours(5));

    private void RequirePostgres() => Assert.SkipUnless(f.Available, PostgresFixture.SkipReason);

    /// <summary>Sends the catalog to the "Empty" organization of this class's own fixture (idempotent: the same rows again are unchanged).</summary>
    private async Task SendCatalogAsync()
    {
        var c = f.Ingest.Client(f.Empty);
        var b = "cat-" + Guid.NewGuid().ToString("N")[..8];
        var sends = new[]
        {
            c.Envelope("categories", b + "-c", 1, new[] { OneCSampleClient.Category("cat-med", "Лекарства", null, T) }),
            c.Envelope("price_types", b + "-t", 1, new[]
            {
                OneCSampleClient.PriceType("pt-r", "Розничная", true, T), OneCSampleClient.PriceType("pt-w", "Оптовая", false, T),
            }),
            c.Envelope("warehouses", b + "-w", 1, new[]
            {
                OneCSampleClient.Warehouse("wh-1", "Аптека Чиланзар", T), OneCSampleClient.Warehouse("wh-2", "Аптека Юнусабад", T),
            }),
            c.Envelope("items", b + "-i", 1, new[]
            {
                OneCSampleClient.CatalogItem("it-1", "Парацетамол 500 мг", "cat-med", T, "Goods", OneCSampleClient.Barcode(1)),
                OneCSampleClient.CatalogItem("it-2", "Ибупрофен 200 мг", "cat-med", T),
                OneCSampleClient.CatalogItem("it-3", "Витамин C", "cat-med", T),
            }),
            c.Envelope("prices", b + "-p", 1, new[]
            {
                OneCSampleClient.Price("it-1", "pt-r", 12000m, T), OneCSampleClient.Price("it-1", "pt-w", 10000m, T),
                OneCSampleClient.Price("it-2", "pt-r", 25000m, T), OneCSampleClient.Price("it-3", "pt-w", 8000m, T),
            }),
            c.Envelope("stock", b + "-s", 1, new[]
            {
                OneCSampleClient.Stock("it-1", "wh-1", 10m, T), OneCSampleClient.Stock("it-1", "wh-2", 0m, T),
                OneCSampleClient.Stock("it-2", "wh-1", 3m, T, reserved: 1m), OneCSampleClient.Stock("it-2", "wh-2", -2m, T),
                OneCSampleClient.Stock("it-3", "wh-1", 7m, T),
            }),
        };
        foreach (var envelope in sends)
        {
            var r = await c.SendAsync(envelope);
            Assert.True((int)r.Status < 300, $"{r.Status}: {r.Body.GetRawText()}");
            Assert.All(r.Items, i => Assert.NotEqual("rejected", i.Status));
        }
    }

    private Task<JsonElement> AskAsync(string tool, object? args = null) => f.AskAsync(f.EmptyKey, tool, args);

    private static List<JsonElement> Rows(JsonElement body, string name) => body.GetProperty("data").GetProperty(name).EnumerateArray().ToList();

    private static string Name(JsonElement row, string property = "name") => row.GetProperty(property).GetProperty("untrusted_text").GetString()!;

    private static decimal? Amount(JsonElement money)
    {
        if (money.ValueKind == JsonValueKind.Null) return null;
        var a = money.GetProperty("amount");
        return a.ValueKind == JsonValueKind.String ? decimal.Parse(a.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : a.GetDecimal();
    }

    [Fact]
    public async Task Stock_finds_items_by_name_in_any_spelling_and_by_barcode_with_price_and_value()
    {
        RequirePostgres();
        await SendCatalogAsync();

        var latin = Rows(await AskAsync("get_1c_stock", new { search = "paratsetamol" }), "stock");
        Assert.Equal(2, latin.Count);
        Assert.All(latin, r => Assert.Equal("Парацетамол 500 мг", Name(r)));
        var chilanzar = latin.Single(r => Name(r, "warehouse") == "Аптека Чиланзар");
        Assert.Equal(10m, chilanzar.GetProperty("quantity").GetDecimal());
        Assert.Equal(12000m, Amount(chilanzar.GetProperty("price")));
        Assert.Equal(120000m, Amount(chilanzar.GetProperty("value")));
        Assert.Null(Amount(latin.Single(r => Name(r, "warehouse") == "Аптека Юнусабад").GetProperty("value")));

        var byBarcode = Rows(await AskAsync("get_1c_stock", new { search = OneCSampleClient.Barcode(1) }), "stock");
        Assert.Equal(2, byBarcode.Count);

        var body = await AskAsync("get_1c_stock", new { search = "ибупрофен", warehouse = "чиланзар" });
        var ibuprofen = Assert.Single(Rows(body, "stock"));
        Assert.Equal(2m, ibuprofen.GetProperty("available").GetDecimal());
        Assert.True(body.GetProperty("data").GetProperty("price_type").GetProperty("is_default").GetBoolean());
    }

    [Fact]
    public async Task Stock_filters_out_of_stock_negative_and_almost_gone()
    {
        RequirePostgres();
        await SendCatalogAsync();

        Assert.Equal(2, Rows(await AskAsync("get_1c_stock", new { state = "out_of_stock" }), "stock").Count);
        var negative = Assert.Single(Rows(await AskAsync("get_1c_stock", new { state = "negative" }), "stock"));
        Assert.Equal("Ибупрофен 200 мг", Name(negative));
        Assert.Equal(3, Rows(await AskAsync("get_1c_stock", new { max_quantity = 5 }), "stock").Count);
        Assert.Equal(2, Rows(await AskAsync("get_1c_stock", new { warehouse = "юнусабад" }), "stock").Count);
    }

    [Fact]
    public async Task Summary_counts_positions_and_values_stock_at_the_default_price_per_warehouse()
    {
        RequirePostgres();
        await SendCatalogAsync();

        var body = await AskAsync("get_1c_stock_summary");
        var data = body.GetProperty("data");
        Assert.Equal(3, data.GetProperty("active_items").GetInt64());
        Assert.Equal(2, data.GetProperty("price_types").GetInt64());
        Assert.Equal(195000m, Amount(Assert.Single(data.GetProperty("total_value").EnumerateArray())));

        var warehouses = Rows(body, "warehouses");
        var w1 = warehouses.Single(w => Name(w) == "Аптека Чиланзар");
        Assert.Equal((3, 3, 0, 0, 1), (w1.GetProperty("positions").GetInt64(), w1.GetProperty("in_stock").GetInt64(),
            w1.GetProperty("out_of_stock").GetInt64(), w1.GetProperty("negative").GetInt64(), w1.GetProperty("in_stock_without_price").GetInt64()));
        Assert.Equal(195000m, Amount(Assert.Single(w1.GetProperty("value").EnumerateArray())));
        var w2 = warehouses.Single(w => Name(w) == "Аптека Юнусабад");
        Assert.Equal((2, 0, 2, 1), (w2.GetProperty("positions").GetInt64(), w2.GetProperty("in_stock").GetInt64(),
            w2.GetProperty("out_of_stock").GetInt64(), w2.GetProperty("negative").GetInt64()));
        Assert.Empty(w2.GetProperty("value").EnumerateArray());
        Assert.Contains(body.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("no price of the default price type"));
    }

    [Fact]
    public async Task Prices_list_every_price_type_filter_by_type_and_find_items_without_a_price()
    {
        RequirePostgres();
        await SendCatalogAsync();

        static List<JsonElement> Items(JsonElement body) => body.GetProperty("data").EnumerateArray().ToList();
        static List<(string Type, decimal Price, bool IsDefault)> Prices(JsonElement item) => item.GetProperty("prices").EnumerateArray()
            .Select(p => (Name(p, "price_type"), (decimal)Amount(p.GetProperty("price"))!, p.GetProperty("is_default").GetBoolean())).ToList();

        var paracetamol = Assert.Single(Items(await AskAsync("get_1c_prices", new { search = "парацетамол" })));
        Assert.Equal([("Розничная", 12000m, true), ("Оптовая", 10000m, false)], Prices(paracetamol));

        var wholesale = Items(await AskAsync("get_1c_prices", new { price_type = "опт" }));
        Assert.Equal(["Витамин C", "Парацетамол 500 мг"], wholesale.Select(i => Name(i)).Order(StringComparer.Ordinal));
        Assert.All(wholesale, i => Assert.All(Prices(i), p => Assert.Equal("Оптовая", p.Type)));

        var noRetail = Assert.Single(Items(await AskAsync("get_1c_prices", new { price_type = "розн", without_price = true })));
        Assert.Equal("Витамин C", Name(noRetail));
    }

    [Fact]
    public async Task An_organization_without_a_catalog_gets_empty_answers_with_a_note()
    {
        RequirePostgres();
        var body = await f.AskAsync(f.GoldenKey, "get_1c_stock_summary");
        Assert.Empty(body.GetProperty("data").GetProperty("warehouses").EnumerateArray());
        Assert.Contains(body.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("no stock yet"));
    }
}
