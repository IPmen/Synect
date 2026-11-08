using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnalyticsIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_edo_documents_tenant_id_counterparty_inn",
                table: "edo_documents");

            migrationBuilder.DropIndex(
                name: "ix_bank_transactions_tenant_id_counterparty_inn",
                table: "bank_transactions");

            migrationBuilder.CreateIndex(
                name: "ix_edo_documents_tenant_id_counterparty_inn",
                table: "edo_documents",
                columns: new[] { "tenant_id", "counterparty_inn" })
                .Annotation("Npgsql:IndexInclude", new[] { "status" });

            migrationBuilder.CreateIndex(
                name: "ix_edo_documents_tenant_id_owner_status_document_date",
                table: "edo_documents",
                columns: new[] { "tenant_id", "owner", "status", "document_date" });

            migrationBuilder.CreateIndex(
                name: "ix_edo_documents_tenant_id_source_created_at",
                table: "edo_documents",
                columns: new[] { "tenant_id", "source_created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_edo_documents_tenant_id_source_updated_at",
                table: "edo_documents",
                columns: new[] { "tenant_id", "source_updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_transactions_flows",
                table: "bank_transactions",
                columns: new[] { "tenant_id", "posting_date" },
                filter: "has_error = false")
                .Annotation("Npgsql:IndexInclude", new[] { "bank_account_id", "currency", "direction", "amount", "counterparty_inn" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_transactions_tenant_id_counterparty_inn_posting_date",
                table: "bank_transactions",
                columns: new[] { "tenant_id", "counterparty_inn", "posting_date" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_daily_balances_tenant_id_date",
                table: "bank_daily_balances",
                columns: new[] { "tenant_id", "date" });

            // V5: prefix search (LIKE 'x%') must work whatever the database collation is.
            migrationBuilder.Sql("CREATE INDEX ix_edo_documents_tenant_id_number_prefix ON edo_documents (tenant_id, number text_pattern_ops);");
            migrationBuilder.Sql("CREATE INDEX ix_edo_documents_tenant_id_contract_number_prefix ON edo_documents (tenant_id, contract_number text_pattern_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_edo_documents_tenant_id_number_prefix;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_edo_documents_tenant_id_contract_number_prefix;");

            migrationBuilder.DropIndex(
                name: "ix_edo_documents_tenant_id_counterparty_inn",
                table: "edo_documents");

            migrationBuilder.DropIndex(
                name: "ix_edo_documents_tenant_id_owner_status_document_date",
                table: "edo_documents");

            migrationBuilder.DropIndex(
                name: "ix_edo_documents_tenant_id_source_created_at",
                table: "edo_documents");

            migrationBuilder.DropIndex(
                name: "ix_edo_documents_tenant_id_source_updated_at",
                table: "edo_documents");

            migrationBuilder.DropIndex(
                name: "ix_bank_transactions_flows",
                table: "bank_transactions");

            migrationBuilder.DropIndex(
                name: "ix_bank_transactions_tenant_id_counterparty_inn_posting_date",
                table: "bank_transactions");

            migrationBuilder.DropIndex(
                name: "ix_bank_daily_balances_tenant_id_date",
                table: "bank_daily_balances");

            migrationBuilder.CreateIndex(
                name: "ix_edo_documents_tenant_id_counterparty_inn",
                table: "edo_documents",
                columns: new[] { "tenant_id", "counterparty_inn" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_transactions_tenant_id_counterparty_inn",
                table: "bank_transactions",
                columns: new[] { "tenant_id", "counterparty_inn" });
        }
    }
}
