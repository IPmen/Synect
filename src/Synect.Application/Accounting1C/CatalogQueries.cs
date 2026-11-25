using System.Globalization;
using Synect.Application.Analytics;
using Synect.Domain.Counterparties;

namespace Synect.Application.Accounting1C;

/// <summary>Which stock rows a list shows by quantity.</summary>
public enum CatalogStockState
{
    All,

    /// <summary>quantity &gt; 0.</summary>
    InStock,

    /// <summary>quantity &lt;= 0 (ran out; negative stock included).</summary>
    OutOfStock,

    /// <summary>quantity &lt; 0 (1C allows selling below zero).</summary>
    Negative,
}

/// <param name="Search">Name fragment in any spelling (transliterated like counterparty names), or exact code, article or barcode.</param>
/// <param name="Warehouse">Warehouse name fragment or exact warehouse ref.</param>
/// <param name="MaxQuantity">Only rows with quantity at or below this value (e.g. "almost gone").</param>
public sealed record CatalogStockFilter(string? Search = null, string? Warehouse = null, CatalogStockState State = CatalogStockState.All,
    decimal? MaxQuantity = null);

/// <summary>Current stock of one item in one warehouse, with the item's price of the default price type (null when it has none).</summary>
public sealed record CatalogStockRow(
    string ItemRef, string? Code, string? Article, string Name, string? Unit, string? Category, string WarehouseRef, string? Warehouse,
    decimal Quantity, decimal? Reserved, DateTimeOffset AsOf, long? PriceMinor, string? PriceCurrency)
{
    /// <summary>Quantity × price in minor units, for positive stock with a price; null otherwise.</summary>
    public long? ValueMinor => PriceMinor is { } p && Quantity > 0 ? (long)Math.Round(Quantity * p, MidpointRounding.AwayFromZero) : null;
}

/// <summary>The price type used for stock values: the one 1C marks as default, otherwise the first by name.</summary>
public sealed record CatalogPriceTypeInfo(string Ref, string Name, string Currency, bool IsDefault);

public sealed record CatalogStockPage(CatalogPriceTypeInfo? PriceType, Page<CatalogStockRow> Rows);

public sealed record CatalogMoney(string Currency, long AmountMinor);

/// <summary>One warehouse: positions (items with a stock row), how many are in stock, out of stock or negative, stock value.</summary>
public sealed record CatalogWarehouseSummary(
    string WarehouseRef, string? Name, long Positions, long InStock, long OutOfStock, long Negative, long InStockWithoutPrice,
    IReadOnlyList<CatalogMoney> Value, DateTimeOffset? LatestAsOf);

public sealed record CatalogStockSummary(
    CatalogPriceTypeInfo? PriceType, IReadOnlyList<CatalogWarehouseSummary> Warehouses, IReadOnlyList<CatalogMoney> TotalValue,
    long Items, long PriceTypes);

public sealed record CatalogPriceValue(string PriceTypeRef, string? PriceType, bool IsDefault, long PriceMinor, string Currency, DateOnly? EffectiveFrom);

public sealed record CatalogPriceItem(
    string ItemRef, string? Code, string? Article, string Name, string? Unit, string? Category, IReadOnlyList<CatalogPriceValue> Prices);

