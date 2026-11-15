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

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    ALTER TABLE connections ADD consecutive_failures integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    ALTER TABLE connections ADD status_changed_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    CREATE TABLE connection_secrets (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        connection_id uuid NOT NULL,
        kind character varying(64) NOT NULL,
        encrypted_value text NOT NULL,
        version integer NOT NULL,
        created_at timestamp with time zone NOT NULL,
        rotated_at timestamp with time zone,
        CONSTRAINT pk_connection_secrets PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_connection_secrets_connections_tenant_id_connection_id FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_connection_secrets_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    CREATE TABLE sync_jobs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        connection_id uuid NOT NULL,
        kind character varying(16) NOT NULL,
        priority integer NOT NULL,
        status character varying(16) NOT NULL,
        scheduled_for timestamp with time zone,
        enqueued_at timestamp with time zone NOT NULL,
        not_before timestamp with time zone NOT NULL,
        started_at timestamp with time zone,
        finished_at timestamp with time zone,
        sync_run_id uuid,
        error character varying(2000),
        CONSTRAINT pk_sync_jobs PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_sync_jobs_connections_tenant_id_connection_id FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_sync_jobs_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    CREATE UNIQUE INDEX ix_connection_secrets_tenant_id_connection_id_kind ON connection_secrets (tenant_id, connection_id, kind);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    CREATE INDEX ix_sync_jobs_tenant_id_connection_id_enqueued_at ON sync_jobs (tenant_id, connection_id, enqueued_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    CREATE INDEX ix_sync_jobs_tenant_id_status_not_before ON sync_jobs (tenant_id, status, not_before);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    CREATE UNIQUE INDEX ux_sync_jobs_tenant_id_connection_id_active ON sync_jobs (tenant_id, connection_id) WHERE status IN ('Queued', 'Running');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    ALTER TABLE sync_jobs ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    ALTER TABLE sync_jobs FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    CREATE POLICY tenant_isolation ON sync_jobs USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    ALTER TABLE connection_secrets ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    ALTER TABLE connection_secrets FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    CREATE POLICY tenant_isolation ON connection_secrets USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108173337_QueueSecretsConnectionStates') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261108173337_QueueSecretsConnectionStates', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108183931_SyncJobHeartbeat') THEN
    ALTER TABLE sync_jobs ADD heartbeat_at timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108183931_SyncJobHeartbeat') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261108183931_SyncJobHeartbeat', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108185807_AnalyticsStage3') THEN
    ALTER TABLE edo_documents ADD status_comment character varying(2000);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108185807_AnalyticsStage3') THEN
    CREATE TABLE analytics_counterparty_monthly (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        counterparty_inn character varying(16) NOT NULL,
        month date NOT NULL,
        currency character varying(3) NOT NULL,
        bank_incoming bigint NOT NULL,
        bank_outgoing bigint NOT NULL,
        bank_count integer NOT NULL,
        edo_incoming_total bigint NOT NULL,
        edo_outgoing_total bigint NOT NULL,
        edo_incoming_count integer NOT NULL,
        edo_outgoing_count integer NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_analytics_counterparty_monthly PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_analytics_counterparty_monthly_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108185807_AnalyticsStage3') THEN
    CREATE UNIQUE INDEX ix_analytics_counterparty_monthly_tenant_id_counterparty_inn_m ON analytics_counterparty_monthly (tenant_id, counterparty_inn, month, currency);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108185807_AnalyticsStage3') THEN
    CREATE INDEX ix_analytics_counterparty_monthly_tenant_id_month_currency ON analytics_counterparty_monthly (tenant_id, month, currency);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108185807_AnalyticsStage3') THEN
    ALTER TABLE analytics_counterparty_monthly ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108185807_AnalyticsStage3') THEN
    ALTER TABLE analytics_counterparty_monthly FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108185807_AnalyticsStage3') THEN
    CREATE POLICY tenant_isolation ON analytics_counterparty_monthly USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108185807_AnalyticsStage3') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261108185807_AnalyticsStage3', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    DROP INDEX ix_edo_documents_tenant_id_counterparty_inn;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    DROP INDEX ix_bank_transactions_tenant_id_counterparty_inn;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    CREATE INDEX ix_edo_documents_tenant_id_counterparty_inn ON edo_documents (tenant_id, counterparty_inn) INCLUDE (status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    CREATE INDEX ix_edo_documents_tenant_id_owner_status_document_date ON edo_documents (tenant_id, owner, status, document_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    CREATE INDEX ix_edo_documents_tenant_id_source_created_at ON edo_documents (tenant_id, source_created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    CREATE INDEX ix_edo_documents_tenant_id_source_updated_at ON edo_documents (tenant_id, source_updated_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    CREATE INDEX ix_bank_transactions_flows ON bank_transactions (tenant_id, posting_date) INCLUDE (bank_account_id, currency, direction, amount, counterparty_inn) WHERE has_error = false;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    CREATE INDEX ix_bank_transactions_tenant_id_counterparty_inn_posting_date ON bank_transactions (tenant_id, counterparty_inn, posting_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    CREATE INDEX ix_bank_daily_balances_tenant_id_date ON bank_daily_balances (tenant_id, date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    CREATE INDEX ix_edo_documents_tenant_id_number_prefix ON edo_documents (tenant_id, number text_pattern_ops);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    CREATE INDEX ix_edo_documents_tenant_id_contract_number_prefix ON edo_documents (tenant_id, contract_number text_pattern_ops);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261108191349_AnalyticsIndexes') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261108191349_AnalyticsIndexes', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    CREATE TABLE api_keys (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name character varying(256) NOT NULL,
        key_hash bytea NOT NULL,
        key_prefix character varying(32) NOT NULL,
        scopes text[] NOT NULL,
        created_at timestamp with time zone NOT NULL,
        last_used_at timestamp with time zone,
        revoked_at timestamp with time zone,
        CONSTRAINT pk_api_keys PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_api_keys_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    CREATE TABLE tool_audit_log (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        api_key_id uuid NOT NULL,
        tool character varying(64) NOT NULL,
        parameters jsonb NOT NULL,
        started_at timestamp with time zone NOT NULL,
        duration_ms integer NOT NULL,
        row_count integer,
        status character varying(32) NOT NULL,
        error_message character varying(2000),
        CONSTRAINT pk_tool_audit_log PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_tool_audit_log_api_keys_tenant_id_api_key_id FOREIGN KEY (tenant_id, api_key_id) REFERENCES api_keys (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_tool_audit_log_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    CREATE UNIQUE INDEX ix_api_keys_key_hash ON api_keys (key_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    CREATE INDEX ix_tool_audit_log_tenant_id_api_key_id ON tool_audit_log (tenant_id, api_key_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    CREATE INDEX ix_tool_audit_log_tenant_id_started_at ON tool_audit_log (tenant_id, started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    ALTER TABLE api_keys ADD CONSTRAINT ck_api_keys_key_hash_length CHECK (octet_length(key_hash) = 32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    ALTER TABLE api_keys ADD CONSTRAINT ck_api_keys_scopes CHECK (scopes <@ ARRAY['read', 'sync_request']::text[]);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    ALTER TABLE tool_audit_log ADD CONSTRAINT ck_tool_audit_log_status CHECK (status IN ('Ok', 'Error', 'RateLimited', 'Timeout'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    ALTER TABLE tool_audit_log ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    ALTER TABLE tool_audit_log FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    CREATE POLICY tenant_isolation ON tool_audit_log USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    ALTER TABLE api_keys ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    CREATE POLICY tenant_isolation ON api_keys USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    REVOKE ALL ON FUNCTION synect_resolve_api_key(bytea) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    DO $do$
    BEGIN
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'synect_app') THEN
            GRANT EXECUTE ON FUNCTION synect_resolve_api_key(bytea) TO synect_app;
        END IF;
    END
    $do$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109164934_McpAccessStage4') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261109164934_McpAccessStage4', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109181830_CounterpartyAliases') THEN
    CREATE EXTENSION IF NOT EXISTS pg_trgm;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109181830_CounterpartyAliases') THEN
    CREATE EXTENSION IF NOT EXISTS btree_gin;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109181830_CounterpartyAliases') THEN
    CREATE TABLE counterparty_aliases (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        counterparty_inn character varying(16) NOT NULL,
        name character varying(500) NOT NULL,
        normalized_name character varying(500) NOT NULL,
        source character varying(16) NOT NULL,
        occurrences integer NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_counterparty_aliases PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_counterparty_aliases_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109181830_CounterpartyAliases') THEN
    CREATE UNIQUE INDEX ix_counterparty_aliases_tenant_id_counterparty_inn_source_name ON counterparty_aliases (tenant_id, counterparty_inn, source, name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109181830_CounterpartyAliases') THEN
    ALTER TABLE counterparty_aliases ADD CONSTRAINT ck_counterparty_aliases_source CHECK (source IN ('Bank', 'Edo'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109181830_CounterpartyAliases') THEN
    CREATE INDEX ix_counterparty_aliases_tenant_id_normalized_name_trgm ON counterparty_aliases USING gin (tenant_id, normalized_name gin_trgm_ops);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109181830_CounterpartyAliases') THEN
    ALTER TABLE counterparty_aliases ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109181830_CounterpartyAliases') THEN
    ALTER TABLE counterparty_aliases FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109181830_CounterpartyAliases') THEN
    CREATE POLICY tenant_isolation ON counterparty_aliases USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261109181830_CounterpartyAliases') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261109181830_CounterpartyAliases', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE edo_documents ADD currency character varying(3) NOT NULL DEFAULT 'UZS';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE edo_documents ADD seller_account character varying(34);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE TABLE reconciliation_runs (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        started_at timestamp with time zone NOT NULL,
        finished_at timestamp with time zone,
        scope character varying(16) NOT NULL,
        scope_inns text[],
        match_count integer NOT NULL,
        created_count integer NOT NULL,
        removed_count integer NOT NULL,
        rules_version character varying(64) NOT NULL,
        CONSTRAINT pk_reconciliation_runs PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_reconciliation_runs_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE TABLE reconciliation_findings (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        run_id uuid NOT NULL,
        kind character varying(32) NOT NULL,
        counterparty_inn character varying(16) NOT NULL,
        subject_kind character varying(16) NOT NULL,
        subject_id uuid NOT NULL,
        related_kind character varying(16),
        related_id uuid,
        confidence integer NOT NULL,
        reasons jsonb NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_reconciliation_findings PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_reconciliation_findings_reconciliation_runs_tenant_id_run_id FOREIGN KEY (tenant_id, run_id) REFERENCES reconciliation_runs (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_reconciliation_findings_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE TABLE reconciliation_matches (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        run_id uuid NOT NULL,
        match_type character varying(16) NOT NULL,
        status character varying(16) NOT NULL,
        confidence integer NOT NULL,
        reasons jsonb NOT NULL,
        counterparty_inn character varying(16) NOT NULL,
        currency character varying(3) NOT NULL,
        document_owner character varying(16) NOT NULL,
        payments_total bigint NOT NULL,
        documents_total bigint NOT NULL,
        signature character varying(64) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        decided_at timestamp with time zone,
        decided_by_key uuid,
        decision_comment character varying(1000),
        CONSTRAINT pk_reconciliation_matches PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_reconciliation_matches_reconciliation_runs_tenant_id_run_id FOREIGN KEY (tenant_id, run_id) REFERENCES reconciliation_runs (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_reconciliation_matches_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE TABLE reconciliation_match_items (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        match_id uuid NOT NULL,
        kind character varying(16) NOT NULL,
        ref_id uuid NOT NULL,
        allocated_amount_minor bigint NOT NULL,
        match_status character varying(16) NOT NULL,
        CONSTRAINT pk_reconciliation_match_items PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_reconciliation_match_items_reconciliation_matches_tenant_id FOREIGN KEY (tenant_id, match_id) REFERENCES reconciliation_matches (tenant_id, id) ON DELETE CASCADE,
        CONSTRAINT fk_reconciliation_match_items_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE INDEX ix_reconciliation_findings_tenant_id_counterparty_inn ON reconciliation_findings (tenant_id, counterparty_inn);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE INDEX ix_reconciliation_findings_tenant_id_kind_subject_id ON reconciliation_findings (tenant_id, kind, subject_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE INDEX ix_reconciliation_findings_tenant_id_run_id ON reconciliation_findings (tenant_id, run_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE UNIQUE INDEX ix_reconciliation_match_items_tenant_id_match_id_kind_ref_id ON reconciliation_match_items (tenant_id, match_id, kind, ref_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE UNIQUE INDEX ux_reconciliation_match_items_proposed ON reconciliation_match_items (tenant_id, kind, ref_id) WHERE match_status = 'Proposed';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE INDEX ix_reconciliation_matches_tenant_id_counterparty_inn_status ON reconciliation_matches (tenant_id, counterparty_inn, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE INDEX ix_reconciliation_matches_tenant_id_run_id ON reconciliation_matches (tenant_id, run_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE INDEX ix_reconciliation_matches_tenant_id_status_match_type ON reconciliation_matches (tenant_id, status, match_type);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE INDEX ix_reconciliation_runs_tenant_id_started_at ON reconciliation_runs (tenant_id, started_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_runs ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_runs FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE POLICY tenant_isolation ON reconciliation_runs USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_matches ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_matches FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE POLICY tenant_isolation ON reconciliation_matches USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_match_items ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_match_items FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE POLICY tenant_isolation ON reconciliation_match_items USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_findings ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_findings FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    CREATE POLICY tenant_isolation ON reconciliation_findings USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_matches ADD CONSTRAINT ck_reconciliation_matches_confidence CHECK (confidence BETWEEN 0 AND 100);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_matches ADD CONSTRAINT ck_reconciliation_matches_status CHECK (status IN ('Proposed', 'Confirmed', 'Rejected'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_matches ADD CONSTRAINT ck_reconciliation_matches_type CHECK (match_type IN ('Exact', 'OneToMany', 'ManyToOne', 'Partial'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_match_items ADD CONSTRAINT ck_reconciliation_match_items_kind CHECK (kind IN ('BankTransaction', 'EdoDocument'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE reconciliation_match_items ADD CONSTRAINT ck_reconciliation_match_items_amount CHECK (allocated_amount_minor >= 0);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE api_keys DROP CONSTRAINT ck_api_keys_scopes;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    ALTER TABLE api_keys ADD CONSTRAINT ck_api_keys_scopes CHECK (scopes <@ ARRAY['read', 'sync_request', 'review']::text[]);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110031802_ReconciliationStage5') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261110031802_ReconciliationStage5', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110085407_ReversibleMatchDecisions') THEN
    ALTER TABLE tool_audit_log ADD details jsonb;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110085407_ReversibleMatchDecisions') THEN
    CREATE TABLE reconciliation_match_decisions (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        match_id uuid NOT NULL,
        previous_status character varying(16) NOT NULL,
        new_status character varying(16) NOT NULL,
        decided_at timestamp with time zone NOT NULL,
        decided_by_key uuid,
        comment character varying(1000),
        CONSTRAINT pk_reconciliation_match_decisions PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_reconciliation_match_decisions_reconciliation_matches_tenan FOREIGN KEY (tenant_id, match_id) REFERENCES reconciliation_matches (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_reconciliation_match_decisions_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110085407_ReversibleMatchDecisions') THEN
    CREATE INDEX ix_reconciliation_match_decisions_tenant_id_match_id_decided_at ON reconciliation_match_decisions (tenant_id, match_id, decided_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110085407_ReversibleMatchDecisions') THEN
    ALTER TABLE reconciliation_match_decisions ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110085407_ReversibleMatchDecisions') THEN
    ALTER TABLE reconciliation_match_decisions FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110085407_ReversibleMatchDecisions') THEN
    CREATE POLICY tenant_isolation ON reconciliation_match_decisions USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110085407_ReversibleMatchDecisions') THEN
    ALTER TABLE reconciliation_match_decisions ADD CONSTRAINT ck_reconciliation_match_decisions_previous CHECK (previous_status IN ('Proposed', 'Confirmed', 'Rejected'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110085407_ReversibleMatchDecisions') THEN
    ALTER TABLE reconciliation_match_decisions ADD CONSTRAINT ck_reconciliation_match_decisions_new CHECK (new_status IN ('Confirmed', 'Rejected') AND new_status <> previous_status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110085407_ReversibleMatchDecisions') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261110085407_ReversibleMatchDecisions', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    CREATE TABLE attention_items (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        rule_code character varying(64) NOT NULL,
        category character varying(32) NOT NULL,
        severity character varying(16) NOT NULL,
        title character varying(200) NOT NULL,
        body character varying(1000) NOT NULL,
        details jsonb NOT NULL,
        entity_kind character varying(32) NOT NULL,
        entity_ref character varying(64) NOT NULL,
        dedup_key character varying(256) NOT NULL,
        member_keys text[] NOT NULL,
        status character varying(16) NOT NULL,
        snoozed_until timestamp with time zone,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        resolved_at timestamp with time zone,
        decided_by_key uuid,
        comment character varying(1000),
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_attention_items PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_attention_items_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    CREATE TABLE attention_item_events (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        item_id uuid NOT NULL,
        kind character varying(16) NOT NULL,
        at timestamp with time zone NOT NULL,
        api_key_id uuid,
        comment character varying(1000),
        CONSTRAINT pk_attention_item_events PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_attention_item_events_attention_items_tenant_id_item_id FOREIGN KEY (tenant_id, item_id) REFERENCES attention_items (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_attention_item_events_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    CREATE INDEX ix_attention_item_events_tenant_id_item_id_at ON attention_item_events (tenant_id, item_id, at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    CREATE INDEX ix_attention_items_tenant_id_rule_code_status ON attention_items (tenant_id, rule_code, status);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    CREATE INDEX ix_attention_items_tenant_id_status_severity_last_seen_at ON attention_items (tenant_id, status, severity, last_seen_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    CREATE UNIQUE INDEX ux_attention_items_dedup_open ON attention_items (tenant_id, dedup_key) WHERE status <> 'Resolved';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    ALTER TABLE attention_items ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    ALTER TABLE attention_items FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    CREATE POLICY tenant_isolation ON attention_items USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    ALTER TABLE attention_item_events ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    ALTER TABLE attention_item_events FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    CREATE POLICY tenant_isolation ON attention_item_events USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    ALTER TABLE attention_items ADD CONSTRAINT ck_attention_items_severity CHECK (severity IN ('Critical', 'High', 'Medium', 'Low'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    ALTER TABLE attention_items ADD CONSTRAINT ck_attention_items_status CHECK (status IN ('New', 'Seen', 'Snoozed', 'Dismissed', 'Resolved'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    ALTER TABLE attention_items ADD CONSTRAINT ck_attention_items_category CHECK (category IN ('Connection', 'Bank', 'Reconciliation', 'Documents'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    ALTER TABLE attention_items ADD CONSTRAINT ck_attention_items_snooze CHECK (status <> 'Snoozed' OR snoozed_until IS NOT NULL);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    ALTER TABLE attention_item_events ADD CONSTRAINT ck_attention_item_events_kind CHECK (kind IN ('Created', 'Reopened', 'Seen', 'Snoozed', 'Dismissed', 'AutoResolved'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110101425_AttentionInbox') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261110101425_AttentionInbox', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    DROP INDEX ux_attention_items_dedup_open;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE bank_transactions ADD details jsonb;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE bank_transactions ADD id_source character varying(16) NOT NULL DEFAULT 'Bank';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    CREATE TABLE statement_imports (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        connection_id uuid NOT NULL,
        sync_run_id uuid,
        file_name character varying(256) NOT NULL,
        file_sha256 character(64) NOT NULL,
        format character varying(16) NOT NULL,
        profile character varying(128) NOT NULL,
        imported_at timestamp with time zone NOT NULL,
        imported_by_key uuid,
        account character varying(512) NOT NULL,
        period_from date NOT NULL,
        period_to date NOT NULL,
        transactions_total integer NOT NULL,
        transactions_new integer NOT NULL,
        transactions_duplicate integer NOT NULL,
        rows_rejected integer NOT NULL,
        warning_count integer NOT NULL,
        balance_check character varying(16) NOT NULL,
        CONSTRAINT pk_statement_imports PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_statement_imports_connections_tenant_id_connection_id FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_statement_imports_sync_runs_tenant_id_sync_run_id FOREIGN KEY (tenant_id, sync_run_id) REFERENCES sync_runs (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_statement_imports_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    CREATE UNIQUE INDEX ux_attention_items_dedup_open ON attention_items (tenant_id, dedup_key) WHERE status NOT IN ('Resolved', 'Archived');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    CREATE INDEX ix_statement_imports_tenant_id_connection_id_imported_at ON statement_imports (tenant_id, connection_id, imported_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    CREATE UNIQUE INDEX ix_statement_imports_tenant_id_file_sha256 ON statement_imports (tenant_id, file_sha256);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    CREATE INDEX ix_statement_imports_tenant_id_sync_run_id ON statement_imports (tenant_id, sync_run_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE statement_imports ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE statement_imports FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    CREATE POLICY tenant_isolation ON statement_imports USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE statement_imports ADD CONSTRAINT ck_statement_imports_format CHECK (format IN ('Mt940', 'Csv'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE statement_imports ADD CONSTRAINT ck_statement_imports_balance_check CHECK (balance_check IN ('Passed', 'Failed', 'Skipped'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE statement_imports ADD CONSTRAINT ck_statement_imports_counts CHECK (transactions_total >= 0 AND transactions_new >= 0 AND transactions_duplicate >= 0 AND rows_rejected >= 0 AND period_from <= period_to);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    UPDATE bank_transactions SET id_source = 'Synthetic' WHERE operation_id LIKE 'syn:%';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE bank_transactions ADD CONSTRAINT ck_bank_transactions_id_source CHECK (id_source IN ('Bank', 'Synthetic', 'File', 'FileHash'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE attention_items DROP CONSTRAINT ck_attention_items_status;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE attention_items ADD CONSTRAINT ck_attention_items_status CHECK (status IN ('New', 'Seen', 'Snoozed', 'Dismissed', 'Resolved', 'Archived'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE attention_item_events DROP CONSTRAINT ck_attention_item_events_kind;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    ALTER TABLE attention_item_events ADD CONSTRAINT ck_attention_item_events_kind CHECK (kind IN ('Created', 'Reopened', 'Seen', 'Snoozed', 'Dismissed', 'AutoResolved', 'Archived'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261110143731_StatementImports') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261110143731_StatementImports', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE tool_audit_log DROP CONSTRAINT fk_tool_audit_log_api_keys_tenant_id_api_key_id;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE api_keys DROP CONSTRAINT pk_api_keys;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE tenants RENAME COLUMN inn TO tin;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE tool_audit_log ADD account_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE connections ADD onboarding_note character varying(500);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE connections ADD onboarding_status character varying(32) NOT NULL DEFAULT 'NotStarted';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE api_keys ALTER COLUMN tenant_id DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE api_keys ADD account_id uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE api_keys ADD CONSTRAINT pk_api_keys PRIMARY KEY (id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    CREATE TABLE accounts (
        id uuid NOT NULL,
        name character varying(256) NOT NULL,
        status character varying(32) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_accounts PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    CREATE TABLE api_key_tenants (
        key_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        CONSTRAINT pk_api_key_tenants PRIMARY KEY (key_id, tenant_id),
        CONSTRAINT fk_api_key_tenants_api_keys_key_id FOREIGN KEY (key_id) REFERENCES api_keys (id) ON DELETE CASCADE,
        CONSTRAINT fk_api_key_tenants_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    CREATE TABLE account_tenants (
        account_id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        display_name character varying(256),
        added_at timestamp with time zone NOT NULL,
        disabled_at timestamp with time zone,
        CONSTRAINT pk_account_tenants PRIMARY KEY (account_id, tenant_id),
        CONSTRAINT fk_account_tenants_accounts_account_id FOREIGN KEY (account_id) REFERENCES accounts (id) ON DELETE RESTRICT,
        CONSTRAINT fk_account_tenants_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    CREATE INDEX ix_tool_audit_log_account_id ON tool_audit_log (account_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    CREATE INDEX ix_tool_audit_log_api_key_id ON tool_audit_log (api_key_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    CREATE INDEX ix_api_keys_account_id ON api_keys (account_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    CREATE INDEX ix_api_keys_tenant_id ON api_keys (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    CREATE UNIQUE INDEX ix_account_tenants_tenant_id ON account_tenants (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    CREATE UNIQUE INDEX ix_accounts_name ON accounts (name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    CREATE INDEX ix_api_key_tenants_tenant_id ON api_key_tenants (tenant_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE api_keys ADD CONSTRAINT fk_api_keys_accounts_account_id FOREIGN KEY (account_id) REFERENCES accounts (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE tool_audit_log ADD CONSTRAINT fk_tool_audit_log_accounts_account_id FOREIGN KEY (account_id) REFERENCES accounts (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE tool_audit_log ADD CONSTRAINT fk_tool_audit_log_api_keys_api_key_id FOREIGN KEY (api_key_id) REFERENCES api_keys (id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE tenants ADD CONSTRAINT ck_tenants_tin CHECK (tin IS NULL OR tin ~ '^([0-9]{9}|[0-9]{14})$');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE api_keys ADD CONSTRAINT ck_api_keys_owner CHECK ((tenant_id IS NULL) <> (account_id IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE accounts ADD CONSTRAINT ck_accounts_status CHECK (status IN ('Active', 'Suspended'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE connections ADD CONSTRAINT ck_connections_onboarding_status CHECK (onboarding_status IN ('NotStarted', 'WaitingForAccess', 'Connected', 'NeedsAttention'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE accounts ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    REVOKE ALL ON accounts FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE account_tenants ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    REVOKE ALL ON account_tenants FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE api_key_tenants ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    REVOKE ALL ON api_key_tenants FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    ALTER TABLE account_tool_audit_log ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    REVOKE ALL ON account_tool_audit_log FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    DO $do$
    BEGIN
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'synect_app') THEN
            REVOKE ALL ON accounts, account_tenants, api_key_tenants, account_tool_audit_log FROM synect_app;
        END IF;
    END
    $do$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    DROP FUNCTION synect_resolve_api_key(bytea);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    REVOKE ALL ON FUNCTION synect_resolve_api_key(bytea) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    DO $do$
    BEGIN
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'synect_app') THEN
            GRANT EXECUTE ON FUNCTION synect_resolve_api_key(bytea) TO synect_app;
        END IF;
    END
    $do$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    REVOKE ALL ON FUNCTION synect_api_key_organizations(bytea) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    DO $do$
    BEGIN
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'synect_app') THEN
            GRANT EXECUTE ON FUNCTION synect_api_key_organizations(bytea) TO synect_app;
        END IF;
    END
    $do$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    REVOKE ALL ON FUNCTION synect_write_account_tool_audit(bytea, text, jsonb, timestamptz, integer, integer, text, text, jsonb, uuid[]) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    DO $do$
    BEGIN
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'synect_app') THEN
            GRANT EXECUTE ON FUNCTION synect_write_account_tool_audit(bytea, text, jsonb, timestamptz, integer, integer, text, text, jsonb, uuid[]) TO synect_app;
        END IF;
    END
    $do$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111034710_AccountsStage65') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261111034710_AccountsStage65', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE accounting_bank_account_balances (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        as_of_date date NOT NULL,
        bank_account_ref character varying(64) NOT NULL,
        balance_minor bigint NOT NULL,
        turnover_debit_minor bigint,
        turnover_credit_minor bigint,
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_accounting_bank_account_balances PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_accounting_bank_account_balances_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE accounting_bank_accounts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        number character varying(64) NOT NULL,
        bank_code character varying(64),
        bank_name text,
        currency character varying(3) NOT NULL,
        gl_account_code character varying(64),
        is_active boolean,
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        ref character varying(64) NOT NULL,
        CONSTRAINT pk_accounting_bank_accounts PRIMARY KEY (tenant_id, id),
        CONSTRAINT ak_accounting_bank_accounts_tenant_id_ref UNIQUE (tenant_id, ref),
        CONSTRAINT fk_accounting_bank_accounts_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE accounting_bank_documents (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        direction character varying(16) NOT NULL,
        number character varying(64) NOT NULL,
        date date NOT NULL,
        posted boolean NOT NULL,
        bank_account_ref character varying(64),
        bank_account_number character varying(64),
        counterparty_ref character varying(64),
        counterparty_tin character varying(16),
        amount_minor bigint NOT NULL,
        currency character varying(3) NOT NULL,
        purpose text,
        contract_ref character varying(64),
        bank_document_number character varying(64),
        bank_document_date date,
        cash_flow_item character varying(256),
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        ref character varying(64) NOT NULL,
        CONSTRAINT pk_accounting_bank_documents PRIMARY KEY (tenant_id, id),
        CONSTRAINT ak_accounting_bank_documents_tenant_id_ref UNIQUE (tenant_id, ref),
        CONSTRAINT fk_accounting_bank_documents_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE accounting_contracts (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        counterparty_ref character varying(64) NOT NULL,
        number character varying(64),
        date date,
        kind character varying(256),
        currency character varying(3) NOT NULL,
        valid_until date,
        payment_term_days integer,
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        ref character varying(64) NOT NULL,
        CONSTRAINT pk_accounting_contracts PRIMARY KEY (tenant_id, id),
        CONSTRAINT ak_accounting_contracts_tenant_id_ref UNIQUE (tenant_id, ref),
        CONSTRAINT fk_accounting_contracts_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE accounting_counterparties (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        tin character varying(16),
        name text NOT NULL,
        full_name text,
        kind character varying(256),
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        ref character varying(64) NOT NULL,
        CONSTRAINT pk_accounting_counterparties PRIMARY KEY (tenant_id, id),
        CONSTRAINT ak_accounting_counterparties_tenant_id_ref UNIQUE (tenant_id, ref),
        CONSTRAINT fk_accounting_counterparties_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE accounting_documents (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        doc_kind character varying(16) NOT NULL,
        doc_type character varying(256),
        number character varying(64) NOT NULL,
        date date NOT NULL,
        posted boolean NOT NULL,
        counterparty_ref character varying(64),
        counterparty_tin character varying(16),
        contract_ref character varying(64),
        contract_number character varying(64),
        contract_date date,
        currency character varying(3) NOT NULL,
        amount_total_minor bigint NOT NULL,
        vat_amount_minor bigint,
        esf_number character varying(64),
        esf_date date,
        esf_id character varying(64),
        esf_status character varying(64),
        due_date date,
        comment text,
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        ref character varying(64) NOT NULL,
        CONSTRAINT pk_accounting_documents PRIMARY KEY (tenant_id, id),
        CONSTRAINT ak_accounting_documents_tenant_id_ref UNIQUE (tenant_id, ref),
        CONSTRAINT fk_accounting_documents_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE accounting_payment_orders (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        number character varying(64) NOT NULL,
        date date NOT NULL,
        posted boolean NOT NULL,
        bank_account_ref character varying(64),
        counterparty_tin character varying(16),
        amount_minor bigint NOT NULL,
        currency character varying(3) NOT NULL,
        purpose text,
        bank_status character varying(256),
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        ref character varying(64) NOT NULL,
        CONSTRAINT pk_accounting_payment_orders PRIMARY KEY (tenant_id, id),
        CONSTRAINT ak_accounting_payment_orders_tenant_id_ref UNIQUE (tenant_id, ref),
        CONSTRAINT fk_accounting_payment_orders_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE accounting_settlement_balances (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        as_of_date date NOT NULL,
        counterparty_ref character varying(64),
        counterparty_tin character varying(16),
        contract_ref character varying(64),
        gl_account_code character varying(64) NOT NULL,
        currency character varying(3) NOT NULL,
        balance_debit_minor bigint,
        balance_credit_minor bigint,
        turnover_debit_minor bigint,
        turnover_credit_minor bigint,
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_accounting_settlement_balances PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_accounting_settlement_balances_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE ingest_full_sync_items (
        tenant_id uuid NOT NULL,
        full_sync_id character varying(64) NOT NULL,
        entity character varying(32) NOT NULL,
        item_key character varying(400) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_ingest_full_sync_items PRIMARY KEY (tenant_id, full_sync_id, entity, item_key),
        CONSTRAINT fk_ingest_full_sync_items_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE ingest_keys (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        connection_id uuid NOT NULL,
        name character varying(256) NOT NULL,
        key_hash bytea NOT NULL,
        key_prefix character varying(32) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        last_used_at timestamp with time zone,
        revoked_at timestamp with time zone,
        CONSTRAINT pk_ingest_keys PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_ingest_keys_connections_tenant_id_connection_id FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_ingest_keys_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE accounting_bank_document_settlements (
        tenant_id uuid NOT NULL,
        document_ref character varying(64) NOT NULL,
        line_no integer NOT NULL,
        settled_document_ref character varying(64),
        amount_minor bigint NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        CONSTRAINT pk_accounting_bank_document_settlements PRIMARY KEY (tenant_id, document_ref, line_no),
        CONSTRAINT fk_accounting_bank_document_settlements_accounting_bank_docume FOREIGN KEY (tenant_id, document_ref) REFERENCES accounting_bank_documents (tenant_id, ref) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE accounting_document_lines (
        tenant_id uuid NOT NULL,
        document_ref character varying(64) NOT NULL,
        line_no integer NOT NULL,
        name text,
        product_code character varying(256),
        quantity numeric(28,6),
        price_minor bigint,
        vat_rate numeric(9,4),
        amount_minor bigint NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        CONSTRAINT pk_accounting_document_lines PRIMARY KEY (tenant_id, document_ref, line_no),
        CONSTRAINT fk_accounting_document_lines_accounting_documents_tenant_id_do FOREIGN KEY (tenant_id, document_ref) REFERENCES accounting_documents (tenant_id, ref) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE TABLE ingest_batches (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        connection_id uuid NOT NULL,
        ingest_key_id uuid NOT NULL,
        batch_id character varying(64) NOT NULL,
        sequence integer NOT NULL,
        entity character varying(32) NOT NULL,
        mode character varying(16) NOT NULL,
        full_sync_id character varying(64),
        is_last_batch boolean NOT NULL,
        schema_version integer NOT NULL,
        extension_version character varying(64),
        exported_at timestamp with time zone,
        received_at timestamp with time zone NOT NULL,
        finished_at timestamp with time zone,
        items_total integer NOT NULL,
        accepted integer NOT NULL,
        unchanged integer NOT NULL,
        rejected integer NOT NULL,
        marked_deleted integer NOT NULL,
        status character varying(32) NOT NULL,
        error character varying(2000),
        payload_sha256 character(64) NOT NULL,
        payload_bytes integer NOT NULL,
        item_errors jsonb,
        envelope_extra jsonb,
        attempts integer NOT NULL DEFAULT 1,
        CONSTRAINT pk_ingest_batches PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_ingest_batches_connections_tenant_id_connection_id FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_ingest_batches_ingest_keys_tenant_id_ingest_key_id FOREIGN KEY (tenant_id, ingest_key_id) REFERENCES ingest_keys (tenant_id, id) ON DELETE RESTRICT,
        CONSTRAINT fk_ingest_batches_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE UNIQUE INDEX ix_accounting_bank_account_balances_tenant_id_as_of_date_bank_ ON accounting_bank_account_balances (tenant_id, as_of_date, bank_account_ref);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_accounting_bank_accounts_tenant_id_number ON accounting_bank_accounts (tenant_id, number);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_accounting_bank_documents_tenant_id_counterparty_tin_date ON accounting_bank_documents (tenant_id, counterparty_tin, date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_accounting_contracts_tenant_id_counterparty_ref ON accounting_contracts (tenant_id, counterparty_ref);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_accounting_counterparties_tenant_id_tin ON accounting_counterparties (tenant_id, tin);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_accounting_documents_tenant_id_counterparty_tin_date ON accounting_documents (tenant_id, counterparty_tin, date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_accounting_payment_orders_tenant_id_date ON accounting_payment_orders (tenant_id, date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE UNIQUE INDEX ix_accounting_settlement_balances_tenant_id_as_of_date_counter ON accounting_settlement_balances (tenant_id, as_of_date, counterparty_ref, contract_ref, gl_account_code, currency) NULLS NOT DISTINCT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_accounting_settlement_balances_tenant_id_counterparty_tin_a ON accounting_settlement_balances (tenant_id, counterparty_tin, as_of_date);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE UNIQUE INDEX ix_ingest_batches_tenant_id_batch_id_sequence ON ingest_batches (tenant_id, batch_id, sequence);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_ingest_batches_tenant_id_connection_id ON ingest_batches (tenant_id, connection_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_ingest_batches_tenant_id_full_sync_id_entity_sequence ON ingest_batches (tenant_id, full_sync_id, entity, sequence) WHERE full_sync_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_ingest_batches_tenant_id_ingest_key_id ON ingest_batches (tenant_id, ingest_key_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_ingest_batches_tenant_id_received_at ON ingest_batches (tenant_id, received_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_ingest_full_sync_items_tenant_id_created_at ON ingest_full_sync_items (tenant_id, created_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE UNIQUE INDEX ix_ingest_keys_key_hash ON ingest_keys (key_hash);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE INDEX ix_ingest_keys_tenant_id_connection_id ON ingest_keys (tenant_id, connection_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    UPDATE connections SET adapter_kind = 'Accounting1C' WHERE adapter_kind = 'Accounting';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_counterparties ADD CONSTRAINT fk_accounting_counterparties_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_contracts ADD CONSTRAINT fk_accounting_contracts_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_accounts ADD CONSTRAINT fk_accounting_bank_accounts_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_documents ADD CONSTRAINT fk_accounting_documents_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_documents ADD CONSTRAINT fk_accounting_bank_documents_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_payment_orders ADD CONSTRAINT fk_accounting_payment_orders_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_settlement_balances ADD CONSTRAINT fk_accounting_settlement_balances_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_account_balances ADD CONSTRAINT fk_accounting_bank_account_balances_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_counterparties ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_counterparties FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON accounting_counterparties USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_contracts ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_contracts FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON accounting_contracts USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_accounts ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_accounts FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON accounting_bank_accounts USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_documents ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_documents FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON accounting_documents USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_document_lines ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_document_lines FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON accounting_document_lines USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_documents ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_documents FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON accounting_bank_documents USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_document_settlements ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_document_settlements FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON accounting_bank_document_settlements USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_payment_orders ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_payment_orders FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON accounting_payment_orders USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_settlement_balances ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_settlement_balances FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON accounting_settlement_balances USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_account_balances ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_account_balances FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON accounting_bank_account_balances USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE ingest_batches ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE ingest_batches FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON ingest_batches USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE ingest_full_sync_items ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE ingest_full_sync_items FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON ingest_full_sync_items USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE ingest_keys ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    CREATE POLICY tenant_isolation ON ingest_keys USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE ingest_keys ADD CONSTRAINT ck_ingest_keys_key_hash_length CHECK (octet_length(key_hash) = 32);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE ingest_batches ADD CONSTRAINT ck_ingest_batches_status CHECK (status IN ('Processed', 'ProcessedWithErrors', 'Rejected', 'Failed'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE ingest_batches ADD CONSTRAINT ck_ingest_batches_counts CHECK (sequence >= 1 AND items_total >= 0 AND accepted >= 0 AND unchanged >= 0 AND rejected >= 0 AND marked_deleted >= 0);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_documents ADD CONSTRAINT ck_accounting_documents_doc_kind CHECK (doc_kind IN ('Sales', 'Purchase'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_documents ADD CONSTRAINT ck_accounting_bank_documents_direction CHECK (direction IN ('Receipt', 'Payment'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_counterparties ADD CONSTRAINT ck_accounting_counterparties_ref CHECK (length(ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_contracts ADD CONSTRAINT ck_accounting_contracts_ref CHECK (length(ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_accounts ADD CONSTRAINT ck_accounting_bank_accounts_ref CHECK (length(ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_documents ADD CONSTRAINT ck_accounting_documents_ref CHECK (length(ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_bank_documents ADD CONSTRAINT ck_accounting_bank_documents_ref CHECK (length(ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    ALTER TABLE accounting_payment_orders ADD CONSTRAINT ck_accounting_payment_orders_ref CHECK (length(ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
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
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    REVOKE ALL ON FUNCTION synect_resolve_ingest_key(bytea) FROM PUBLIC;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    DO $do$
    BEGIN
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'synect_app') THEN
            GRANT EXECUTE ON FUNCTION synect_resolve_ingest_key(bytea) TO synect_app;
        END IF;
    END
    $do$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261111182942_Accounting1CIngest') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261111182942_Accounting1CIngest', '10.0.14');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE ingest_batches ADD removed_rows integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE TABLE catalog_categories (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        parent_ref character varying(64),
        name text NOT NULL,
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        ref character varying(64) NOT NULL,
        CONSTRAINT pk_catalog_categories PRIMARY KEY (tenant_id, id),
        CONSTRAINT ak_catalog_categories_tenant_id_ref UNIQUE (tenant_id, ref),
        CONSTRAINT fk_catalog_categories_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE TABLE catalog_items (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        item_type character varying(16),
        code character varying(64),
        article character varying(64),
        name text NOT NULL,
        full_name text,
        search_name character varying(4000) NOT NULL,
        category_ref character varying(64),
        unit character varying(256),
        barcodes text[] NOT NULL,
        vat_rate numeric(9,4),
        is_archived boolean NOT NULL DEFAULT FALSE,
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        ref character varying(64) NOT NULL,
        CONSTRAINT pk_catalog_items PRIMARY KEY (tenant_id, id),
        CONSTRAINT ak_catalog_items_tenant_id_ref UNIQUE (tenant_id, ref),
        CONSTRAINT fk_catalog_items_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE TABLE catalog_price_types (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name text NOT NULL,
        currency character varying(3) NOT NULL,
        is_default boolean NOT NULL DEFAULT FALSE,
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        ref character varying(64) NOT NULL,
        CONSTRAINT pk_catalog_price_types PRIMARY KEY (tenant_id, id),
        CONSTRAINT ak_catalog_price_types_tenant_id_ref UNIQUE (tenant_id, ref),
        CONSTRAINT fk_catalog_price_types_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE TABLE catalog_prices (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        item_ref character varying(64) NOT NULL,
        price_type_ref character varying(64) NOT NULL,
        price_minor bigint NOT NULL,
        currency character varying(3) NOT NULL,
        effective_from date,
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_catalog_prices PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_catalog_prices_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE TABLE catalog_stock (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        item_ref character varying(64) NOT NULL,
        warehouse_ref character varying(64) NOT NULL,
        quantity numeric(28,6) NOT NULL,
        reserved numeric(28,6),
        as_of timestamp with time zone NOT NULL,
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_catalog_stock PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_catalog_stock_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE TABLE catalog_warehouses (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        name text NOT NULL,
        is_deleted boolean NOT NULL DEFAULT FALSE,
        modified_at timestamp with time zone NOT NULL,
        content_hash character(64) NOT NULL,
        extra jsonb NOT NULL DEFAULT '{}',
        schema_version integer NOT NULL,
        last_batch_id uuid NOT NULL,
        first_seen_at timestamp with time zone NOT NULL,
        last_seen_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        ref character varying(64) NOT NULL,
        CONSTRAINT pk_catalog_warehouses PRIMARY KEY (tenant_id, id),
        CONSTRAINT ak_catalog_warehouses_tenant_id_ref UNIQUE (tenant_id, ref),
        CONSTRAINT fk_catalog_warehouses_tenants_tenant_id FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE TABLE ingest_freshness (
        tenant_id uuid NOT NULL,
        connection_id uuid NOT NULL,
        data_group character varying(16) NOT NULL,
        last_accepted_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_ingest_freshness PRIMARY KEY (tenant_id, connection_id, data_group),
        CONSTRAINT fk_ingest_freshness_connections_tenant_id_connection_id FOREIGN KEY (tenant_id, connection_id) REFERENCES connections (tenant_id, id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE TABLE ingest_removed_rows (
        tenant_id uuid NOT NULL,
        id uuid NOT NULL,
        ingest_batch_id uuid NOT NULL,
        full_sync_id character varying(64) NOT NULL,
        entity character varying(32) NOT NULL,
        item_key character varying(400) NOT NULL,
        removed_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_ingest_removed_rows PRIMARY KEY (tenant_id, id),
        CONSTRAINT fk_ingest_removed_rows_ingest_batches_tenant_id_ingest_batch_id FOREIGN KEY (tenant_id, ingest_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE INDEX ix_catalog_categories_tenant_id_parent_ref ON catalog_categories (tenant_id, parent_ref);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE INDEX ix_catalog_items_tenant_id_article ON catalog_items (tenant_id, article);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE INDEX ix_catalog_items_tenant_id_category_ref ON catalog_items (tenant_id, category_ref);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE INDEX ix_catalog_items_tenant_id_code ON catalog_items (tenant_id, code);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE UNIQUE INDEX ix_catalog_prices_tenant_id_item_ref_price_type_ref ON catalog_prices (tenant_id, item_ref, price_type_ref);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE UNIQUE INDEX ix_catalog_stock_tenant_id_item_ref_warehouse_ref ON catalog_stock (tenant_id, item_ref, warehouse_ref);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE INDEX ix_ingest_removed_rows_tenant_id_entity_item_key ON ingest_removed_rows (tenant_id, entity, item_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE INDEX ix_ingest_removed_rows_tenant_id_ingest_batch_id ON ingest_removed_rows (tenant_id, ingest_batch_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_items ADD CONSTRAINT fk_catalog_items_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_categories ADD CONSTRAINT fk_catalog_categories_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_price_types ADD CONSTRAINT fk_catalog_price_types_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_prices ADD CONSTRAINT fk_catalog_prices_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_warehouses ADD CONSTRAINT fk_catalog_warehouses_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_stock ADD CONSTRAINT fk_catalog_stock_ingest_batches FOREIGN KEY (tenant_id, last_batch_id) REFERENCES ingest_batches (tenant_id, id) ON DELETE RESTRICT;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_items ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_items FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE POLICY tenant_isolation ON catalog_items USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_categories ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_categories FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE POLICY tenant_isolation ON catalog_categories USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_price_types ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_price_types FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE POLICY tenant_isolation ON catalog_price_types USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_prices ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_prices FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE POLICY tenant_isolation ON catalog_prices USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_warehouses ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_warehouses FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE POLICY tenant_isolation ON catalog_warehouses USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_stock ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_stock FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE POLICY tenant_isolation ON catalog_stock USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE ingest_freshness ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE ingest_freshness FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE POLICY tenant_isolation ON ingest_freshness USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE ingest_removed_rows ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE ingest_removed_rows FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE POLICY tenant_isolation ON ingest_removed_rows USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid) WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_items ADD CONSTRAINT ck_catalog_items_ref CHECK (length(ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_categories ADD CONSTRAINT ck_catalog_categories_ref CHECK (length(ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_price_types ADD CONSTRAINT ck_catalog_price_types_ref CHECK (length(ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_warehouses ADD CONSTRAINT ck_catalog_warehouses_ref CHECK (length(ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_prices ADD CONSTRAINT ck_catalog_prices_refs CHECK (length(item_ref) BETWEEN 1 AND 64 AND length(price_type_ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_stock ADD CONSTRAINT ck_catalog_stock_refs CHECK (length(item_ref) BETWEEN 1 AND 64 AND length(warehouse_ref) BETWEEN 1 AND 64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE catalog_items ADD CONSTRAINT ck_catalog_items_item_type CHECK (item_type IS NULL OR item_type IN ('Goods', 'Service'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE ingest_freshness ADD CONSTRAINT ck_ingest_freshness_data_group CHECK (data_group IN ('Accounting', 'Catalog'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    ALTER TABLE ingest_batches ADD CONSTRAINT ck_ingest_batches_removed_rows CHECK (removed_rows >= 0);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE INDEX ix_catalog_items_tenant_id_barcodes ON catalog_items USING gin (tenant_id, barcodes);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    CREATE INDEX ix_catalog_items_tenant_id_search_name_trgm ON catalog_items USING gin (tenant_id, search_name gin_trgm_ops);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    INSERT INTO ingest_freshness (tenant_id, connection_id, data_group, last_accepted_at)
    SELECT b.tenant_id, b.connection_id, 'Accounting', max(b.received_at)
    FROM ingest_batches b
    WHERE b.status IN ('Processed', 'ProcessedWithErrors') AND (b.accepted + b.unchanged > 0 OR b.items_total = 0)
    GROUP BY b.tenant_id, b.connection_id
    ON CONFLICT DO NOTHING;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM __ef_migrations_history WHERE "migration_id" = '20261115115327_CatalogIngest') THEN
    INSERT INTO __ef_migrations_history (migration_id, product_version)
    VALUES ('20261115115327_CatalogIngest', '10.0.14');
    END IF;
END $EF$;
COMMIT;

