using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Synect.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountsStage65 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tool_audit_log_api_keys_tenant_id_api_key_id",
                table: "tool_audit_log");

            migrationBuilder.DropPrimaryKey(
                name: "pk_api_keys",
                table: "api_keys");

            migrationBuilder.RenameColumn(
                name: "inn",
                table: "tenants",
                newName: "tin");

            migrationBuilder.AddColumn<Guid>(
                name: "account_id",
                table: "tool_audit_log",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "onboarding_note",
                table: "connections",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "onboarding_status",
                table: "connections",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NotStarted");

            migrationBuilder.AlterColumn<Guid>(
                name: "tenant_id",
                table: "api_keys",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "account_id",
                table: "api_keys",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_api_keys",
                table: "api_keys",
                column: "id");

            migrationBuilder.CreateTable(
                name: "accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_accounts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_key_tenants",
                columns: table => new
                {
                    key_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_key_tenants", x => new { x.key_id, x.tenant_id });
                    table.ForeignKey(
                        name: "fk_api_key_tenants_api_keys_key_id",
                        column: x => x.key_id,
                        principalTable: "api_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_api_key_tenants_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "account_tenants",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_tenants", x => new { x.account_id, x.tenant_id });
                    table.ForeignKey(
                        name: "fk_account_tenants_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_account_tenants_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tool_audit_log_account_id",
                table: "tool_audit_log",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_tool_audit_log_api_key_id",
                table: "tool_audit_log",
                column: "api_key_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_account_id",
                table: "api_keys",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_tenant_id",
                table: "api_keys",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_tenants_tenant_id",
                table: "account_tenants",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_accounts_name",
                table: "accounts",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_key_tenants_tenant_id",
                table: "api_key_tenants",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "fk_api_keys_accounts_account_id",
                table: "api_keys",
                column: "account_id",
                principalTable: "accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tool_audit_log_accounts_account_id",
                table: "tool_audit_log",
                column: "account_id",
                principalTable: "accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_tool_audit_log_api_keys_api_key_id",
                table: "tool_audit_log",
                column: "api_key_id",
                principalTable: "api_keys",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            AddAccountSql(migrationBuilder);
        }

        /// <summary>
        /// Stage 6.5 SQL: checks, row-level security of the account tables, account_tool_audit_log, the TIN uniqueness
        /// trigger and the SECURITY DEFINER functions that are the only way for the application role to reach account data.
        /// </summary>
        private static void AddAccountSql(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE tenants ADD CONSTRAINT ck_tenants_tin CHECK (tin IS NULL OR tin ~ '^([0-9]{9}|[0-9]{14})$');");
            migrationBuilder.Sql("ALTER TABLE api_keys ADD CONSTRAINT ck_api_keys_owner CHECK ((tenant_id IS NULL) <> (account_id IS NULL));");
            migrationBuilder.Sql("ALTER TABLE accounts ADD CONSTRAINT ck_accounts_status CHECK (status IN ('Active', 'Suspended'));");
            migrationBuilder.Sql("ALTER TABLE connections ADD CONSTRAINT ck_connections_onboarding_status " +
                "CHECK (onboarding_status IN ('NotStarted', 'WaitingForAccess', 'Connected', 'NeedsAttention'));");

            // Calls of an account key that did not resolve to exactly one organization (a missing or ambiguous
            // 'organization', several organizations at once). Written only by synect_write_account_tool_audit.
            migrationBuilder.Sql("""
                CREATE TABLE account_tool_audit_log (
                    id uuid PRIMARY KEY,
                    account_id uuid NOT NULL REFERENCES accounts (id) ON DELETE RESTRICT,
                    api_key_id uuid NOT NULL REFERENCES api_keys (id) ON DELETE RESTRICT,
                    tool varchar(64) NOT NULL,
                    parameters jsonb NOT NULL,
                    started_at timestamptz NOT NULL,
                    duration_ms integer NOT NULL,
                    row_count integer NULL,
                    status varchar(32) NOT NULL CHECK (status IN ('Ok', 'Error', 'RateLimited', 'Timeout')),
                    error_message varchar(2000) NULL,
                    details jsonb NULL,
                    tenant_ids uuid[] NOT NULL DEFAULT '{}'
                );
                CREATE INDEX ix_account_tool_audit_log_account_id_started_at ON account_tool_audit_log (account_id, started_at);
                CREATE INDEX ix_account_tool_audit_log_api_key_id ON account_tool_audit_log (api_key_id);
                """);

            // The account tables hold no organization data, but who serves whom is confidential between accounts:
            // row-level security is enabled WITHOUT a policy (any role other than the owner sees and changes nothing),
            // and the grants that the default privileges give synect_app are revoked. Not forced: the owner (DevTools
            // administration, the SECURITY DEFINER functions below) works with them.
            foreach (var table in new[] { "accounts", "account_tenants", "api_key_tenants", "account_tool_audit_log" })
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"REVOKE ALL ON {table} FROM PUBLIC;");
            }
            migrationBuilder.Sql("""
                DO $do$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'synect_app') THEN
                        REVOKE ALL ON accounts, account_tenants, api_key_tenants, account_tool_audit_log FROM synect_app;
                    END IF;
                END
                $do$;
                """);

            // TIN unique within an account. The TIN lives on tenants and the membership on account_tenants, so a unique
            // index cannot express it; both sides check under a per-account advisory lock (concurrent inserts serialize).
            migrationBuilder.Sql("""
                CREATE FUNCTION synect_check_account_tin()
                RETURNS trigger
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $fn$
                DECLARE
                    v_account uuid;
                    v_tenant uuid;
                    v_tin text;
                BEGIN
                    IF TG_TABLE_NAME = 'account_tenants' THEN
                        v_account := NEW.account_id;
                        v_tenant := NEW.tenant_id;
                        SELECT t.tin INTO v_tin FROM public.tenants t WHERE t.id = NEW.tenant_id;
                    ELSE
                        v_tenant := NEW.id;
                        v_tin := NEW.tin;
                        SELECT at.account_id INTO v_account FROM public.account_tenants at WHERE at.tenant_id = NEW.id;
                    END IF;
                    IF v_tin IS NULL OR v_account IS NULL THEN
                        RETURN NEW;
                    END IF;
                    PERFORM pg_advisory_xact_lock(hashtextextended('synect_account_tin:' || v_account::text, 0));
                    IF EXISTS (SELECT 1 FROM public.account_tenants at JOIN public.tenants t ON t.id = at.tenant_id
                               WHERE at.account_id = v_account AND at.tenant_id <> v_tenant AND t.tin = v_tin) THEN
                        RAISE EXCEPTION 'TIN % is already used by another organization of this account', v_tin
                            USING ERRCODE = '23505', CONSTRAINT = 'ux_account_tenants_tin';
                    END IF;
                    RETURN NEW;
                END
                $fn$;
                CREATE TRIGGER trg_account_tenants_tin BEFORE INSERT OR UPDATE OF account_id, tenant_id ON account_tenants
                    FOR EACH ROW EXECUTE FUNCTION synect_check_account_tin();
                CREATE TRIGGER trg_tenants_tin BEFORE UPDATE OF tin ON tenants
                    FOR EACH ROW EXECUTE FUNCTION synect_check_account_tin();
                REVOKE ALL ON FUNCTION synect_check_account_tin() FROM PUBLIC;
                """);

            // Key check, now for single-organization and account keys. Returns only (id, account, tenant, scopes).
            // A revoked key, a key of a suspended account, and a single-organization key of a disabled organization
            // (or of a suspended account) return nothing.
            migrationBuilder.Sql("DROP FUNCTION synect_resolve_api_key(bytea);");
            migrationBuilder.Sql("""
                CREATE FUNCTION synect_resolve_api_key(p_key_hash bytea)
                RETURNS TABLE (api_key_id uuid, account_id uuid, tenant_id uuid, scopes text[])
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
                        SELECT k.id, k.account_id, k.tenant_id, k.scopes FROM public.api_keys k
                        WHERE k.key_hash = p_key_hash AND k.revoked_at IS NULL
                          AND ((k.tenant_id IS NOT NULL AND NOT EXISTS (
                                    SELECT 1 FROM public.account_tenants at JOIN public.accounts a ON a.id = at.account_id
                                    WHERE at.tenant_id = k.tenant_id AND (at.disabled_at IS NOT NULL OR a.status <> 'Active')))
                            OR (k.account_id IS NOT NULL AND EXISTS (
                                    SELECT 1 FROM public.accounts a WHERE a.id = k.account_id AND a.status = 'Active')));
                    IF FOUND THEN
                        -- At most one write per key and minute.
                        UPDATE public.api_keys SET last_used_at = now()
                        WHERE key_hash = p_key_hash AND (last_used_at IS NULL OR last_used_at < now() - interval '1 minute');
                    END IF;
                END
                $fn$;
                """);

            // Organizations a key may work with: the key's own organization, or the non-disabled organizations of the
            // key's (active) account, narrowed to api_key_tenants when the key has rows there. Takes the key hash, not
            // the key id, so the caller must hold the key itself. Returns only (tenant_id, name, tin).
            migrationBuilder.Sql("""
                CREATE FUNCTION synect_api_key_organizations(p_key_hash bytea)
                RETURNS TABLE (tenant_id uuid, name text, tin text)
                LANGUAGE sql
                STABLE
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $fn$
                    WITH k AS (
                        SELECT k.id, k.tenant_id, k.account_id FROM public.api_keys k
                        WHERE p_key_hash IS NOT NULL AND octet_length(p_key_hash) = 32
                          AND k.key_hash = p_key_hash AND k.revoked_at IS NULL)
                    SELECT t.id, COALESCE(at.display_name, t.name)::text, t.tin::text
                    FROM k
                    JOIN public.tenants t ON t.id = k.tenant_id
                    LEFT JOIN public.account_tenants at ON at.tenant_id = t.id
                    LEFT JOIN public.accounts a ON a.id = at.account_id
                    WHERE at.tenant_id IS NULL OR (at.disabled_at IS NULL AND a.status = 'Active')
                    UNION ALL
                    SELECT t.id, COALESCE(at.display_name, t.name)::text, t.tin::text
                    FROM k
                    JOIN public.accounts a ON a.id = k.account_id AND a.status = 'Active'
                    JOIN public.account_tenants at ON at.account_id = a.id AND at.disabled_at IS NULL
                    JOIN public.tenants t ON t.id = at.tenant_id
                    WHERE NOT EXISTS (SELECT 1 FROM public.api_key_tenants kt WHERE kt.key_id = k.id)
                       OR EXISTS (SELECT 1 FROM public.api_key_tenants kt WHERE kt.key_id = k.id AND kt.tenant_id = t.id)
                $fn$;
                """);

            // Journal row of an account key call that did not resolve to one organization. The key and account are
            // taken from the hash (the caller cannot write rows of another account); listed organizations must belong
            // to that account.
            migrationBuilder.Sql("""
                CREATE FUNCTION synect_write_account_tool_audit(
                    p_key_hash bytea, p_tool text, p_parameters jsonb, p_started_at timestamptz, p_duration_ms integer,
                    p_row_count integer, p_status text, p_error_message text, p_details jsonb, p_tenant_ids uuid[])
                RETURNS boolean
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $fn$
                DECLARE
                    v_key uuid;
                    v_account uuid;
                BEGIN
                    IF p_key_hash IS NULL OR octet_length(p_key_hash) <> 32 THEN
                        RETURN false;
                    END IF;
                    SELECT k.id, k.account_id INTO v_key, v_account FROM public.api_keys k
                    WHERE k.key_hash = p_key_hash AND k.account_id IS NOT NULL;
                    IF v_key IS NULL THEN
                        RETURN false;
                    END IF;
                    IF EXISTS (SELECT 1 FROM unnest(COALESCE(p_tenant_ids, '{}'::uuid[])) x(id)
                               WHERE NOT EXISTS (SELECT 1 FROM public.account_tenants at
                                                 WHERE at.account_id = v_account AND at.tenant_id = x.id)) THEN
                        RAISE EXCEPTION 'organization outside the account' USING ERRCODE = '42501';
                    END IF;
                    INSERT INTO public.account_tool_audit_log
                        (id, account_id, api_key_id, tool, parameters, started_at, duration_ms, row_count, status, error_message, details, tenant_ids)
                    VALUES (gen_random_uuid(), v_account, v_key, left(p_tool, 64), COALESCE(p_parameters, '{}'::jsonb), p_started_at,
                            GREATEST(p_duration_ms, 0), p_row_count, p_status, left(p_error_message, 2000), p_details,
                            COALESCE(p_tenant_ids, '{}'::uuid[]));
                    RETURN true;
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

        /// <summary>SECURITY DEFINER functions the application role may execute (tests grant them to their own role).</summary>
        public static readonly string[] AppFunctions =
        [
            "synect_resolve_api_key(bytea)",
            "synect_api_key_organizations(bytea)",
            "synect_write_account_tool_audit(bytea, text, jsonb, timestamptz, integer, integer, text, text, jsonb, uuid[])",
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS synect_write_account_tool_audit(bytea, text, jsonb, timestamptz, integer, integer, text, text, jsonb, uuid[]);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS synect_api_key_organizations(bytea);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS synect_resolve_api_key(bytea);");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_tenants_tin ON tenants;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_account_tenants_tin ON account_tenants;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS synect_check_account_tin();");
            migrationBuilder.Sql("DROP TABLE IF EXISTS account_tool_audit_log;");
            migrationBuilder.Sql("ALTER TABLE connections DROP CONSTRAINT IF EXISTS ck_connections_onboarding_status;");
            migrationBuilder.Sql("ALTER TABLE api_keys DROP CONSTRAINT IF EXISTS ck_api_keys_owner;");
            migrationBuilder.Sql("ALTER TABLE tenants DROP CONSTRAINT IF EXISTS ck_tenants_tin;");
            // Account keys cannot exist without accounts.
            migrationBuilder.Sql("DELETE FROM api_key_tenants;");
            migrationBuilder.Sql("DELETE FROM api_keys WHERE tenant_id IS NULL;");

            migrationBuilder.DropForeignKey(
                name: "fk_api_keys_accounts_account_id",
                table: "api_keys");

            migrationBuilder.DropForeignKey(
                name: "fk_tool_audit_log_accounts_account_id",
                table: "tool_audit_log");

            migrationBuilder.DropForeignKey(
                name: "fk_tool_audit_log_api_keys_api_key_id",
                table: "tool_audit_log");

            migrationBuilder.DropTable(
                name: "account_tenants");

            migrationBuilder.DropTable(
                name: "api_key_tenants");

            migrationBuilder.DropTable(
                name: "accounts");

            migrationBuilder.DropIndex(
                name: "ix_tool_audit_log_account_id",
                table: "tool_audit_log");

            migrationBuilder.DropIndex(
                name: "ix_tool_audit_log_api_key_id",
                table: "tool_audit_log");

            migrationBuilder.DropPrimaryKey(
                name: "pk_api_keys",
                table: "api_keys");

            migrationBuilder.DropIndex(
                name: "ix_api_keys_account_id",
                table: "api_keys");

            migrationBuilder.DropIndex(
                name: "ix_api_keys_tenant_id",
                table: "api_keys");

            migrationBuilder.DropColumn(
                name: "account_id",
                table: "tool_audit_log");

            migrationBuilder.DropColumn(
                name: "onboarding_note",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "onboarding_status",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "account_id",
                table: "api_keys");

            migrationBuilder.RenameColumn(
                name: "tin",
                table: "tenants",
                newName: "inn");

            migrationBuilder.AlterColumn<Guid>(
                name: "tenant_id",
                table: "api_keys",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_api_keys",
                table: "api_keys",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "fk_tool_audit_log_api_keys_tenant_id_api_key_id",
                table: "tool_audit_log",
                columns: new[] { "tenant_id", "api_key_id" },
                principalTable: "api_keys",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

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
    }
}
