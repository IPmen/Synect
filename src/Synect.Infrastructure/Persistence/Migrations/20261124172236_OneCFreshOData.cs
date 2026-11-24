using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OneCFreshOData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Packages loaded by Synect itself (1C:Fresh over OData) have no ingest key: their source is the connection.
            migrationBuilder.AlterColumn<Guid>(
                name: "ingest_key_id",
                table: "ingest_batches",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            // At most one 1C connection (extension or OData) that is not Disabled per organization. An organization that
            // already has two is not changed silently: the migration stops and names it, an administrator disables one.
            migrationBuilder.Sql("""
                DO $$
                DECLARE t uuid;
                BEGIN
                    SELECT tenant_id INTO t FROM connections
                    WHERE adapter_kind = 'Accounting1C' AND status <> 'Disabled'
                    GROUP BY tenant_id HAVING count(*) > 1 LIMIT 1;
                    IF t IS NOT NULL THEN
                        RAISE EXCEPTION 'Organization % has more than one active 1C connection; disable all but one before this migration.', t;
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ux_connections_one_active_1c",
                table: "connections",
                column: "tenant_id",
                unique: true,
                filter: "adapter_kind = 'Accounting1C' AND status <> 'Disabled'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM ingest_batches WHERE ingest_key_id IS NULL) THEN
                        RAISE EXCEPTION 'ingest_batches has packages of 1C:Fresh OData connections (no ingest key); they must be removed before rolling back.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ux_connections_one_active_1c",
                table: "connections");

            migrationBuilder.AlterColumn<Guid>(
                name: "ingest_key_id",
                table: "ingest_batches",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
