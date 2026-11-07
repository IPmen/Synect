using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Row-level security for every tenant-scoped table, driven by the transaction-local setting
    /// app.tenant_id (see TenantSessionInterceptor). FORCE makes the policies apply to the table
    /// owner too; only superusers and BYPASSRLS roles skip them. The tenants table itself is not
    /// under RLS: the worker must enumerate tenants.
    /// Also makes edo_document_status_logs append-only at the database level.
    /// </summary>
    public partial class RowLevelSecurity : Migration
    {
        private static readonly string[] TenantTables =
        [
            "connections", "sync_runs", "raw_objects", "bank_accounts", "bank_transactions",
            "bank_daily_balances", "edo_documents", "edo_document_status_logs", "counterparties",
        ];

        private const string TenantPredicate = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY tenant_isolation ON {table} USING ({TenantPredicate}) WITH CHECK ({TenantPredicate});");
            }

            migrationBuilder.Sql("""
                CREATE FUNCTION edo_document_status_logs_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'edo_document_status_logs is append-only';
                END;
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_edo_document_status_logs_append_only
                BEFORE UPDATE OR DELETE ON edo_document_status_logs
                FOR EACH ROW EXECUTE FUNCTION edo_document_status_logs_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_edo_document_status_logs_append_only ON edo_document_status_logs;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS edo_document_status_logs_append_only();");

            foreach (var table in TenantTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
                migrationBuilder.Sql($"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
