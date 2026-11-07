-- Local PostgreSQL setup for Synect (run once as a superuser, e.g. `psql -U postgres -f db/create-roles.sql`).
-- Passwords are NOT stored here: set them interactively with \password, then put the
-- connection string into user-secrets (see README.md).
--
--   synect_owner : owns the schema, runs migrations.
--   synect_app   : used by Api/Worker. Not the table owner, no BYPASSRLS -> row-level security applies.

CREATE ROLE synect_owner LOGIN;
CREATE ROLE synect_app LOGIN;

CREATE DATABASE synect OWNER synect_owner;

\connect synect

-- Default privileges: tables created by synect_owner (migrations) are usable by synect_app.
ALTER DEFAULT PRIVILEGES FOR ROLE synect_owner IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO synect_app;
ALTER DEFAULT PRIVILEGES FOR ROLE synect_owner IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO synect_app;
GRANT USAGE ON SCHEMA public TO synect_app;

-- Next steps:
--   \password synect_owner
--   \password synect_app
