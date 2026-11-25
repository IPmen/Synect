using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Synect.Application.Accounting1C;
using Synect.Mcp.Responses;
using static Synect.Mcp.Tools.ToolText;

namespace Synect.Mcp.Tools;

internal static class CatalogText
{
    public const string About =
        " Catalog data is what 1C sent to Synect: items (номенклатура), warehouses, price types, CURRENT prices and CURRENT stock (see the 1C" +
        " source and its 'catalog' data group in data_freshness: the stock is as fresh as the last catalog package). Nothing is read from 1C" +
        " itself. Quantities are in each item's own unit (шт, кг, уп): never add quantities of different items or units together. Stock value" +
        " is quantity × the item's price of the default price type (the one 1C marks as default), an estimate at that price, not the cost" +
        " price; items without that price have no value. There are no sales, receipts or turnover here: only what is in stock now and the" +
        " current price.";

    public const string Search =
        "Item name fragment in any spelling (Cyrillic or Latin, e.g. «парацетамол», «paratsetamol»), or the exact code, article or barcode.";
}

/// <summary>
/// Questions about the 1C catalog: current stock per warehouse, a stock summary per warehouse with value at the default price
/// type, and current prices. Read only; one application query per tool.
/// </summary>
[McpServerToolType]
public sealed class CatalogTools(ToolRunner runner, Presenter p, CatalogQueries queries)
{
    private const ToolGroup Group = ToolGroup.Accounting;

    public static readonly IReadOnlyDictionary<string, CatalogStockState> States = new Dictionary<string, CatalogStockState>
    {
        ["all"] = CatalogStockState.All,
        ["in_stock"] = CatalogStockState.InStock,
        ["out_of_stock"] = CatalogStockState.OutOfStock,
        ["negative"] = CatalogStockState.Negative,
    };

    private object? PriceType(CatalogPriceTypeInfo? t) =>
        t is null ? null : new { Ref = p.Code(t.Ref), Name = p.Untrusted(t.Name), t.IsDefault };

    [McpServerTool(Name = "get_1c_stock", Title = "Stock by 1C", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Current stock (остатки товаров) by 1C, one row per item and warehouse, by item name: quantity, reserved, unit, warehouse, the item's " +
                 "price of the default price type and the stock value at that price. Filters: search (name in any spelling, code, article, barcode), " +
                 "warehouse (name fragment), state (all, in_stock, out_of_stock = quantity ≤ 0, negative = below zero), max_quantity (e.g. 5 for «almost " +
                 "gone»). Examples: «Сколько осталось парацетамола?», «Что закончилось на складе аптеки на Чиланзаре?», «Какие товары заканчиваются " +
                 "(осталось меньше 5)?», «Где товар ушёл в минус?», «Есть ли в наличии артикул A-15?», «Qaysi dorilar tugab qoldi?»." +
                 CatalogText.About + Common)]
    public Task<CallToolResult> GetStock(
        [Description(CatalogText.Search)] string? search = null,
        [Description("Warehouse (склад, магазин, аптека) name fragment or exact warehouse ref; default all warehouses.")] string? warehouse = null,
        [Description("all (default), in_stock (quantity > 0), out_of_stock (quantity ≤ 0) or negative (quantity < 0).")] string state = "all",
        [Description("Only rows with quantity at or below this number, e.g. 5.")] double? max_quantity = null,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_1c_stock", Group, async (a, ct) =>
        {
            var filter = new CatalogStockFilter(a.OptionalText("search", search), a.OptionalText("warehouse", warehouse), a.Enum("state", state, States),
                max_quantity is { } m ? (decimal)m : null);
            var r = await queries.GetStockAsync(filter, a.Page(limit, cursor), ct);
            var items = r.Data.Rows.Items.Select(s => (object)new
            {
                ItemRef = p.Code(s.ItemRef),
                Code = p.Code(s.Code),
                Article = p.Code(s.Article),
                Name = p.Untrusted(s.Name),
                Category = p.Untrusted(s.Category),
                Unit = p.Code(s.Unit),
                WarehouseRef = p.Code(s.WarehouseRef),
                Warehouse = p.Untrusted(s.Warehouse),
                s.Quantity,
                s.Reserved,
                Available = s.Reserved is { } res ? s.Quantity - res : (decimal?)null,
                Price = s.PriceMinor is { } pr ? p.Money(pr, s.PriceCurrency ?? "UZS") : null,
                Value = s.ValueMinor is { } v ? p.Money(v, s.PriceCurrency ?? "UZS") : null,
                StockAsOf = s.AsOf,
            }).ToList();
            var notes = new List<string>();
            if (r.Data.PriceType is null) notes.Add("1C has sent no price types: stock has no prices and no value.");
            if (r.Data.Rows.Items.Count == 0) notes.Add("No stock rows match (or 1C has sent no stock yet: see the 'catalog' data group in data_freshness).");
            return new ToolOutput(new { PriceType = PriceType(r.Data.PriceType), Stock = items }, items.Count, r.Freshness, r.Data.Rows.NextCursor,
                Notes: notes.Count > 0 ? notes : null);
        }, cancellationToken);

