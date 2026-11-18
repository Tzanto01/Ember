-- Creates or updates the role emberops and its own database emberops (the dashboard's comments, card positions and reminder
-- snapshots; the app creates its tables at startup). Idempotent.
-- Run as superuser from the repo root (the password never appears on a command line or in output):
--   OPS_PW="$(cat ~/.config/ember-ops/emberops)" docker exec -i -e OPS_PW homeserver-postgres psql -U finvarex -d postgres -f - < ops/sql/emberops.sql

\set ON_ERROR_STOP on
\set QUIET on
SET log_statement = 'none';
SET log_min_duration_statement = -1;
SET log_min_error_statement = 'panic';
\getenv ops_pw OPS_PW
\if :{?ops_pw}
\else
DO $$ BEGIN RAISE EXCEPTION 'OPS_PW is not set'; END $$;
\endif
SELECT length(:'ops_pw') < 16 AS ops_pw_short \gset
\if :ops_pw_short
DO $$ BEGIN RAISE EXCEPTION 'OPS_PW must be at least 16 characters'; END $$;
\endif

BEGIN;
SELECT format('CREATE ROLE emberops LOGIN PASSWORD %L', :'ops_pw')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'emberops') \gexec
ALTER ROLE emberops WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 5 PASSWORD :'ops_pw';
ALTER ROLE emberops SET statement_timeout = '5s';
COMMIT;

SELECT 'CREATE DATABASE emberops OWNER emberops'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'emberops') \gexec
ALTER DATABASE emberops OWNER TO emberops;
REVOKE CONNECT, TEMPORARY ON DATABASE emberops FROM PUBLIC;
GRANT CONNECT ON DATABASE emberops TO emberops;

\echo 'emberops: role and database applied'
