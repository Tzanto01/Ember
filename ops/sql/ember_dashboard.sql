-- Creates or updates the read-mostly role ember_dashboard on emberdb: column-level SELECT on aggregates only, UPDATE on
-- Feedback ("Status", "ResolvedAt"). Idempotent; re-run after every Ember migration so new v2 tables and columns are granted.
-- Run as superuser from the repo root (the password never appears on a command line or in output):
--   DASH_PW="$(cat ~/.config/ember-ops/ember_dashboard)" docker exec -i -e DASH_PW homeserver-postgres psql -U finvarex -d emberdb -f - < ops/sql/ember_dashboard.sql

\set ON_ERROR_STOP on
\set QUIET on
SET log_statement = 'none';
SET log_min_duration_statement = -1;
SET log_min_error_statement = 'panic';
\getenv dash_pw DASH_PW
\if :{?dash_pw}
\else
DO $$ BEGIN RAISE EXCEPTION 'DASH_PW is not set'; END $$;
\endif
SELECT length(:'dash_pw') < 16 AS dash_pw_short \gset
\if :dash_pw_short
DO $$ BEGIN RAISE EXCEPTION 'DASH_PW must be at least 16 characters'; END $$;
\endif

BEGIN;
SELECT format('CREATE ROLE ember_dashboard LOGIN PASSWORD %L', :'dash_pw')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'ember_dashboard') \gexec
ALTER ROLE ember_dashboard WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 5 PASSWORD :'dash_pw';
ALTER ROLE ember_dashboard SET statement_timeout = '5s';
ALTER ROLE ember_dashboard SET lock_timeout = '2s';
ALTER ROLE ember_dashboard SET idle_in_transaction_session_timeout = '10s';
ALTER ROLE ember_dashboard SET jit = off;

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM ember_dashboard;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM ember_dashboard;
DO $$
DECLARE t text; c text;
BEGIN
    FOREACH t IN ARRAY ARRAY['HabitLogs', 'DayNotes', 'DayMarks', 'JournalKeys', 'HabitSlips', 'FiresideLinks', 'Warmth', 'ReminderMessages', 'PushReminders'] LOOP
        IF to_regclass(format('public.%I', t)) IS NOT NULL THEN
            EXECUTE format('REVOKE ALL ON public.%I FROM ember_dashboard', t);
        END IF;
    END LOOP;
    IF EXISTS (SELECT FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'AspNetUsers' AND column_name = 'FiresideEnabled') THEN
        REVOKE ALL ("FiresideEnabled") ON "AspNetUsers" FROM ember_dashboard;
    END IF;
    IF EXISTS (SELECT FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Habits' AND column_name = 'Kind') THEN
        REVOKE ALL ("Kind") ON "Habits" FROM ember_dashboard;
    END IF;
    FOR t, c IN VALUES
        ('AspNetUsers', 'ShowStreaks'), ('AspNetUsers', 'GraceDaysPerWeek'), ('AspNetUsers', 'SecondReminderMinutes'), ('AspNetUsers', 'DmFailureCount'),
        ('AspNetUsers', 'DmRemindersPausedAt'), ('AspNetUsers', 'DiscordReminders'), ('AspNetUsers', 'Theme'),
        ('Habits', 'Id'), ('Habits', 'RoutineId'), ('Habits', 'PausedFrom'), ('Habits', 'PausedUntil'), ('Habits', 'SecondReminderUtc'),
        ('Habits', 'FrequencyType'), ('Habits', 'TimesPerDay'), ('Habits', 'ReminderTimes'),
        ('Routines', 'Id'), ('Routines', 'UserId'), ('Routines', 'SecondReminderUtc'), ('Routines', 'ReminderTime'), ('Routines', 'CreatedAt'),
        ('PushSubscriptions', 'Id'), ('ReminderPhones', 'UserId'), ('ReminderPhones', 'Enabled')
    LOOP
        IF EXISTS (SELECT FROM information_schema.columns WHERE table_schema = 'public' AND table_name = t AND column_name = c) THEN
            EXECUTE format('REVOKE ALL (%I) ON public.%I FROM ember_dashboard', c, t);
        END IF;
    END LOOP;
END
$$;

GRANT CONNECT ON DATABASE emberdb TO ember_dashboard;
GRANT USAGE ON SCHEMA public TO ember_dashboard;

GRANT SELECT ("Id") ON "AspNetUsers" TO ember_dashboard;
GRANT SELECT ("UserId", "LoginProvider") ON "AspNetUserLogins" TO ember_dashboard;
GRANT SELECT ("UserId", "CreatedAt", "NextReminderUtc") ON "Habits" TO ember_dashboard;
GRANT SELECT ("NextReminderUtc") ON "Routines" TO ember_dashboard;
GRANT SELECT ("UserId", "CreatedUtc", "LastSeenUtc") ON "AuthSessions" TO ember_dashboard;
GRANT SELECT ("UserId", "Enabled") ON "PushSubscriptions" TO ember_dashboard;
GRANT SELECT ("Id") ON "ReminderPhones" TO ember_dashboard;
GRANT SELECT ("Id", "Title", "Text", "Platform", "Version", "CreatedAt", "Status", "ResolvedAt", "UserId") ON "Feedback" TO ember_dashboard;
GRANT UPDATE ("Status", "ResolvedAt") ON "Feedback" TO ember_dashboard;
GRANT SELECT ("Id", "FeedbackId", "ContentType", "Size", "Data") ON "FeedbackImages" TO ember_dashboard;
GRANT SELECT ("MigrationId") ON "__EFMigrationsHistory" TO ember_dashboard;

DO $$
BEGIN
    IF EXISTS (SELECT FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'AspNetUsers' AND column_name = 'CreatedUtc') THEN
        GRANT SELECT ("CreatedUtc") ON "AspNetUsers" TO ember_dashboard;
    END IF;
    IF EXISTS (SELECT FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'AuthSessions' AND column_name = 'Platform') THEN
        GRANT SELECT ("Platform") ON "AuthSessions" TO ember_dashboard;
    END IF;
    IF EXISTS (SELECT FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'AuthSessions' AND column_name = 'AppVersion') THEN
        GRANT SELECT ("AppVersion") ON "AuthSessions" TO ember_dashboard;
    END IF;
END
$$;

COMMIT;
\echo 'ember_dashboard: role and grants applied'
