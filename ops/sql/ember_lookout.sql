-- Creates or updates the role ember_lookout, the Lookout sender's own login on emberdb: the columns of the outbox, the reports waiting
-- in it, their pictures and the diagnostics uploads that the sender reads, the delivery state it writes, and the rows it deletes once
-- Lookout answered. Nothing of accounts, habits or the link between a report and its account ("Feedback"."UserId"), and no permanent objects of its own.
-- Idempotent. Re-run it after a migration that changes a column the sender uses: the sender refuses to start while a right is missing.
-- Run as superuser from the repo root, with a password of letters and digits (it goes into a connection string; it never appears on a
-- command line or in output):
--   LOOKOUT_PW="$(cat ~/.config/ember-ops/ember_lookout)" docker exec -i -e LOOKOUT_PW homeserver-postgres psql -U finvarex -d emberdb -f - < ops/sql/ember_lookout.sql

\set ON_ERROR_STOP on
\set QUIET on
SET log_statement = 'none';
SET log_min_duration_statement = -1;
SET log_min_error_statement = 'panic';
\getenv lookout_pw LOOKOUT_PW
\if :{?lookout_pw}
\else
DO $$ BEGIN RAISE EXCEPTION 'LOOKOUT_PW is not set'; END $$;
\endif
SELECT :'lookout_pw' !~ '^[A-Za-z0-9]{24,}$' AS lookout_pw_unfit \gset
\if :lookout_pw_unfit
DO $$ BEGIN RAISE EXCEPTION 'LOOKOUT_PW must be at least 24 letters and digits'; END $$;
\endif

BEGIN;
SELECT format('CREATE ROLE ember_lookout LOGIN PASSWORD %L', :'lookout_pw')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ember_lookout') \gexec
ALTER ROLE ember_lookout WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 5 PASSWORD :'lookout_pw';
ALTER ROLE ember_lookout SET statement_timeout = '30s';
ALTER ROLE ember_lookout SET lock_timeout = '10s';
ALTER ROLE ember_lookout SET idle_in_transaction_session_timeout = '30s';

SELECT format('REVOKE %s FROM ember_lookout', roleid::regrole) FROM pg_auth_members WHERE member = 'ember_lookout'::regrole \gexec

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM ember_lookout;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM ember_lookout;
REVOKE ALL ON SCHEMA public FROM ember_lookout;
REVOKE ALL ON DATABASE :"DBNAME" FROM ember_lookout;

GRANT CONNECT ON DATABASE :"DBNAME" TO ember_lookout;
GRANT USAGE ON SCHEMA public TO ember_lookout;

GRANT SELECT ("Id", "Kind", "Key", "FeedbackId", "UserRef", "SentAt", "ParkedAt") ON "LookoutOutbox" TO ember_lookout;
GRANT UPDATE ("SentAt", "ParkedAt", "ParkedStatus") ON "LookoutOutbox" TO ember_lookout;
GRANT DELETE ON "LookoutOutbox" TO ember_lookout;
GRANT SELECT ("Id", "Platform", "Version", "Title", "Text", "CreatedAt") ON "Feedback" TO ember_lookout;
GRANT DELETE ON "Feedback" TO ember_lookout;
GRANT SELECT ("Id", "FeedbackId", "ContentType", "Data") ON "FeedbackImages" TO ember_lookout;
GRANT DELETE ON "FeedbackImages" TO ember_lookout;
GRANT SELECT ("Id", "Key", "Body", "CreatedAt", "NextAttemptAt", "Attempts") ON "LookoutDiagnostics" TO ember_lookout;
GRANT UPDATE ("NextAttemptAt", "Attempts") ON "LookoutDiagnostics" TO ember_lookout;
GRANT DELETE ON "LookoutDiagnostics" TO ember_lookout;

COMMIT;
\echo 'ember_lookout: role and grants applied'
