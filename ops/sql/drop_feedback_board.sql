-- Drops the old feedback board's tables comments and card_positions from emberops. This deletes the operator notes for good.
-- Run by hand, only after the release-day check confirmed that Lookout holds every note and the ember-ops version without the
-- feedback page is running. Idempotent. Refuses any database other than emberops.
-- Run as superuser from the repo root:
--   docker exec -i homeserver-postgres psql -U finvarex -d emberops -f - < ops/sql/drop_feedback_board.sql

\set ON_ERROR_STOP on
\set QUIET on

BEGIN;
DO $$
BEGIN
    IF current_database() <> 'emberops' THEN
        RAISE EXCEPTION 'connected to %, not emberops', current_database();
    END IF;
END
$$;
DROP TABLE IF EXISTS public.comments;
DROP TABLE IF EXISTS public.card_positions;
COMMIT;
\echo 'emberops: comments and card_positions dropped'
