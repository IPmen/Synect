using Synect.Domain.Accounting;
using Synect.Domain.Common;

namespace Synect.Domain.Catalog;

/// <summary>Kind of a catalog item of 1C: goods (товар) or a service (услуга).</summary>
public enum CatalogItemType
{
    Goods = 1,
    Service = 2,
}

/// <summary>
/// catalog_items (Номенклатура) for the Mailo Telegram bot (stage 8a, catalog part). Like every 1C record: identified by
/// (organization, ref), never deleted (is_deleted), unknown fields in extra. No foreign key to the category: an item may
/// arrive before its category; the link is resolved by ref.
/// </summary>
public class CatalogItem : AccountingRefRecord
{
    /// <summary>Goods or Service; NULL when the source did not say or sent a value Synect does not know (kept in extra).</summary>
    public CatalogItemType? ItemType { get; set; }

    public string? Code { get; set; }
    public string? Article { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? FullName { get; set; }

    /// <summary>
    /// Search key of the name: transliterated to Latin, lower case, signs removed (CounterpartyNameNormalizer.NormalizeText,
    /// without removing legal forms). Indexed with pg_trgm. Computed by Synect, not sent by the source.
    /// </summary>
    public string SearchName { get; set; } = string.Empty;

    public string? CategoryRef { get; set; }

    /// <summary>Unit of measure as the source names it (шт, кг, услуга).</summary>
    public string? Unit { get; set; }

    /// <summary>Barcodes (strings of digits); an item may have several.</summary>
    public string[] Barcodes { get; set; } = [];

    public decimal? VatRate { get; set; }
    public bool IsArchived { get; set; }
}

/// <summary>catalog_categories (Группы номенклатуры / Виды номенклатуры): a tree by parent_ref.</summary>
public class CatalogCategory : AccountingRefRecord
{
    public string? ParentRef { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>catalog_price_types (Виды цен).</summary>
public class CatalogPriceType : AccountingRefRecord
{
    public string Name { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

/// <summary>catalog_warehouses (Склады).</summary>
public class CatalogWarehouse : AccountingRefRecord
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// catalog_prices: the CURRENT price of an item for a price type, one row per (organization, item_ref, price_type_ref).
/// A completed full export removes rows it did not contain (logged in ingest_removed_rows); a delta deletion only marks.
/// </summary>
public class CatalogPrice : AccountingRecordBase
{
    public string ItemRef { get; set; } = string.Empty;
    public string PriceTypeRef { get; set; } = string.Empty;
    public long PriceMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateOnly? EffectiveFrom { get; set; }
}

/// <summary>
/// catalog_stock: the CURRENT stock of an item in a warehouse, one row per (organization, item_ref, warehouse_ref).
/// Quantities are numeric (fractions of kg, negative stock is possible in 1C). A completed full export removes rows it did
/// not contain (logged in ingest_removed_rows).
/// </summary>
public class CatalogStock : AccountingRecordBase
{
    public string ItemRef { get; set; } = string.Empty;
    public string WarehouseRef { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal? Reserved { get; set; }

    /// <summary>Time of the stock by the source (the item's as_of, or the export time of its package).</summary>
    public DateTimeOffset AsOf { get; set; }
}
