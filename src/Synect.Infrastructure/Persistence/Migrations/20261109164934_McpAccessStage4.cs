using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class McpAccessStage4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    key_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scopes = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_api_keys_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tool_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    api_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tool_audit_log", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_tool_audit_log_api_keys_tenant_id_api_key_id",
                        columns: x => new { x.tenant_id, x.api_key_id },
                        principalTable: "api_keys",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tool_audit_log_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_key_hash",
                table: "api_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tool_audit_log_tenant_id_api_key_id",
                table: "tool_audit_log",
                columns: new[] { "tenant_id", "api_key_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tool_audit_log_tenant_id_started_at",
                table: "tool_audit_log",
                columns: new[] { "tenant_id", "started_at" });

            migrationBuilder.Sql("ALTER TABLE api_keys ADD CONSTRAINT ck_api_keys_key_hash_length CHECK (octet_length(key_hash) = 32);");
            migrationBuilder.Sql("ALTER TABLE api_keys ADD CONSTRAINT ck_api_keys_scopes CHECK (scopes <@ ARRAY['read', 'sync_request']::text[]);");
            migrationBuilder.Sql("ALTER TABLE tool_audit_log ADD CONSTRAINT ck_tool_audit_log_status CHECK (status IN ('Ok', 'Error', 'RateLimited', 'Timeout'));");

            // tool_audit_log: the same row-level security rule as every other tenant table.
            migrationBuilder.Sql("ALTER TABLE tool_audit_log ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE tool_audit_log FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("CREATE POLICY tenant_isolation ON tool_audit_log " +
                "USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) " +
                "WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");

            // api_keys: RLS is enabled for the application role (synect_app sees only the keys of the tenant in
            // app.tenant_id), but deliberately NOT forced, so that the table owner bypasses it. The owner-defined
            // SECURITY DEFINER function below is the only way to find a key before the tenant is known: it takes the
            // SHA-256 of a presented key, matches exactly one non-revoked row and returns only (id, tenant_id, scopes).
            migrationBuilder.Sql("ALTER TABLE api_keys ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("CREATE POLICY tenant_isolation ON api_keys " +
                "USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) " +
                "WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);");
            migrationBuilder.Sql("""
                CREATE FUNCTION synect_resolve_api_key(p_key_hash bytea)
                RETURNS TABLE (api_key_id uuid, tenant_id uuid, scopes text[])
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $fn$
                #variable_conflict use_column
                BEGIN
                    IF p_key_hash IS NULL OR octet_length(p_key_hash) <> 32 THEN
                        RETURN;
                    END IF;
                    RETURN QUERY
                        SELECT k.id, k.tenant_id, k.scopes FROM public.api_keys k
                        WHERE k.key_hash = p_key_hash AND k.revoked_at IS NULL;
                    IF FOUND THEN
                        -- At most one write per key and minute.
                        UPDATE public.api_keys SET last_used_at = now()
                        WHERE key_hash = p_key_hash AND (last_used_at IS NULL OR last_used_at < now() - interval '1 minute');
                    END IF;
                END
                $fn$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION synect_resolve_api_key(bytea) FROM PUBLIC;");
            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'synect_app') THEN
                        GRANT EXECUTE ON FUNCTION synect_resolve_api_key(bytea) TO synect_app;
                    END IF;
                END
                $do$;
                """);
        }


        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS synect_resolve_api_key(bytea);");
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON tool_audit_log;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON api_keys;");

            migrationBuilder.DropTable(
                name: "tool_audit_log");

            migrationBuilder.DropTable(
                name: "api_keys");
        }
    }
}
