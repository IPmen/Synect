using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Decisions on matches can be changed (Confirmed &lt;-&gt; Rejected): their history in reconciliation_match_decisions
    /// (row-level security like every tenant table) and what a changing tool did in tool_audit_log.details.
    /// </summary>
    public partial class ReversibleMatchDecisions : Migration
    {
        private const string TenantPredicate = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "details",
                table: "tool_audit_log",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reconciliation_match_decisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    match_id = table.Column<Guid>(type: "uuid", nullable: false),
                    previous_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    new_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_key = table.Column<Guid>(type: "uuid", nullable: true),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_match_decisions", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_reconciliation_match_decisions_reconciliation_matches_tenan",
                        columns: x => new { x.tenant_id, x.match_id },
                        principalTable: "reconciliation_matches",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reconciliation_match_decisions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_match_decisions_tenant_id_match_id_decided_at",
                table: "reconciliation_match_decisions",
                columns: new[] { "tenant_id", "match_id", "decided_at" });

            migrationBuilder.Sql("ALTER TABLE reconciliation_match_decisions ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE reconciliation_match_decisions FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"CREATE POLICY tenant_isolation ON reconciliation_match_decisions USING ({TenantPredicate}) WITH CHECK ({TenantPredicate});");
            migrationBuilder.Sql("ALTER TABLE reconciliation_match_decisions ADD CONSTRAINT ck_reconciliation_match_decisions_previous CHECK (previous_status IN ('Proposed', 'Confirmed', 'Rejected'));");
            migrationBuilder.Sql("ALTER TABLE reconciliation_match_decisions ADD CONSTRAINT ck_reconciliation_match_decisions_new CHECK (new_status IN ('Confirmed', 'Rejected') AND new_status <> previous_status);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON reconciliation_match_decisions;");
            migrationBuilder.DropTable(
                name: "reconciliation_match_decisions");

            migrationBuilder.DropColumn(
                name: "details",
                table: "tool_audit_log");
        }
    }
}