/// <summary>
/// Read-only questions about the 1C catalog (Номенклатура, склады, виды цен, текущие цены и остатки) received through the 1C
/// receiver: current stock per warehouse, a stock summary with value at the default price type, and current prices.
/// Deleted rows and archived items are left out. Quantities are numbers in the item's unit; units are never added together.
/// Names are third-party text (callers clean and wrap them).
/// </summary>
public sealed class CatalogQueries(AnalyticsContext ctx)
{
    /// <summary>Current stock rows, by item name, then warehouse.</summary>
    public Task<AnalyticsResult<CatalogStockPage>> GetStockAsync(CatalogStockFilter filter, PageRequest? page, CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var priceType = await DefaultPriceTypeAsync(token);
            var limit = ctx.Limit(page);
            var sql = new Sql().Raw("""
                select s.item_ref, i.code, i.article, coalesce(i.name, s.item_ref) as name, i.unit, c.name as category,
                       s.warehouse_ref, w.name as warehouse, s.quantity, s.reserved, s.as_of, p.price_minor, p.currency as price_currency
                from catalog_stock s
                left join catalog_items i on i.tenant_id = s.tenant_id and i.ref = s.item_ref
                left join catalog_categories c on c.tenant_id = i.tenant_id and c.ref = i.category_ref and not c.is_deleted
                left join catalog_warehouses w on w.tenant_id = s.tenant_id and w.ref = s.warehouse_ref
                left join catalog_prices p on p.tenant_id = s.tenant_id and p.item_ref = s.item_ref and not p.is_deleted and p.price_type_ref =
                """).P(priceType?.Ref ?? "").Raw(" where s.tenant_id = ").P(ctx.TenantId)
                .Raw(" and not s.is_deleted and (i.ref is null or (not i.is_deleted and not i.is_archived))");
            ItemSearch(sql, filter.Search);
            if (filter.Warehouse is { Length: > 0 } wh)
                sql.Raw(" and (s.warehouse_ref = ").P(wh).Raw(" or strpos(lower(w.name), ").P(wh.ToLowerInvariant()).Raw(") > 0)");
            switch (filter.State)
            {
                case CatalogStockState.InStock: sql.Raw(" and s.quantity > 0"); break;
                case CatalogStockState.OutOfStock: sql.Raw(" and s.quantity <= 0"); break;
                case CatalogStockState.Negative: sql.Raw(" and s.quantity < 0"); break;
            }
            if (filter.MaxQuantity is { } max) sql.Raw(" and s.quantity <= ").P(max);
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 3);
                sql.Raw(" and (coalesce(i.name, s.item_ref), s.item_ref, s.warehouse_ref) > (").P(c[0]!).Raw(", ").P(c[1]!).Raw(", ").P(c[2]!).Raw(")");
            }
            sql.Raw(" order by coalesce(i.name, s.item_ref), s.item_ref, s.warehouse_ref limit ").P(limit + 1);
            var rows = await ctx.QueryAsync<StockRow>(sql, token);
            var result = AnalyticsContext.ToPage(rows, limit,
                r => new CatalogStockRow(r.ItemRef, r.Code, r.Article, r.Name, r.Unit, r.Category, r.WarehouseRef, r.Warehouse,
                    r.Quantity, r.Reserved, r.AsOf, r.PriceMinor, r.PriceCurrency),
                r => Cursor.Encode(r.Name, r.ItemRef, r.WarehouseRef));
            return new CatalogStockPage(priceType, result);
        }, ct);

    /// <summary>Per warehouse: positions, in stock, out of stock, negative, stock value at the default price type (per currency).</summary>
    public Task<AnalyticsResult<CatalogStockSummary>> GetStockSummaryAsync(CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var priceType = await DefaultPriceTypeAsync(token);
            const string From = """
                from catalog_stock s
                left join catalog_items i on i.tenant_id = s.tenant_id and i.ref = s.item_ref
                left join catalog_warehouses w on w.tenant_id = s.tenant_id and w.ref = s.warehouse_ref
                left join catalog_prices p on p.tenant_id = s.tenant_id and p.item_ref = s.item_ref and not p.is_deleted and p.price_type_ref =
                """;
            var counts = new Sql().Raw("""
                select s.warehouse_ref, max(w.name) as warehouse, count(*) as positions,
                       count(*) filter (where s.quantity > 0) as in_stock, count(*) filter (where s.quantity <= 0) as out_of_stock,
                       count(*) filter (where s.quantity < 0) as negative,
                       count(*) filter (where s.quantity > 0 and p.price_minor is null) as in_stock_without_price, max(s.as_of) as latest_as_of
                """ + "\n" + From).P(priceType?.Ref ?? "");
            ActiveStock(counts);
            counts.Raw(" group by s.warehouse_ref");
            var values = new Sql().Raw("select s.warehouse_ref, p.currency, round(sum(s.quantity * p.price_minor))::bigint as value\n" + From)
                .P(priceType?.Ref ?? "");
            ActiveStock(values);
            values.Raw(" and s.quantity > 0 and p.price_minor is not null group by s.warehouse_ref, p.currency");

            var countRows = await ctx.QueryAsync<SummaryRow>(counts, token);
            var valueRows = await ctx.QueryAsync<ValueRow>(values, token);
            var byWarehouse = valueRows.GroupBy(v => v.WarehouseRef, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<CatalogMoney>)g.OrderBy(v => v.Currency, StringComparer.Ordinal)
                    .Select(v => new CatalogMoney(v.Currency, v.Value)).ToList(), StringComparer.Ordinal);
            var warehouses = countRows
                .Select(r => new CatalogWarehouseSummary(r.WarehouseRef, r.Warehouse, r.Positions, r.InStock, r.OutOfStock, r.Negative,
                    r.InStockWithoutPrice, byWarehouse.GetValueOrDefault(r.WarehouseRef) ?? [], r.LatestAsOf))
                .OrderBy(w => w.Name ?? w.WarehouseRef, StringComparer.CurrentCultureIgnoreCase).ThenBy(w => w.WarehouseRef, StringComparer.Ordinal)
                .ToList();
            var total = valueRows.GroupBy(v => v.Currency, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new CatalogMoney(g.Key, g.Sum(v => v.Value))).ToList();
            var items = (await ctx.QueryAsync<CountRow>(new Sql().Raw("select count(*) as value from catalog_items where tenant_id = ")
                .P(ctx.TenantId).Raw(" and not is_deleted and not is_archived"), token)).Single().Value;
            var priceTypes = (await ctx.QueryAsync<CountRow>(new Sql().Raw("select count(*) as value from catalog_price_types where tenant_id = ")
                .P(ctx.TenantId).Raw(" and not is_deleted"), token)).Single().Value;
            return new CatalogStockSummary(priceType, warehouses, total, items, priceTypes);
        }, ct);

    /// <summary>Items with their current prices of every price type, by name.</summary>
    /// <param name="priceType">Price type name fragment or exact ref: only that type's prices (and only items that have it).</param>
    /// <param name="withoutPrice">Only items with no price at all (of <paramref name="priceType"/> when given).</param>
    public Task<AnalyticsResult<Page<CatalogPriceItem>>> GetPricesAsync(string? search, string? priceType, bool withoutPrice, PageRequest? page,
        CancellationToken ct) =>
        ctx.RunAsync(async token =>
        {
            var limit = ctx.Limit(page);
            var typeFilter = new Sql();
            if (priceType is { Length: > 0 } pt)
                typeFilter.Raw(" and (p.price_type_ref = ").P(pt).Raw(" or strpos(lower(t.name), ").P(pt.ToLowerInvariant()).Raw(") > 0)");

            var sql = new Sql().Raw("""
                select i.ref as item_ref, i.code, i.article, i.name, i.unit, c.name as category
                from catalog_items i
                left join catalog_categories c on c.tenant_id = i.tenant_id and c.ref = i.category_ref and not c.is_deleted
                where i.tenant_id =
                """).P(ctx.TenantId).Raw(" and not i.is_deleted and not i.is_archived");
            ItemSearch(sql, search);
            sql.Raw(withoutPrice ? " and not exists (" : priceType is { Length: > 0 } ? " and exists (" : "");
            if (withoutPrice || priceType is { Length: > 0 })
            {
                sql.Raw("select 1 from catalog_prices p left join catalog_price_types t on t.tenant_id = p.tenant_id and t.ref = p.price_type_ref " +
                        "where p.tenant_id = i.tenant_id and p.item_ref = i.ref and not p.is_deleted");
                sql.Append(typeFilter);
                sql.Raw(")");
            }
            if (page?.Cursor is { } cursor)
            {
                var c = Cursor.Decode(cursor, 2);
                sql.Raw(" and (i.name, i.ref) > (").P(c[0]!).Raw(", ").P(c[1]!).Raw(")");
            }
            sql.Raw(" order by i.name, i.ref limit ").P(limit + 1);
            var items = await ctx.QueryAsync<PriceItemRow>(sql, token);

            var refs = items.Take(limit).Select(r => r.ItemRef).ToArray();
            var prices = new List<PriceRow>();
            if (refs.Length > 0 && !withoutPrice)
            {
                var ps = new Sql().Raw("""
                    select p.item_ref, p.price_type_ref, t.name as price_type, coalesce(t.is_default, false) as is_default,
                           p.price_minor, p.currency, p.effective_from
                    from catalog_prices p
                    left join catalog_price_types t on t.tenant_id = p.tenant_id and t.ref = p.price_type_ref and not t.is_deleted
                    where p.tenant_id =
                    """).P(ctx.TenantId).Raw(" and not p.is_deleted and p.item_ref = any(").P(refs).Raw(")");
                ps.Append(typeFilter);
                ps.Raw(" order by p.item_ref, coalesce(t.is_default, false) desc, t.name, p.price_type_ref");
                prices = await ctx.QueryAsync<PriceRow>(ps, token);
            }
            var byItem = prices.GroupBy(p => p.ItemRef, StringComparer.Ordinal).ToDictionary(g => g.Key,
                g => (IReadOnlyList<CatalogPriceValue>)g.Select(p => new CatalogPriceValue(p.PriceTypeRef, p.PriceType, p.IsDefault, p.PriceMinor,
                    p.Currency, p.EffectiveFrom)).ToList(), StringComparer.Ordinal);
            return AnalyticsContext.ToPage(items, limit,
                r => new CatalogPriceItem(r.ItemRef, r.Code, r.Article, r.Name, r.Unit, r.Category, byItem.GetValueOrDefault(r.ItemRef) ?? []),
                r => Cursor.Encode(r.Name, r.ItemRef));
        }, ct);

    /// <summary>The default price type of the organization (null when 1C sent none).</summary>
    private async Task<CatalogPriceTypeInfo?> DefaultPriceTypeAsync(CancellationToken ct)
    {
        var sql = new Sql().Raw("select ref, name, currency, is_default from catalog_price_types where tenant_id = ").P(ctx.TenantId)
            .Raw(" and not is_deleted order by is_default desc, name, ref limit 1");
        return (await ctx.QueryAsync<PriceTypeRow>(sql, ct)).Select(r => new CatalogPriceTypeInfo(r.Ref, r.Name, r.Currency, r.IsDefault)).SingleOrDefault();
    }

    /// <summary>Name fragment in any spelling (the same key as catalog_items.search_name), or exact code, article or barcode.</summary>
    private static void ItemSearch(Sql sql, string? search)
    {
        if (search is not { Length: > 0 } text) return;
        var key = CounterpartyNameNormalizer.NormalizeText(text);
        sql.Raw(" and (i.code = ").P(text).Raw(" or i.article = ").P(text).Raw(" or ").P(text).Raw(" = any(i.barcodes)");
        if (key.Length > 0) sql.Raw(" or strpos(i.search_name, ").P(key).Raw(") > 0");
        sql.Raw(")");
    }

    private void ActiveStock(Sql sql) =>
        sql.Raw(" where s.tenant_id = ").P(ctx.TenantId).Raw(" and not s.is_deleted and (i.ref is null or (not i.is_deleted and not i.is_archived))");

    private sealed class StockRow
    {
        public string ItemRef { get; set; } = "";
        public string? Code { get; set; }
        public string? Article { get; set; }
        public string Name { get; set; } = "";
        public string? Unit { get; set; }
        public string? Category { get; set; }
        public string WarehouseRef { get; set; } = "";
        public string? Warehouse { get; set; }
        public decimal Quantity { get; set; }
        public decimal? Reserved { get; set; }
        public DateTimeOffset AsOf { get; set; }
        public long? PriceMinor { get; set; }
        public string? PriceCurrency { get; set; }
    }

    private sealed class SummaryRow
    {
        public string WarehouseRef { get; set; } = "";
        public string? Warehouse { get; set; }
        public long Positions { get; set; }
        public long InStock { get; set; }
        public long OutOfStock { get; set; }
        public long Negative { get; set; }
        public long InStockWithoutPrice { get; set; }
        public DateTimeOffset? LatestAsOf { get; set; }
    }

    private sealed class ValueRow
    {
        public string WarehouseRef { get; set; } = "";
        public string Currency { get; set; } = "";
        public long Value { get; set; }
    }

    private sealed class CountRow
    {
        public long Value { get; set; }
    }

    private sealed class PriceTypeRow
    {
        public string Ref { get; set; } = "";
        public string Name { get; set; } = "";
        public string Currency { get; set; } = "";
        public bool IsDefault { get; set; }
    }

    private sealed class PriceItemRow
    {
        public string ItemRef { get; set; } = "";
        public string? Code { get; set; }
        public string? Article { get; set; }
        public string Name { get; set; } = "";
        public string? Unit { get; set; }
        public string? Category { get; set; }
    }

    private sealed class PriceRow
    {
        public string ItemRef { get; set; } = "";
        public string PriceTypeRef { get; set; } = "";
        public string? PriceType { get; set; }
        public bool IsDefault { get; set; }
        public long PriceMinor { get; set; }
        public string Currency { get; set; } = "";
        public DateOnly? EffectiveFrom { get; set; }
    }
}
