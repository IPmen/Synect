CREATE TABLE IF NOT EXISTS __ef_migrations_history (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE TABLE tenants (
        id uuid NOT NULL,
        name character varying(256) NOT NULL,
        inn character varying(16),
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_tenants PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE TABLE connections (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name character varying(256) NOT NULL,
        source_code character varying(64) NOT NULL,
        adapter_kind character varying(32) NOT NULL,
        status character varying(32) NOT NULL,
        status_reason character varying(2000),
        last_successful_sync_at timestamp with time zone,
        settings jsonb NOT NULL,
        credentials_ref character varying(256),
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_connections PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_connections_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE TABLE counterparties (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        inn character varying(16) NOT NULL,
        name character varying(512),
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_counterparties PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_counterparties_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE TABLE bank_accounts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        connection_id uuid NOT NULL,
        account_number character varying(34) NOT NULL,
        mfo character varying(16) NOT NULL,
        currency character varying(3) NOT NULL,
        name character varying(512),
        external_id character varying(256),
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_bank_accounts PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_bank_accounts_connections_tenant_id_connection_id FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_bank_accounts_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE TABLE edo_documents (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        connection_id uuid NOT NULL,
        external_id character varying(128) NOT NULL,
        doc_type character varying(16) NOT NULL,
        status character varying(16) NOT NULL,
        owner character varying(16) NOT NULL,
        number character varying(256),
        document_date date,
        counterparty_inn character varying(16),
        counterparty_name character varying(512),
        contract_number character varying(256),
        contract_date date,
        total_sum bigint NOT NULL,
        total_vat_sum bigint NOT NULL,
        total_delivery_sum bigint NOT NULL,
        total_delivery_sum_with_vat bigint NOT NULL,
        source_created_at timestamp with time zone,
        source_updated_at timestamp with time zone,
        source_hash character(64) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_edo_documents PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_edo_documents_connections_tenant_id_connection_id FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_edo_documents_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE TABLE raw_objects (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        connection_id uuid NOT NULL,
        source_code character varying(64) NOT NULL,
        object_type character varying(128) NOT NULL,
        external_id character varying(256) NOT NULL,
        payload jsonb NOT NULL,
        hash character(64) NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_raw_objects PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_raw_objects_connections_tenant_id_connection_id FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_raw_objects_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE TABLE sync_runs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        connection_id uuid NOT NULL,
        started_at timestamp with time zone NOT NULL,
        finished_at timestamp with time zone,
        result character varying(32) NOT NULL,
        is_complete boolean NOT NULL,
        record_count integer NOT NULL,
        error character varying(2000),
        range_from date,
        range_to date,
        CONSTRAINT pk_sync_runs PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_sync_runs_connections_tenant_id_connection_id FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_sync_runs_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE TABLE bank_daily_balances (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        bank_account_id uuid NOT NULL,
        date date NOT NULL,
        opening_balance bigint NOT NULL,
        closing_balance bigint NOT NULL,
        total_debit bigint NOT NULL,
        total_credit bigint NOT NULL,
        is_day_closed boolean NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_bank_daily_balances PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_bank_daily_balances_bank_accounts_tenant_id_bank_account_id FOREIGN KEY (tenant_id, bank_account_id) REFERENCES bank_accounts (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_bank_daily_balances_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE TABLE bank_transactions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        connection_id uuid NOT NULL,
        bank_account_id uuid NOT NULL,
        operation_id character varying(128) NOT NULL,
        amount bigint NOT NULL,
        currency character varying(3) NOT NULL,
        direction character varying(16) NOT NULL,
        counterparty_inn character varying(16),
        counterparty_name character varying(512),
        counterparty_account character varying(34),
        counterparty_mfo character varying(16),
        purpose text,
        purpose_code character varying(16),
        document_number character varying(64),
        posting_date date NOT NULL,
        value_date date,
        is_final boolean NOT NULL,
        source_state character varying(32),
        source_hash character(64) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_bank_transactions PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_bank_transactions_bank_accounts_tenant_id_bank_account_id FOREIGN KEY (tenant_id, bank_account_id) REFERENCES bank_accounts (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_bank_transactions_connections_tenant_id_connection_id FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_bank_transactions_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE TABLE edo_document_status_logs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        edo_document_id uuid NOT NULL,
        status character varying(16) NOT NULL,
        recorded_at timestamp with time zone NOT NULL,
        source_updated_at timestamp with time zone,
        sync_run_id uuid,
        CONSTRAINT pk_edo_document_status_logs PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_edo_document_status_logs_edo_documents_tenant_id_edo_docume FOREIGN KEY (tenant_id, edo_document_id) REFERENCES edo_documents (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_edo_document_status_logs_sync_runs_tenant_id_sync_run_id FOREIGN KEY (tenant_id, sync_run_id) REFERENCES sync_runs (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_edo_document_status_logs_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_bank_accounts_tenant_id_connection_id_account_number ON bank_accounts (tenant_id, connection_id, account_number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_bank_daily_balances_tenant_id_bank_account_id_date ON bank_daily_balances (tenant_id, bank_account_id, date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_bank_transactions_tenant_id_bank_account_id_operation_id ON bank_transactions (tenant_id, bank_account_id, operation_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE INDEX ix_bank_transactions_tenant_id_bank_account_id_posting_date ON bank_transactions (tenant_id, bank_account_id, posting_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE INDEX ix_bank_transactions_tenant_id_connection_id ON bank_transactions (tenant_id, connection_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE INDEX ix_bank_transactions_tenant_id_counterparty_inn ON bank_transactions (tenant_id, counterparty_inn);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE INDEX ix_connections_tenant_id_source_code ON connections (tenant_id, source_code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_counterparties_tenant_id_inn ON counterparties (tenant_id, inn);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE INDEX ix_edo_document_status_logs_tenant_id_edo_document_id_recorded ON edo_document_status_logs (tenant_id, edo_document_id, recorded_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE INDEX ix_edo_document_status_logs_tenant_id_sync_run_id ON edo_document_status_logs (tenant_id, sync_run_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_edo_documents_tenant_id_connection_id_external_id ON edo_documents (tenant_id, connection_id, external_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE INDEX ix_edo_documents_tenant_id_counterparty_inn ON edo_documents (tenant_id, counterparty_inn);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE INDEX ix_edo_documents_tenant_id_document_date ON edo_documents (tenant_id, document_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE UNIQUE INDEX ix_raw_objects_tenant_id_connection_id_object_type_external_id ON raw_objects (tenant_id, connection_id, object_type, external_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    CREATE INDEX ix_sync_runs_tenant_id_connection_id_started_at ON sync_runs (tenant_id, connection_id, started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185511_InitialCreate') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261107185511_InitialCreate', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE connections ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE connections FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON connections USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE sync_runs ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE sync_runs FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON sync_runs USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE raw_objects ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE raw_objects FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON raw_objects USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE bank_accounts ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE bank_accounts FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON bank_accounts USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE bank_transactions ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE bank_transactions FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON bank_transactions USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE bank_daily_balances ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE bank_daily_balances FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON bank_daily_balances USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE edo_documents ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE edo_documents FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON edo_documents USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE edo_document_status_logs ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE edo_document_status_logs FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON edo_document_status_logs USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE counterparties ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    ALTER TABLE counterparties FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    CREATE POLICY tenant_isolation ON counterparties USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    CREATE FUNCTION edo_document_status_logs_append_only() RETURNS trigger
    LANGUAGE plpgsql AS $$
    BEGIN
        RAISE EXCEPTION 'edo_document_status_logs is append-only';
    END;
    $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    CREATE TRIGGER trg_edo_document_status_logs_append_only
    BEFORE UPDATE OR DELETE ON edo_document_status_logs
    FOR EACH ROW EXECUTE FUNCTION edo_document_status_logs_append_only();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107185537_RowLevelSecurity') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261107185537_RowLevelSecurity', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107193644_BankTransactionErrors') THEN
    ALTER TABLE bank_transactions ADD error_code integer;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107193644_BankTransactionErrors') THEN
    ALTER TABLE bank_transactions ADD error_message character varying(1000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107193644_BankTransactionErrors') THEN
    ALTER TABLE bank_transactions ADD has_error boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261107193644_BankTransactionErrors') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261107193644_BankTransactionErrors', '10.0.14');
    END IF;
END $EF$;
COMMIT;

