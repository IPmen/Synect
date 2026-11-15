using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CatalogIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "removed_rows",
                table: "ingest_batches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "catalog_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_categories", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_catalog_categories_tenant_id_ref", x => new { x.tenant_id, x.@ref });
                    table.ForeignKey(
                        name: "fk_catalog_categories_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    article = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: true),
                    search_name = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    category_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    unit = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    barcodes = table.Column<string[]>(type: "text[]", nullable: false),
                    vat_rate = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_items", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_catalog_items_tenant_id_ref", x => new { x.tenant_id, x.@ref });
                    table.ForeignKey(
                        name: "fk_catalog_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_price_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_price_types", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_catalog_price_types_tenant_id_ref", x => new { x.tenant_id, x.@ref });
                    table.ForeignKey(
                        name: "fk_catalog_price_types_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_prices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    price_type_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    price_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_prices", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_catalog_prices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_stock",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    warehouse_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(28,6)", nullable: false),
                    reserved = table.Column<decimal>(type: "numeric(28,6)", nullable: true),
                    as_of = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_stock", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_catalog_stock_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "catalog_warehouses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_warehouses", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_catalog_warehouses_tenant_id_ref", x => new { x.tenant_id, x.@ref });
                    table.ForeignKey(
                        name: "fk_catalog_warehouses_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ingest_freshness",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_group = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    last_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ingest_freshness", x => new { x.tenant_id, x.connection_id, x.data_group });
                    table.ForeignKey(
                        name: "fk_ingest_freshness_connections_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ingest_removed_rows",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ingest_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_sync_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    item_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ingest_removed_rows", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_ingest_removed_rows_ingest_batches_tenant_id_ingest_batch_id",
                        columns: x => new { x.tenant_id, x.ingest_batch_id },
                        principalTable: "ingest_batches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_categories_tenant_id_parent_ref",
                table: "catalog_categories",
                columns: new[] { "tenant_id", "parent_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_items_tenant_id_article",
                table: "catalog_items",
                columns: new[] { "tenant_id", "article" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_items_tenant_id_category_ref",
                table: "catalog_items",
                columns: new[] { "tenant_id", "category_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_items_tenant_id_code",
                table: "catalog_items",
                columns: new[] { "tenant_id", "code" });

            migrationBuilder.CreateIndex(
                name: "ix_catalog_prices_tenant_id_item_ref_price_type_ref",
                table: "catalog_prices",
                columns: new[] { "tenant_id", "item_ref", "price_type_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_catalog_stock_tenant_id_item_ref_warehouse_ref",
                table: "catalog_stock",
                columns: new[] { "tenant_id", "item_ref", "warehouse_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ingest_removed_rows_tenant_id_entity_item_key",
                table: "ingest_removed_rows",
                columns: new[] { "tenant_id", "entity", "item_key" });

            migrationBuilder.CreateIndex(
                name: "ix_ingest_removed_rows_tenant_id_ingest_batch_id",
                table: "ingest_removed_rows",
                columns: new[] { "tenant_id", "ingest_batch_id" });

            ApplySecurity(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in RecordTables)
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS fk_{table}_ingest_batches;");
            migrationBuilder.DropTable(
                name: "catalog_categories");

            migrationBuilder.DropTable(
                name: "catalog_items");

            migrationBuilder.DropTable(
                name: "catalog_price_types");

            migrationBuilder.DropTable(
                name: "catalog_prices");

            migrationBuilder.DropTable(
                name: "catalog_stock");

            migrationBuilder.DropTable(
                name: "catalog_warehouses");

            migrationBuilder.DropTable(
                name: "ingest_freshness");

            migrationBuilder.DropTable(
                name: "ingest_removed_rows");

            migrationBuilder.Sql("ALTER TABLE ingest_batches DROP CONSTRAINT IF EXISTS ck_ingest_batches_removed_rows;");
            migrationBuilder.DropColumn(
                name: "removed_rows",
                table: "ingest_batches");
        }

        /// <summary>Tables of organization data created here: row-level security forced, like every tenant table.</summary>
        public static readonly string[] ForcedTables =
        [
            "catalog_items", "catalog_categories", "catalog_price_types", "catalog_prices", "catalog_warehouses", "catalog_stock",
            "ingest_freshness", "ingest_removed_rows",
        ];

        private static readonly string[] RecordTables =
        [
            "catalog_items", "catalog_categories", "catalog_price_types", "catalog_prices", "catalog_warehouses", "catalog_stock",
        ];

        private const string TenantPredicate = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

        private static void ApplySecurity(MigrationBuilder migrationBuilder)
        {
            // Records point at the package that last changed them (as in stage 8a: in SQL, so that no index is added for it).
            foreach (var table in RecordTables)
                migrationBuilder.Sql($"ALTER TABLE {table} ADD CONSTRAINT fk_{table}_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) " +
                                     "REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;");

            foreach (var table in ForcedTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"CREATE POLICY tenant_isolation ON {table} USING ({TenantPredicate}) WITH CHECK ({TenantPredicate});");
            }

            foreach (var table in new[] { "catalog_items", "catalog_categories", "catalog_price_types", "catalog_warehouses" })
                migrationBuilder.Sql($"ALTER TABLE {table} ADD CONSTRAINT ck_{table}_ref CHECK (length(ref) BETWEEN 1 AND 64);");
            migrationBuilder.Sql("ALTER TABLE catalog_prices ADD CONSTRAINT ck_catalog_prices_refs CHECK (length(item_ref) BETWEEN 1 AND 64 AND length(price_type_ref) BETWEEN 1 AND 64);");
            migrationBuilder.Sql("ALTER TABLE catalog_stock ADD CONSTRAINT ck_catalog_stock_refs CHECK (length(item_ref) BETWEEN 1 AND 64 AND length(warehouse_ref) BETWEEN 1 AND 64);");
            migrationBuilder.Sql("ALTER TABLE catalog_items ADD CONSTRAINT ck_catalog_items_item_type CHECK (item_type IS NULL OR item_type IN ('Goods', 'Service'));");
            migrationBuilder.Sql("ALTER TABLE ingest_freshness ADD CONSTRAINT ck_ingest_freshness_data_group CHECK (data_group IN ('Accounting', 'Catalog'));");
            migrationBuilder.Sql("ALTER TABLE ingest_batches ADD CONSTRAINT ck_ingest_batches_removed_rows CHECK (removed_rows >= 0);");

            // Search indexes for the future catalog search of the Mailo bot, tenant_id first (btree_gin, stage 4 extension):
            // any of several barcodes, and trigrams of the normalized, transliterated name.
            migrationBuilder.Sql("CREATE INDEX ix_catalog_items_tenant_id_barcodes ON catalog_items USING gin (tenant_id, barcodes);");
            migrationBuilder.Sql("CREATE INDEX ix_catalog_items_tenant_id_search_name_trgm ON catalog_items USING gin (tenant_id, search_name gin_trgm_ops);");

            // Accounting freshness so far was the connection's last accepted package: carry it over from the journal.
            migrationBuilder.Sql("""
                INSERT INTO ingest_freshness (tenant_id, connection_id, data_group, last_accepted_at)
                SELECT b.tenant_id, b.connection_id, 'Accounting', max(b.received_at)
                FROM ingest_batches b
                WHERE b.status IN ('Processed', 'ProcessedWithErrors') AND (b.accepted + b.unchanged > 0 OR b.items_total = 0)
                GROUP BY b.tenant_id, b.connection_id
                ON CONFLICT DO NOTHING;
                """);
        }
    }
}
