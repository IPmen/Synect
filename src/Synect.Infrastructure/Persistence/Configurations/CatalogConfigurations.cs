using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Synect.Domain.Accounting;
using Synect.Domain.Catalog;
using Synect.Domain.Sync;

namespace Synect.Infrastructure.Persistence.Configurations;

/// <summary>
/// Catalog of 1C for the Mailo bot (stage 8a, catalog part). Same rules as the accounting records: no foreign keys between
/// 1C records (a price may arrive before its item), tenant_id first in every index. The search indexes (GIN on barcodes,
/// pg_trgm on search_name) are created by the migration in SQL.
/// </summary>
internal sealed class CatalogItemConfiguration : IEntityTypeConfiguration<CatalogItem>
{
    public void Configure(EntityTypeBuilder<CatalogItem> b)
    {
        b.ConfigureRefRecord("catalog_items");
        b.Property(e => e.ItemType).HasConversion<string>().HasMaxLength(16);
        b.Property(e => e.Code).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.Article).HasMaxLength(AccountingRecordConfiguration.NumberLength);
        b.Property(e => e.SearchName).HasMaxLength(4000);
        b.Property(e => e.CategoryRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.Unit).HasMaxLength(AccountingRecordConfiguration.ShortTextLength);
        // text[] on PostgreSQL by convention (a JSON text column on the SQLite test host); the writer always fills it.
        b.Property(e => e.VatRate).HasColumnType("numeric(9,4)");
        b.Property(e => e.IsArchived).HasDefaultValue(false);
        b.HasIndex(e => new { e.TenantId, e.Code });
        b.HasIndex(e => new { e.TenantId, e.Article });
        b.HasIndex(e => new { e.TenantId, e.CategoryRef });
    }
}

internal sealed class CatalogCategoryConfiguration : IEntityTypeConfiguration<CatalogCategory>
{
    public void Configure(EntityTypeBuilder<CatalogCategory> b)
    {
        b.ConfigureRefRecord("catalog_categories");
        b.Property(e => e.ParentRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.HasIndex(e => new { e.TenantId, e.ParentRef });
    }
}

internal sealed class CatalogPriceTypeConfiguration : IEntityTypeConfiguration<CatalogPriceType>
{
    public void Configure(EntityTypeBuilder<CatalogPriceType> b)
    {
        b.ConfigureRefRecord("catalog_price_types");
        b.Property(e => e.Currency).HasMaxLength(AccountingRecordConfiguration.CurrencyLength);
        b.Property(e => e.IsDefault).HasDefaultValue(false);
    }
}

internal sealed class CatalogWarehouseConfiguration : IEntityTypeConfiguration<CatalogWarehouse>
{
    public void Configure(EntityTypeBuilder<CatalogWarehouse> b) => b.ConfigureRefRecord("catalog_warehouses");
}

internal sealed class CatalogPriceConfiguration : IEntityTypeConfiguration<CatalogPrice>
{
    public void Configure(EntityTypeBuilder<CatalogPrice> b)
    {
        b.ConfigureRecord("catalog_prices");
        b.Property(e => e.ItemRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.PriceTypeRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.Currency).HasMaxLength(AccountingRecordConfiguration.CurrencyLength);
        // Current state: one price per item and price type; also the lookup of an item's prices (item_ref leads).
        b.HasIndex(e => new { e.TenantId, e.ItemRef, e.PriceTypeRef }).IsUnique();
    }
}

internal sealed class CatalogStockConfiguration : IEntityTypeConfiguration<CatalogStock>
{
    public void Configure(EntityTypeBuilder<CatalogStock> b)
    {
        b.ConfigureRecord("catalog_stock");
        b.Property(e => e.ItemRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.WarehouseRef).HasMaxLength(AccountingRecordConfiguration.RefLength);
        b.Property(e => e.Quantity).HasColumnType("numeric(28,6)");
        b.Property(e => e.Reserved).HasColumnType("numeric(28,6)");
        // Current state: one row per item and warehouse; also the lookup of an item's stock (item_ref leads).
        b.HasIndex(e => new { e.TenantId, e.ItemRef, e.WarehouseRef }).IsUnique();
    }
}

internal sealed class IngestFreshnessConfiguration : IEntityTypeConfiguration<IngestFreshness>
{
    public void Configure(EntityTypeBuilder<IngestFreshness> b)
    {
        b.ToTable("ingest_freshness");
        b.HasKey(e => new { e.TenantId, e.ConnectionId, e.DataGroup });
        b.Property(e => e.DataGroup).HasConversion<string>().HasMaxLength(16);
        b.HasOne<Connection>().WithMany().HasForeignKey(e => new { e.TenantId, e.ConnectionId }).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class IngestRemovedRowConfiguration : IEntityTypeConfiguration<IngestRemovedRow>
{
    public void Configure(EntityTypeBuilder<IngestRemovedRow> b)
    {
        b.ToTable("ingest_removed_rows");
        b.HasKey(e => new { e.TenantId, e.Id });
        b.Property(e => e.FullSyncId).HasMaxLength(IngestBatch.MaxIdLength);
        b.Property(e => e.Entity).HasMaxLength(32);
        b.Property(e => e.ItemKey).HasMaxLength(400);
        b.HasOne<IngestBatch>().WithMany().HasForeignKey(e => new { e.TenantId, e.IngestBatchId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.TenantId, e.IngestBatchId });
        b.HasIndex(e => new { e.TenantId, e.Entity, e.ItemKey });
    }
}
