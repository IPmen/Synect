using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Stage 6: bank statement file imports (statement_imports, forced row-level security like every tenant table), the
    /// origin of operation ids (bank_transactions.id_source: Bank, Synthetic, File, FileHash) and technical details
    /// (bank_transactions.details); attention items get the Archived status (dismissed items whose condition stayed gone).
    /// </summary>
    public partial class StatementImports : Migration
    {
        private const string TenantPredicate = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_attention_items_dedup_open",
                table: "attention_items");

            migrationBuilder.AddColumn<string>(
                name: "details",
                table: "bank_transactions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "id_source",
                table: "bank_transactions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Bank");

            migrationBuilder.CreateTable(
                name: "statement_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sync_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    file_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    profile = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    imported_by_key = table.Column<Guid>(type: "uuid", nullable: true),
                    account = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    period_from = table.Column<DateOnly>(type: "date", nullable: false),
                    period_to = table.Column<DateOnly>(type: "date", nullable: false),
                    transactions_total = table.Column<int>(type: "integer", nullable: false),
                    transactions_new = table.Column<int>(type: "integer", nullable: false),
                    transactions_duplicate = table.Column<int>(type: "integer", nullable: false),
                    rows_rejected = table.Column<int>(type: "integer", nullable: false),
                    warning_count = table.Column<int>(type: "integer", nullable: false),
                    balance_check = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_statement_imports", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_statement_imports_connections_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_statement_imports_sync_runs_tenant_id_sync_run_id",
                        columns: x => new { x.tenant_id, x.sync_run_id },
                        principalTable: "sync_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_statement_imports_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_attention_items_dedup_open",
                table: "attention_items",
                columns: new[] { "tenant_id", "dedup_key" },
                unique: true,
                filter: "status NOT IN ('Resolved', 'Archived')");

            migrationBuilder.CreateIndex(
                name: "ix_statement_imports_tenant_id_connection_id_imported_at",
                table: "statement_imports",
                columns: new[] { "tenant_id", "connection_id", "imported_at" });

            migrationBuilder.CreateIndex(
                name: "ix_statement_imports_tenant_id_file_sha256",
                table: "statement_imports",
                columns: new[] { "tenant_id", "file_sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_statement_imports_tenant_id_sync_run_id",
                table: "statement_imports",
                columns: new[] { "tenant_id", "sync_run_id" });

            migrationBuilder.Sql("ALTER TABLE statement_imports ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE statement_imports FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"CREATE POLICY tenant_isolation ON statement_imports USING ({TenantPredicate}) WITH CHECK ({TenantPredicate});");
            migrationBuilder.Sql("ALTER TABLE statement_imports ADD CONSTRAINT ck_statement_imports_format CHECK (format IN ('Mt940', 'Csv'));");
            migrationBuilder.Sql("ALTER TABLE statement_imports ADD CONSTRAINT ck_statement_imports_balance_check CHECK (balance_check IN ('Passed', 'Failed', 'Skipped'));");
            migrationBuilder.Sql("ALTER TABLE statement_imports ADD CONSTRAINT ck_statement_imports_counts CHECK (transactions_total >= 0 AND transactions_new >= 0 AND transactions_duplicate >= 0 AND rows_rejected >= 0 AND period_from <= period_to);");

            // Operations loaded with a synthetic id before this migration ("syn:" prefix) are marked as such.
            migrationBuilder.Sql("UPDATE bank_transactions SET id_source = 'Synthetic' WHERE operation_id LIKE 'syn:%';");
            migrationBuilder.Sql("ALTER TABLE bank_transactions ADD CONSTRAINT ck_bank_transactions_id_source CHECK (id_source IN ('Bank', 'Synthetic', 'File', 'FileHash'));");

            migrationBuilder.Sql("ALTER TABLE attention_items DROP CONSTRAINT ck_attention_items_status;");
            migrationBuilder.Sql("ALTER TABLE attention_items ADD CONSTRAINT ck_attention_items_status CHECK (status IN ('New', 'Seen', 'Snoozed', 'Dismissed', 'Resolved', 'Archived'));");
            migrationBuilder.Sql("ALTER TABLE attention_item_events DROP CONSTRAINT ck_attention_item_events_kind;");
            migrationBuilder.Sql("ALTER TABLE attention_item_events ADD CONSTRAINT ck_attention_item_events_kind CHECK (kind IN ('Created', 'Reopened', 'Seen', 'Snoozed', 'Dismissed', 'AutoResolved', 'Archived'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Archived items go back to Dismissed (the status the person chose), events of kind Archived are removed (the older schema has no such kind).
            migrationBuilder.Sql("UPDATE attention_items SET status = 'Dismissed' WHERE status = 'Archived';");
            migrationBuilder.Sql("ALTER TABLE attention_items DROP CONSTRAINT ck_attention_items_status;");
            migrationBuilder.Sql("ALTER TABLE attention_items ADD CONSTRAINT ck_attention_items_status CHECK (status IN ('New', 'Seen', 'Snoozed', 'Dismissed', 'Resolved'));");
            migrationBuilder.Sql("ALTER TABLE attention_item_events DROP CONSTRAINT ck_attention_item_events_kind;");
            migrationBuilder.Sql("DELETE FROM attention_item_events WHERE kind = 'Archived';");
            migrationBuilder.Sql("ALTER TABLE attention_item_events ADD CONSTRAINT ck_attention_item_events_kind CHECK (kind IN ('Created', 'Reopened', 'Seen', 'Snoozed', 'Dismissed', 'AutoResolved'));");
            migrationBuilder.Sql("ALTER TABLE bank_transactions DROP CONSTRAINT IF EXISTS ck_bank_transactions_id_source;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON statement_imports;");

            migrationBuilder.DropTable(
                name: "statement_imports");

            migrationBuilder.DropIndex(
                name: "ux_attention_items_dedup_open",
                table: "attention_items");

            migrationBuilder.DropColumn(
                name: "details",
                table: "bank_transactions");

            migrationBuilder.DropColumn(
                name: "id_source",
                table: "bank_transactions");

            migrationBuilder.CreateIndex(
                name: "ux_attention_items_dedup_open",
                table: "attention_items",
                columns: new[] { "tenant_id", "dedup_key" },
                unique: true,
                filter: "status <> 'Resolved'");
        }
    }
}
