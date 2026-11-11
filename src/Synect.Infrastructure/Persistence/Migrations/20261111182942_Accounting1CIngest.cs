using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Accounting1CIngest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "accounting_bank_account_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    as_of_date = table.Column<DateOnly>(type: "date", nullable: false),
                    bank_account_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    balance_minor = table.Column<long>(type: "bigint", nullable: false),
                    turnover_debit_minor = table.Column<long>(type: "bigint", nullable: true),
                    turnover_credit_minor = table.Column<long>(type: "bigint", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_bank_account_balances", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_accounting_bank_account_balances_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounting_bank_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bank_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    bank_name = table.Column<string>(type: "text", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    gl_account_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_bank_accounts", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_accounting_bank_accounts_tenant_id_ref", x => new { x.tenant_id, x.@ref });
                    table.ForeignKey(
                        name: "fk_accounting_bank_accounts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounting_bank_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    posted = table.Column<bool>(type: "boolean", nullable: false),
                    bank_account_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    bank_account_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    counterparty_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    counterparty_tin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: true),
                    contract_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    bank_document_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    bank_document_date = table.Column<DateOnly>(type: "date", nullable: true),
                    cash_flow_item = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_bank_documents", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_accounting_bank_documents_tenant_id_ref", x => new { x.tenant_id, x.@ref });
                    table.ForeignKey(
                        name: "fk_accounting_bank_documents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounting_contracts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counterparty_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    date = table.Column<DateOnly>(type: "date", nullable: true),
                    kind = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    valid_until = table.Column<DateOnly>(type: "date", nullable: true),
                    payment_term_days = table.Column<int>(type: "integer", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_contracts", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_accounting_contracts_tenant_id_ref", x => new { x.tenant_id, x.@ref });
                    table.ForeignKey(
                        name: "fk_accounting_contracts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounting_counterparties",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_counterparties", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_accounting_counterparties_tenant_id_ref", x => new { x.tenant_id, x.@ref });
                    table.ForeignKey(
                        name: "fk_accounting_counterparties_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounting_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    doc_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    doc_type = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    posted = table.Column<bool>(type: "boolean", nullable: false),
                    counterparty_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    counterparty_tin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    contract_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    contract_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    contract_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    amount_total_minor = table.Column<long>(type: "bigint", nullable: false),
                    vat_amount_minor = table.Column<long>(type: "bigint", nullable: true),
                    esf_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    esf_date = table.Column<DateOnly>(type: "date", nullable: true),
                    esf_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    esf_status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    comment = table.Column<string>(type: "text", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_documents", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_accounting_documents_tenant_id_ref", x => new { x.tenant_id, x.@ref });
                    table.ForeignKey(
                        name: "fk_accounting_documents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounting_payment_orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    posted = table.Column<bool>(type: "boolean", nullable: false),
                    bank_account_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    counterparty_tin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: true),
                    bank_status = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    @ref = table.Column<string>(name: "ref", type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_payment_orders", x => new { x.tenant_id, x.id });
                    table.UniqueConstraint("ak_accounting_payment_orders_tenant_id_ref", x => new { x.tenant_id, x.@ref });
                    table.ForeignKey(
                        name: "fk_accounting_payment_orders_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounting_settlement_balances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    as_of_date = table.Column<DateOnly>(type: "date", nullable: false),
                    counterparty_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    counterparty_tin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    contract_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    gl_account_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    balance_debit_minor = table.Column<long>(type: "bigint", nullable: true),
                    balance_credit_minor = table.Column<long>(type: "bigint", nullable: true),
                    turnover_debit_minor = table.Column<long>(type: "bigint", nullable: true),
                    turnover_credit_minor = table.Column<long>(type: "bigint", nullable: true),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    modified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    last_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_settlement_balances", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_accounting_settlement_balances_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ingest_full_sync_items",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_sync_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    item_key = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ingest_full_sync_items", x => new { x.tenant_id, x.full_sync_id, x.entity, x.item_key });
                    table.ForeignKey(
                        name: "fk_ingest_full_sync_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ingest_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    key_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    key_prefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ingest_keys", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_ingest_keys_connections_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ingest_keys_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "accounting_bank_document_settlements",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    settled_document_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_bank_document_settlements", x => new { x.tenant_id, x.document_ref, x.line_no });
                    table.ForeignKey(
                        name: "fk_accounting_bank_document_settlements_accounting_bank_docume",
                        columns: x => new { x.tenant_id, x.document_ref },
                        principalTable: "accounting_bank_documents",
                        principalColumns: new[] { "tenant_id", "ref" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "accounting_document_lines",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: true),
                    product_code = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(28,6)", nullable: true),
                    price_minor = table.Column<long>(type: "bigint", nullable: true),
                    vat_rate = table.Column<decimal>(type: "numeric(9,4)", nullable: true),
                    amount_minor = table.Column<long>(type: "bigint", nullable: false),
                    extra = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounting_document_lines", x => new { x.tenant_id, x.document_ref, x.line_no });
                    table.ForeignKey(
                        name: "fk_accounting_document_lines_accounting_documents_tenant_id_do",
                        columns: x => new { x.tenant_id, x.document_ref },
                        principalTable: "accounting_documents",
                        principalColumns: new[] { "tenant_id", "ref" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ingest_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ingest_key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    entity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    full_sync_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_last_batch = table.Column<bool>(type: "boolean", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    extension_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    exported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    items_total = table.Column<int>(type: "integer", nullable: false),
                    accepted = table.Column<int>(type: "integer", nullable: false),
                    unchanged = table.Column<int>(type: "integer", nullable: false),
                    rejected = table.Column<int>(type: "integer", nullable: false),
                    marked_deleted = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    payload_sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    payload_bytes = table.Column<int>(type: "integer", nullable: false),
                    item_errors = table.Column<string>(type: "jsonb", nullable: true),
                    envelope_extra = table.Column<string>(type: "jsonb", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ingest_batches", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_ingest_batches_connections_tenant_id_connection_id",
                        columns: x => new { x.tenant_id, x.connection_id },
                        principalTable: "connections",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ingest_batches_ingest_keys_tenant_id_ingest_key_id",
                        columns: x => new { x.tenant_id, x.ingest_key_id },
                        principalTable: "ingest_keys",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_ingest_batches_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_bank_account_balances_tenant_id_as_of_date_bank_",
                table: "accounting_bank_account_balances",
                columns: new[] { "tenant_id", "as_of_date", "bank_account_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounting_bank_accounts_tenant_id_number",
                table: "accounting_bank_accounts",
                columns: new[] { "tenant_id", "number" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_bank_documents_tenant_id_counterparty_tin_date",
                table: "accounting_bank_documents",
                columns: new[] { "tenant_id", "counterparty_tin", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_contracts_tenant_id_counterparty_ref",
                table: "accounting_contracts",
                columns: new[] { "tenant_id", "counterparty_ref" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_counterparties_tenant_id_tin",
                table: "accounting_counterparties",
                columns: new[] { "tenant_id", "tin" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_documents_tenant_id_counterparty_tin_date",
                table: "accounting_documents",
                columns: new[] { "tenant_id", "counterparty_tin", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_payment_orders_tenant_id_date",
                table: "accounting_payment_orders",
                columns: new[] { "tenant_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_accounting_settlement_balances_tenant_id_as_of_date_counter",
                table: "accounting_settlement_balances",
                columns: new[] { "tenant_id", "as_of_date", "counterparty_ref", "contract_ref", "gl_account_code", "currency" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_accounting_settlement_balances_tenant_id_counterparty_tin_a",
                table: "accounting_settlement_balances",
                columns: new[] { "tenant_id", "counterparty_tin", "as_of_date" });

            migrationBuilder.CreateIndex(
                name: "ix_ingest_batches_tenant_id_batch_id_sequence",
                table: "ingest_batches",
                columns: new[] { "tenant_id", "batch_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ingest_batches_tenant_id_connection_id",
                table: "ingest_batches",
                columns: new[] { "tenant_id", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ingest_batches_tenant_id_full_sync_id_entity_sequence",
                table: "ingest_batches",
                columns: new[] { "tenant_id", "full_sync_id", "entity", "sequence" },
                filter: "full_sync_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ingest_batches_tenant_id_ingest_key_id",
                table: "ingest_batches",
                columns: new[] { "tenant_id", "ingest_key_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ingest_batches_tenant_id_received_at",
                table: "ingest_batches",
                columns: new[] { "tenant_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ingest_full_sync_items_tenant_id_created_at",
                table: "ingest_full_sync_items",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ingest_keys_key_hash",
                table: "ingest_keys",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ingest_keys_tenant_id_connection_id",
                table: "ingest_keys",
                columns: new[] { "tenant_id", "connection_id" });

            ApplySecurity(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS synect_resolve_ingest_key(bytea);");
            foreach (var table in RecordTables)
                migrationBuilder.Sql($"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS fk_{table}_ingest_batches;");
            migrationBuilder.Sql("UPDATE connections SET adapter_kind = 'Accounting' WHERE adapter_kind = 'Accounting1C';");
            migrationBuilder.DropTable(
                name: "accounting_bank_account_balances");

            migrationBuilder.DropTable(
                name: "accounting_bank_accounts");

            migrationBuilder.DropTable(
                name: "accounting_bank_document_settlements");

            migrationBuilder.DropTable(
                name: "accounting_contracts");

            migrationBuilder.DropTable(
                name: "accounting_counterparties");

            migrationBuilder.DropTable(
                name: "accounting_document_lines");

            migrationBuilder.DropTable(
                name: "accounting_payment_orders");

            migrationBuilder.DropTable(
                name: "accounting_settlement_balances");

            migrationBuilder.DropTable(
                name: "ingest_batches");

            migrationBuilder.DropTable(
                name: "ingest_full_sync_items");

            migrationBuilder.DropTable(
                name: "accounting_bank_documents");

            migrationBuilder.DropTable(
                name: "accounting_documents");

            migrationBuilder.DropTable(
                name: "ingest_keys");
        }

        /// <summary>Tables of organization data created here: row-level security forced, like every tenant table.</summary>
        public static readonly string[] ForcedTables =
        [
            "accounting_counterparties", "accounting_contracts", "accounting_bank_accounts", "accounting_documents",
            "accounting_document_lines", "accounting_bank_documents", "accounting_bank_document_settlements",
            "accounting_payment_orders", "accounting_settlement_balances", "accounting_bank_account_balances",
            "ingest_batches", "ingest_full_sync_items",
        ];

        private static readonly string[] RecordTables =
        [
            "accounting_counterparties", "accounting_contracts", "accounting_bank_accounts", "accounting_documents",
            "accounting_bank_documents", "accounting_payment_orders", "accounting_settlement_balances", "accounting_bank_account_balances",
        ];

        /// <summary>SECURITY DEFINER functions of this migration the application role may execute.</summary>
        public static readonly string[] AppFunctions = ["synect_resolve_ingest_key(bytea)"];

        private const string TenantPredicate = "tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid";

        private static void ApplySecurity(MigrationBuilder migrationBuilder)
        {
            // The reserved kind "Accounting" was never used for a connection; 1C connections are "Accounting1C".
            migrationBuilder.Sql("UPDATE connections SET adapter_kind = 'Accounting1C' WHERE adapter_kind = 'Accounting';");

            // Records point at the package that last changed them (same organization). Created here rather than in the EF
            // model so that no supporting index is added to every record table.
            foreach (var table in RecordTables)
                migrationBuilder.Sql($"ALTER TABLE {table} ADD CONSTRAINT fk_{table}_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) " +
                                     "REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;");

            foreach (var table in ForcedTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"CREATE POLICY tenant_isolation ON {table} USING ({TenantPredicate}) WITH CHECK ({TenantPredicate});");
            }

            // ingest_keys like api_keys: RLS enabled for the application role but NOT forced, so that only the owner-defined
            // lookup function below can find a key before the organization is known.
            migrationBuilder.Sql("ALTER TABLE ingest_keys ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql($"CREATE POLICY tenant_isolation ON ingest_keys USING ({TenantPredicate}) WITH CHECK ({TenantPredicate});");
            migrationBuilder.Sql("ALTER TABLE ingest_keys ADD CONSTRAINT ck_ingest_keys_key_hash_length CHECK (octet_length(key_hash) = 32);");

            migrationBuilder.Sql("ALTER TABLE ingest_batches ADD CONSTRAINT ck_ingest_batches_status CHECK (status IN ('Processed', 'ProcessedWithErrors', 'Rejected', 'Failed'));");
            migrationBuilder.Sql("ALTER TABLE ingest_batches ADD CONSTRAINT ck_ingest_batches_counts CHECK (sequence >= 1 AND items_total >= 0 AND accepted >= 0 AND unchanged >= 0 AND rejected >= 0 AND marked_deleted >= 0);");
            migrationBuilder.Sql("ALTER TABLE accounting_documents ADD CONSTRAINT ck_accounting_documents_doc_kind CHECK (doc_kind IN ('Sales', 'Purchase'));");
            migrationBuilder.Sql("ALTER TABLE accounting_bank_documents ADD CONSTRAINT ck_accounting_bank_documents_direction CHECK (direction IN ('Receipt', 'Payment'));");
            foreach (var table in new[] { "accounting_counterparties", "accounting_contracts", "accounting_bank_accounts", "accounting_documents",
                         "accounting_bank_documents", "accounting_payment_orders" })
                migrationBuilder.Sql($"ALTER TABLE {table} ADD CONSTRAINT ck_{table}_ref CHECK (length(ref) BETWEEN 1 AND 64);");

            // Finds a usable ingest key by the SHA-256 of the presented key and returns only what the receiver needs. A key of an
            // organization disabled in its account (or of an inactive account) does not resolve, like an MCP key.
            migrationBuilder.Sql("""
                CREATE FUNCTION synect_resolve_ingest_key(p_key_hash bytea)
                RETURNS TABLE (ingest_key_id uuid, tenant_id uuid, connection_id uuid, tenant_tin text)
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
                        SELECT k.id, k.tenant_id, k.connection_id, t.tin::text
                        FROM public.ingest_keys k JOIN public.tenants t ON t.id = k.tenant_id
                        WHERE k.key_hash = p_key_hash AND k.revoked_at IS NULL
                          AND NOT EXISTS (
                                SELECT 1 FROM public.account_tenants at JOIN public.accounts a ON a.id = at.account_id
                                WHERE at.tenant_id = k.tenant_id AND (at.disabled_at IS NOT NULL OR a.status <> 'Active'));
                    IF FOUND THEN
                        -- At most one write per key and minute.
                        UPDATE public.ingest_keys SET last_used_at = now()
                        WHERE key_hash = p_key_hash AND (last_used_at IS NULL OR last_used_at < now() - interval '1 minute');
                    END IF;
                END
                $fn$;
                """);
            foreach (var function in AppFunctions)
            {
                migrationBuilder.Sql($"REVOKE ALL ON FUNCTION {function} FROM PUBLIC;");
                migrationBuilder.Sql($$"""
                    DO $do$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'synect_app') THEN
                            GRANT EXECUTE ON FUNCTION {{function}} TO synect_app;
                        END IF;
                    END
                    $do$;
                    """);
            }
        }
    }
}
