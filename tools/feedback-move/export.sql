-- Read-only: every report in emberdb as one JSON array, without its text. Works before and after the board columns are dropped
-- (status is null then).
SELECT coalesce(json_agg(json_build_object(
    'id', f."Id",
    'platform', f."Platform",
    'version', f."Version",
    'createdAt', to_char(date_trunc('milliseconds', f."CreatedAt" AT TIME ZONE 'UTC'), 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'),
    'anonymous', f."UserId" IS NULL,
    'status', to_jsonb(f) ->> 'Status',
    'titleMd5', md5(f."Title"),
    'textMd5', md5(f."Text"),
    'pictures', (SELECT coalesce(json_agg(i."Size" ORDER BY i."Size"), '[]') FROM "FeedbackImages" i WHERE i."FeedbackId" = f."Id"),
    'delivery', (SELECT CASE WHEN o."SentAt" IS NOT NULL THEN 'sent' WHEN o."ParkedAt" IS NOT NULL THEN 'parked' ELSE 'waiting' END
                 FROM "LookoutOutbox" o WHERE o."FeedbackId" = f."Id" LIMIT 1)
) ORDER BY f."Id"), '[]')
FROM "Feedback" f;
