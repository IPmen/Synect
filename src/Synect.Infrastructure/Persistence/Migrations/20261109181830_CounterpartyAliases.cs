using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CounterpartyAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Trusted extensions (PostgreSQL 13+): the database owner can create them without superuser rights.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gin;");

            migrationBuilder.CreateTable(
                name: "counterparty_aliases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counterparty_inn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    occurrences = table.Column<int>(type: "integer", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_counterparty_aliases", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_counterparty_aliases_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_counterparty_aliases_tenant_id_counterparty_inn_source_name",
                table: "counterparty_aliases",
                columns: new[] { "tenant_id", "counterparty_inn", "source", "name" },
                unique: true);
            migrationBuilder.Sql("ALTER TABLE counterparty_aliases ADD CONSTRAINT ck_counterparty_aliases_source CHECK (source IN ('Bank', 'Edo'));");
            // Fuzzy search by name inside one tenant: tenant_id (btree_gin) + trigrams of the normalized name.
            migrationBuilder.Sql("CREATE INDEX ix_counterparty_aliases_tenant_id_normalized_name_trgm ON counterparty_aliases " +
                "USING gin (tenant_id, normalized_name gin_trgm_ops);");

            // Same row-level security rule as every other tenant table.
            migrationBuilder.Sql("ALTER TABLE counterparty_aliases ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE counterparty_aliases FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("CREATE POLICY tenant_isolation ON counterparty_aliases " +
                "USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) " +
                "WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON counterparty_aliases;");
            migrationBuilder.DropTable(
                name: "counterparty_aliases");
        }
    }
}