    [McpServerTool(Name = "get_1c_stock_summary", Title = "Stock summary per warehouse by 1C", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stock summary by 1C per warehouse (склад, магазин, аптека): positions with a stock row, how many are in stock, out of stock (≤ 0) or " +
                 "negative, how many in-stock positions have no price, and the stock value at the default price type (per currency), plus totals over " +
                 "all warehouses. Examples: «Сколько товара на складах в деньгах?», «В какой аптеке больше всего позиций закончилось?», «Сравни " +
                 "магазины по остаткам», «Сколько у нас всего наименований?», «Omborlarda qancha tovar bor?»." + CatalogText.About + Common)]
    public Task<CallToolResult> GetStockSummary(CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_1c_stock_summary", Group, async (a, ct) =>
        {
            var r = await queries.GetStockSummaryAsync(ct);
            var notes = new List<string>();
            if (r.Data.Warehouses.Count == 0) notes.Add("1C has sent no stock yet (see the 'catalog' data group in data_freshness).");
            if (r.Data.PriceType is null) notes.Add("1C has sent no price types: stock has no value.");
            if (r.Data.Warehouses.Any(w => w.InStockWithoutPrice > 0))
                notes.Add("Some in-stock positions have no price of the default price type: their value is not included.");
            return new ToolOutput(new
            {
                PriceType = PriceType(r.Data.PriceType),
                ActiveItems = r.Data.Items,
                r.Data.PriceTypes,
                TotalValue = r.Data.TotalValue.Select(v => p.Money(v.AmountMinor, v.Currency)).ToList(),
                Warehouses = r.Data.Warehouses.Select(w => new
                {
                    WarehouseRef = p.Code(w.WarehouseRef),
                    Name = p.Untrusted(w.Name),
                    w.Positions,
                    w.InStock,
                    w.OutOfStock,
                    w.Negative,
                    w.InStockWithoutPrice,
                    Value = w.Value.Select(v => p.Money(v.AmountMinor, v.Currency)).ToList(),
                    LatestStockAsOf = w.LatestAsOf,
                }).ToList(),
            }, r.Data.Warehouses.Count, r.Freshness, Notes: notes.Count > 0 ? notes : null);
        }, cancellationToken);

    [McpServerTool(Name = "get_1c_prices", Title = "Prices by 1C", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Current prices of items by 1C (цены номенклатуры), by item name, with every price type (розничная, оптовая …; the default type first). " +
                 "Filters: search (name in any spelling, code, article, barcode), price_type (name fragment: only that type), without_price (items " +
                 "that have no price at all, or none of that type). Examples: «Сколько стоит ибупрофен?», «Розничная цена на молоко», «Какие оптовые " +
                 "цены на воду?», «У каких товаров нет розничной цены?», «Narxi qancha?»." + CatalogText.About + Common)]
    public Task<CallToolResult> GetPrices(
        [Description(CatalogText.Search)] string? search = null,
        [Description("Price type (вид цен) name fragment or exact ref, e.g. «розн», «опт»; default all types.")] string? price_type = null,
        [Description("true: only items without a price (of price_type when given).")] bool without_price = false,
        [Description(Limit)] int? limit = null,
        [Description(CursorText)] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_1c_prices", Group, async (a, ct) =>
        {
            var r = await queries.GetPricesAsync(a.OptionalText("search", search), a.OptionalText("price_type", price_type), without_price,
                a.Page(limit, cursor), ct);
            var notes = r.Data.Items.Count == 0
                ? new[] { "No items match (or 1C has sent no catalog yet: see the 'catalog' data group in data_freshness)." }
                : null;
            return ToolOutputs.Page(r, i => new
            {
                ItemRef = p.Code(i.ItemRef),
                Code = p.Code(i.Code),
                Article = p.Code(i.Article),
                Name = p.Untrusted(i.Name),
                Category = p.Untrusted(i.Category),
                Unit = p.Code(i.Unit),
                Prices = i.Prices.Select(x => new
                {
                    PriceTypeRef = p.Code(x.PriceTypeRef),
                    PriceType = p.Untrusted(x.PriceType),
                    x.IsDefault,
                    Price = p.Money(x.PriceMinor, x.Currency),
                    x.EffectiveFrom,
                }).ToList(),
            }, notes);
        }, cancellationToken);
}
