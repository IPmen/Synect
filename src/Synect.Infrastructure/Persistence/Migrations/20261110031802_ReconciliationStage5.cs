using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Stage 5: reconciliation of bank payments with Didox documents (runs, matches, match items, findings; all under
    /// row-level security) and the document currency and seller account columns.
    /// </summary>
    public partial class ReconciliationStage5 : Migration
    {
        private static readonly string[] ReconciliationTables =
            ["reconciliation_runs", "reconciliation_matches", "reconciliation_match_items", "reconciliation_findings"];

        private const string TenantPredicate = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "currency",
                table: "edo_documents",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "UZS");

            migrationBuilder.AddColumn<string>(
                name: "seller_account",
                table: "edo_documents",
                type: "character varying(34)",
                maxLength: 34,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reconciliation_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    scope_inns = table.Column<string[]>(type: "text[]", nullable: true),
                    match_count = table.Column<int>(type: "integer", nullable: false),
                    created_count = table.Column<int>(type: "integer", nullable: false),
                    removed_count = table.Column<int>(type: "integer", nullable: false),
                    rules_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_runs", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_reconciliation_runs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    counterparty_inn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    related_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    related_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confidence = table.Column<int>(type: "integer", nullable: false),
                    reasons = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_findings", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_reconciliation_findings_reconciliation_runs_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalTable: "reconciliation_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reconciliation_findings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_matches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    match_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    confidence = table.Column<int>(type: "integer", nullable: false),
                    reasons = table.Column<string>(type: "jsonb", nullable: false),
                    counterparty_inn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    document_owner = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    payments_total = table.Column<long>(type: "bigint", nullable: false),
                    documents_total = table.Column<long>(type: "bigint", nullable: false),
                    signature = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_key = table.Column<Guid>(type: "uuid", nullable: true),
                    decision_comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_matches", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_reconciliation_matches_reconciliation_runs_tenant_id_run_id",
                        columns: x => new { x.tenant_id, x.run_id },
                        principalTable: "reconciliation_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reconciliation_matches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_match_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    match_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    allocated_amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    match_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_match_items", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_reconciliation_match_items_reconciliation_matches_tenant_id",
                        columns: x => new { x.tenant_id, x.match_id },
                        principalTable: "reconciliation_matches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_reconciliation_match_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_findings_tenant_id_counterparty_inn",
                table: "reconciliation_findings",
                columns: new[] { "tenant_id", "counterparty_inn" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_findings_tenant_id_kind_subject_id",
                table: "reconciliation_findings",
                columns: new[] { "tenant_id", "kind", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_findings_tenant_id_run_id",
                table: "reconciliation_findings",
                columns: new[] { "tenant_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_match_items_tenant_id_match_id_kind_ref_id",
                table: "reconciliation_match_items",
                columns: new[] { "tenant_id", "match_id", "kind", "ref_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_reconciliation_match_items_proposed",
                table: "reconciliation_match_items",
                columns: new[] { "tenant_id", "kind", "ref_id" },
                unique: true,
                filter: "match_status = 'Proposed'");

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_matches_tenant_id_counterparty_inn_status",
                table: "reconciliation_matches",
                columns: new[] { "tenant_id", "counterparty_inn", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_matches_tenant_id_run_id",
                table: "reconciliation_matches",
                columns: new[] { "tenant_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_matches_tenant_id_status_match_type",
                table: "reconciliation_matches",
                columns: new[] { "tenant_id", "status", "match_type" });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_runs_tenant_id_started_at",
                table: "reconciliation_runs",
                columns: new[] { "tenant_id", "started_at" });

            // Same row-level security rule as every other tenant table.
            foreach (var table in ReconciliationTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"CREATE POLICY tenant_isolation ON {table} USING ({TenantPredicate}) WITH CHECK ({TenantPredicate});");
            }

            migrationBuilder.Sql("ALTER TABLE reconciliation_matches ADD CONSTRAINT ck_reconciliation_matches_confidence CHECK (confidence BETWEEN 0 AND 100);");
            migrationBuilder.Sql("ALTER TABLE reconciliation_matches ADD CONSTRAINT ck_reconciliation_matches_status CHECK (status IN ('Proposed', 'Confirmed', 'Rejected'));");
            migrationBuilder.Sql("ALTER TABLE reconciliation_matches ADD CONSTRAINT ck_reconciliation_matches_type CHECK (match_type IN ('Exact', 'OneToMany', 'ManyToOne', 'Partial'));");
            migrationBuilder.Sql("ALTER TABLE reconciliation_match_items ADD CONSTRAINT ck_reconciliation_match_items_kind CHECK (kind IN ('BankTransaction', 'EdoDocument'));");
            migrationBuilder.Sql("ALTER TABLE reconciliation_match_items ADD CONSTRAINT ck_reconciliation_match_items_amount CHECK (allocated_amount_minor >= 0);");

            // New API key scope "review" (confirm_match / reject_match).
            migrationBuilder.Sql("ALTER TABLE api_keys DROP CONSTRAINT ck_api_keys_scopes;");
            migrationBuilder.Sql("ALTER TABLE api_keys ADD CONSTRAINT ck_api_keys_scopes CHECK (scopes <@ ARRAY['read', 'sync_request', 'review']::text[]);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE api_keys DROP CONSTRAINT ck_api_keys_scopes;");
            migrationBuilder.Sql("ALTER TABLE api_keys ADD CONSTRAINT ck_api_keys_scopes CHECK (scopes <@ ARRAY['read', 'sync_request']::text[]);");
            foreach (var table in ReconciliationTables)
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");

            migrationBuilder.DropTable(
                name: "reconciliation_findings");

            migrationBuilder.DropTable(
                name: "reconciliation_match_items");

            migrationBuilder.DropTable(
                name: "reconciliation_matches");

            migrationBuilder.DropTable(
                name: "reconciliation_runs");

            migrationBuilder.DropColumn(
                name: "currency",
                table: "edo_documents");

            migrationBuilder.DropColumn(
                name: "seller_account",
                table: "edo_documents");
        }
    }
}
