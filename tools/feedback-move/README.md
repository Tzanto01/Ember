# Release day: feedback moves to Lookout

One-time steps for the release that drops Ember's feedback board. Run them in this order from the Ember checkout on the server. Delete this folder and `ops/sql/mark_imported_feedback.sql` once they are done.

**Before the deploy, with the old version still running**

1. Set `FEEDBACK_RETENTION_ENABLED=false` in the environment file and leave `FEEDBACK_DELETE_SENT_COPIES` unset. Nothing is deleted until step 10.
2. Run `LOOKOUT_AGENT_NAME=… LOOKOUT_AGENT_PROVIDER=… tools/feedback-move/check.sh ~/projects/lookout | tee check.txt`. It reads only. For every report Ember has it looks for Lookout's ticket and compares the status, the text, the number of notes, and the number and size of the pictures.
   - Exit 0: Lookout holds everything.
   - Exit 2: it does, but the status or text of some reports differs (`DIFFERS` lines). Lookout's is the one that counts. Look at each before going on.
   - Exit 1: something is missing (`MISSING` lines). **Don't deploy.** Reports that were never queued can still be imported with `--lookout-import` of the running version. Notes and pictures of a ticket that exists go to Lookout by hand (`board.sh comment`, `board.sh picture-add`); a note added that way still shows as missing, so check it off in the output.
   - `waiting` and `parked` reports aren't in Lookout. Ember keeps them, pictures included, for 12 months at most. Read a parked one with `--list-feedback` after the deploy.
3. Keep `check.txt`: its last line names the ids for step 7. It holds no titles or texts.

**The deploy**

4. Deploy `ember-ops` first. The old one reads `Status` and reports the database as unavailable once the column is gone.
5. Deploy Ember. The bot applies the migrations at startup. Between the migration and the restart of `api` and `lookout-sender`, sending feedback fails; keep that gap short.

**After the deploy**

6. Run `ops/sql/ember_dashboard.sql`, then `ops/sql/ember_lookout.sql` (each names its command in its head). Set `LOOKOUT_DB_USER=ember_lookout` and `LOOKOUT_DB_PASSWORD`, create the directory for the deleted refs, set `LOOKOUT_DELETED_REFS_FILE` and `LOOKOUT_DELETED_REFS_DIR` (see "Restoring Lookout's store" in `TECHNICAL.md`), and recreate `lookout-sender`. Its log must not name a missing right.
7. Run `ops/sql/mark_imported_feedback.sql` with the ids from step 3. Until then Ember treats the imported reports as not confirmed and keeps their pictures for 12 months.
8. Run `check.sh` again. Statuses are no longer compared (the column is gone); reports, notes, texts and pictures must still be held. New reports since step 2 show as `waiting` or `held`.
9. Run `ops/sql/drop_feedback_board.sql`. It deletes the notes in `emberops` for good, so only after step 8 passed. Don't leave it open: `ember-ops` no longer deletes notes of reports that are gone.
10. Remove `FEEDBACK_RETENTION_ENABLED` and recreate `api`. The next run at 08:00 (Berlin) deletes reports older than 12 months, and pictures of confirmed reports older than 29 days, for good.
11. Set `FEEDBACK_DELETE_SENT_COPIES=true` only once a backup of Lookout's store was restored for real and the deleted refs are being recorded. Recreate `api` and `lookout-sender`.

**Going back.** Until step 9 nothing but the three columns `Status`, `ResolvedAt` and `DigestedAt` is gone, and Lookout holds the status. Going back to the old version means reverting the migration with `dotnet ef database update` (every report then has the status `open`) before the old images start.
