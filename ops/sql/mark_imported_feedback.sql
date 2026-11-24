-- Records that Lookout holds the reports in FEEDBACK_IDS (comma-separated) that were imported before the outbox existed and so have no
-- outbox row: each gets a sent one, like a report the sender delivered. Deletes nothing itself, but Ember's retention then treats the
-- report as confirmed. Run only with the ids that tools/feedback-move/check.sh printed. Idempotent; changes nothing when an id is unknown.
-- Run as superuser from the repo root:
--   FEEDBACK_IDS=1,2,3 docker exec -i -e FEEDBACK_IDS homeserver-postgres psql -U finvarex -d emberdb -f - < ops/sql/mark_imported_feedback.sql

\set ON_ERROR_STOP on
\set QUIET on
\getenv ids FEEDBACK_IDS
\if :{?ids}
\else
DO $$ BEGIN RAISE EXCEPTION 'FEEDBACK_IDS is not set'; END $$;
\endif

BEGIN;
SELECT :'ids' <> '' AND NOT EXISTS (SELECT FROM unnest(string_to_array(:'ids', ',')::int[]) id WHERE NOT EXISTS (SELECT FROM "Feedback" WHERE "Id" = id)) AS all_known \gset
\if :all_known
\else
DO $$ BEGIN RAISE EXCEPTION 'FEEDBACK_IDS is empty or names a report that does not exist'; END $$;
\endif
INSERT INTO "LookoutOutbox" ("Kind", "Key", "FeedbackId", "UserRef", "CreatedAt", "SentAt")
SELECT 'ticket', 'import-feedback-' || f."Id", f."Id",
    (SELECT r."Ref" FROM "LookoutUserRefs" r WHERE r."UserId" = f."UserId" ORDER BY r."CreatedAt", r."Ref" LIMIT 1), now(), now()
FROM "Feedback" f
WHERE f."Id" = ANY (string_to_array(:'ids', ',')::int[]) AND NOT EXISTS (SELECT FROM "LookoutOutbox" o WHERE o."FeedbackId" = f."Id");
COMMIT;
\echo 'mark_imported_feedback: done'
