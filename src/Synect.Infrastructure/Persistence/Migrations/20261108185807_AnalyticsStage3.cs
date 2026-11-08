using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnalyticsStage3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "status_comment",
                table: "edo_documents",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "analytics_counterparty_monthly",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counterparty_inn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    month = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    bank_incoming = table.Column<long>(type: "bigint", nullable: false),
                    bank_outgoing = table.Column<long>(type: "bigint", nullable: false),
                    bank_count = table.Column<int>(type: "integer", nullable: false),
                    edo_incoming_total = table.Column<long>(type: "bigint", nullable: false),
                    edo_outgoing_total = table.Column<long>(type: "bigint", nullable: false),
                    edo_incoming_count = table.Column<int>(type: "integer", nullable: false),
                    edo_outgoing_count = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_analytics_counterparty_monthly", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_analytics_counterparty_monthly_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_analytics_counterparty_monthly_tenant_id_counterparty_inn_m",
                table: "analytics_counterparty_monthly",
                columns: new[] { "tenant_id", "counterparty_inn", "month", "currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_analytics_counterparty_monthly_tenant_id_month_currency",
                table: "analytics_counterparty_monthly",
                columns: new[] { "tenant_id", "month", "currency" });

            // Same row-level security rule as every other tenant table.
            migrationBuilder.Sql("ALTER TABLE analytics_counterparty_monthly ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE analytics_counterparty_monthly FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("CREATE POLICY tenant_isolation ON analytics_counterparty_monthly " +
                "USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) " +
                "WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON analytics_counterparty_monthly;");

            migrationBuilder.DropTable(
                name: "analytics_counterparty_monthly");

            migrationBuilder.DropColumn(
                name: "status_comment",
                table: "edo_documents");
        }
    }
}
