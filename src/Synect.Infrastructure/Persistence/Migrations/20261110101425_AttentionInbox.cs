using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Stage 5.5: the platform's "inbox" of cases that need the accountant's attention (attention_items) and their
    /// append-only history (attention_item_events), both under forced row-level security like every tenant table.
    /// </summary>
    public partial class AttentionInbox : Migration
    {
        private static readonly string[] Tables = ["attention_items", "attention_item_events"];

        private const string TenantPredicate = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attention_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    entity_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    entity_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    dedup_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    member_keys = table.Column<string[]>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    snoozed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by_key = table.Column<Guid>(type: "uuid", nullable: true),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attention_items", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_attention_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attention_item_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attention_item_events", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_attention_item_events_attention_items_tenant_id_item_id",
                        columns: x => new { x.tenant_id, x.item_id },
                        principalTable: "attention_items",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attention_item_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attention_item_events_tenant_id_item_id_at",
                table: "attention_item_events",
                columns: new[] { "tenant_id", "item_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_attention_items_tenant_id_rule_code_status",
                table: "attention_items",
                columns: new[] { "tenant_id", "rule_code", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_attention_items_tenant_id_status_severity_last_seen_at",
                table: "attention_items",
                columns: new[] { "tenant_id", "status", "severity", "last_seen_at" });

            migrationBuilder.CreateIndex(
                name: "ux_attention_items_dedup_open",
                table: "attention_items",
                columns: new[] { "tenant_id", "dedup_key" },
                unique: true,
                filter: "status <> 'Resolved'");

            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"CREATE POLICY tenant_isolation ON {table} USING ({TenantPredicate}) WITH CHECK ({TenantPredicate});");
            }

            migrationBuilder.Sql("ALTER TABLE attention_items ADD CONSTRAINT ck_attention_items_severity CHECK (severity IN ('Critical', 'High', 'Medium', 'Low'));");
            migrationBuilder.Sql("ALTER TABLE attention_items ADD CONSTRAINT ck_attention_items_status CHECK (status IN ('New', 'Seen', 'Snoozed', 'Dismissed', 'Resolved'));");
            migrationBuilder.Sql("ALTER TABLE attention_items ADD CONSTRAINT ck_attention_items_category CHECK (category IN ('Connection', 'Bank', 'Reconciliation', 'Documents'));");
            migrationBuilder.Sql("ALTER TABLE attention_items ADD CONSTRAINT ck_attention_items_snooze CHECK (status <> 'Snoozed' OR snoozed_until IS NOT NULL);");
            migrationBuilder.Sql("ALTER TABLE attention_item_events ADD CONSTRAINT ck_attention_item_events_kind CHECK (kind IN ('Created', 'Reopened', 'Seen', 'Snoozed', 'Dismissed', 'AutoResolved'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");

            migrationBuilder.DropTable(
                name: "attention_item_events");

            migrationBuilder.DropTable(
                name: "attention_items");
        }
    }
}
