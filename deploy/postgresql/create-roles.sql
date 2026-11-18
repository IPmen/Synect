-- Run once as postgres: psql -v owner_pw="..." -v app_pw="..." -f create-roles.sql
-- (deploy/docs: the passwords are generated on the server and passed as psql variables, never written here).
--   synect_owner : owns the database and the schema, runs migrations (synect-devtools --owner migrate).
--   synect_app   : used by Api and Worker. Not the owner, NOSUPERUSER, NOBYPASSRLS -> row-level security applies.
CREATE ROLE synect_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD :'owner_pw';
CREATE ROLE synect_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD :'app_pw';
CREATE DATABASE synect OWNER synect_owner ENCODING 'UTF8' TEMPLATE template0;
\connect synect
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO synect_owner;
GRANT USAGE ON SCHEMA public TO synect_app;
ALTER DEFAULT PRIVILEGES FOR ROLE synect_owner IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO synect_app;
ALTER DEFAULT PRIVILEGES FOR ROLE synect_owner IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO synect_app;
REVOKE CONNECT ON DATABASE synect FROM PUBLIC;
GRANT CONNECT ON DATABASE synect TO synect_owner, synect_app;
