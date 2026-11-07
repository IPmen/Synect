using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    inn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    adapter_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    last_successful_sync_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    credentials_ref = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connections", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_connections_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "counterparties",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_counterparties", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_counterparties_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bank_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_number = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: false),
                    mfo = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    external_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_accounts", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_bank_accounts_connections_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_accounts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "edo_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    doc_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    owner = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    number = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    document_date = table.Column<DateOnly>(type: "date", nullable: true),
                    counterparty_inn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    counterparty_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    contract_number = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    contract_date = table.Column<DateOnly>(type: "date", nullable: true),
                    total_sum = table.Column<long>(type: "bigint", nullable: false),
                    total_vat_sum = table.Column<long>(type: "bigint", nullable: false),
                    total_delivery_sum = table.Column<long>(type: "bigint", nullable: false),
                    total_delivery_sum_with_vat = table.Column<long>(type: "bigint", nullable: false),
                    source_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edo_documents", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_edo_documents_connections_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_edo_documents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "raw_objects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    object_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    external_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_raw_objects", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_raw_objects_connections_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_raw_objects_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sync_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_complete = table.Column<bool>(type: "boolean", nullable: false),
                    record_count = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    range_from = table.Column<DateOnly>(type: "date", nullable: true),
                    range_to = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sync_runs", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_sync_runs_connections_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sync_runs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bank_daily_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    opening_balance = table.Column<long>(type: "bigint", nullable: false),
                    closing_balance = table.Column<long>(type: "bigint", nullable: false),
                    total_debit = table.Column<long>(type: "bigint", nullable: false),
                    total_credit = table.Column<long>(type: "bigint", nullable: false),
                    is_day_closed = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_daily_balances", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_bank_daily_balances_bank_accounts_tenant_id_bank_account_id",
                        columns: x => new { x.tenant_id, x.bank_account_id },
                        principalTable: "bank_accounts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_daily_balances_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bank_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bank_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    counterparty_inn = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    counterparty_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    counterparty_account = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    counterparty_mfo = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: true),
                    purpose_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    document_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    posting_date = table.Column<DateOnly>(type: "date", nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: true),
                    is_final = table.Column<bool>(type: "boolean", nullable: false),
                    source_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    source_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bank_transactions", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_bank_transactions_bank_accounts_tenant_id_bank_account_id",
                        columns: x => new { x.tenant_id, x.bank_account_id },
                        principalTable: "bank_accounts",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_transactions_connections_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bank_transactions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "edo_document_status_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    edo_document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sync_run_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_edo_document_status_logs", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_edo_document_status_logs_edo_documents_tenant_id_edo_docume",
                        columns: x => new { x.tenant_id, x.edo_document_id },
                        principalTable: "edo_documents",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_edo_document_status_logs_sync_runs_tenant_id_sync_run_id",
                        columns: x => new { x.tenant_id, x.sync_run_id },
                        principalTable: "sync_runs",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_edo_document_status_logs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bank_accounts_tenant_id_connection_id_account_number",
                table: "bank_accounts",
                columns: new[] { "tenant_id", "connection_id", "account_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bank_daily_balances_tenant_id_bank_account_id_date",
                table: "bank_daily_balances",
                columns: new[] { "tenant_id", "bank_account_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bank_transactions_tenant_id_bank_account_id_operation_id",
                table: "bank_transactions",
                columns: new[] { "tenant_id", "bank_account_id", "operation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bank_transactions_tenant_id_bank_account_id_posting_date",
                table: "bank_transactions",
                columns: new[] { "tenant_id", "bank_account_id", "posting_date" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_transactions_tenant_id_connection_id",
                table: "bank_transactions",
                columns: new[] { "tenant_id", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_bank_transactions_tenant_id_counterparty_inn",
                table: "bank_transactions",
                columns: new[] { "tenant_id", "counterparty_inn" });

            migrationBuilder.CreateIndex(
                name: "ix_connections_tenant_id_source_code",
                table: "connections",
                columns: new[] { "tenant_id", "source_code" });

            migrationBuilder.CreateIndex(
                name: "ix_counterparties_tenant_id_inn",
                table: "counterparties",
                columns: new[] { "tenant_id", "inn" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_edo_document_status_logs_tenant_id_edo_document_id_recorded",
                table: "edo_document_status_logs",
                columns: new[] { "tenant_id", "edo_document_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_edo_document_status_logs_tenant_id_sync_run_id",
                table: "edo_document_status_logs",
                columns: new[] { "tenant_id", "sync_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_edo_documents_tenant_id_connection_id_external_id",
                table: "edo_documents",
                columns: new[] { "tenant_id", "connection_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_edo_documents_tenant_id_counterparty_inn",
                table: "edo_documents",
                columns: new[] { "tenant_id", "counterparty_inn" });

            migrationBuilder.CreateIndex(
                name: "ix_edo_documents_tenant_id_document_date",
                table: "edo_documents",
                columns: new[] { "tenant_id", "document_date" });

            migrationBuilder.CreateIndex(
                name: "ix_raw_objects_tenant_id_connection_id_object_type_external_id",
                table: "raw_objects",
                columns: new[] { "tenant_id", "connection_id", "object_type", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sync_runs_tenant_id_connection_id_started_at",
                table: "sync_runs",
                columns: new[] { "tenant_id", "connection_id", "started_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bank_daily_balances");

            migrationBuilder.DropTable(
                name: "bank_transactions");

            migrationBuilder.DropTable(
                name: "counterparties");

            migrationBuilder.DropTable(
                name: "edo_document_status_logs");

            migrationBuilder.DropTable(
                name: "raw_objects");

            migrationBuilder.DropTable(
                name: "bank_accounts");

            migrationBuilder.DropTable(
                name: "edo_documents");

            migrationBuilder.DropTable(
                name: "sync_runs");

            migrationBuilder.DropTable(
                name: "connections");

            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
