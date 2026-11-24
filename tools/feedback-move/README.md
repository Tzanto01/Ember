# Release day: feedback moves to Lookout

One-time steps for the release that drops Ember's feedback board. Run them in this order, in one session, from the new Ember checkout on the server, not during the nightly dump. Delete this folder and `ops/sql/mark_imported_feedback.sql` once they are done.

The check, used in steps 2 and 8:

```bash
LOOKOUT_AGENT_NAME=... LOOKOUT_AGENT_PROVIDER=... tools/feedback-move/check.sh ~/lookout > check.txt 2>&1; echo "exit $?"; cat check.txt
```

It reads only. For every report Ember has it looks for Lookout's ticket and compares the status, the title and text, the notes one by one, and the number and size of the pictures. `check.txt` holds ids, counts, sizes and statuses, never titles or texts. Its last line is `RESULT`; without it the check didn't finish.

- Exit 0: Lookout holds everything.
- Exit 2: it does, but `DIFFERS` lines say where a status, title or text differs. Lookout's is the one that counts. Look at each before going on.
- Exit 1: `MISSING` lines say what Lookout doesn't hold. **Don't go on.**
  - A note: copy it with `board.sh comment <ticket> <file>`, unchanged. The next check finds it.
  - A picture: `tools/feedback-images.sh` of the running version, then `board.sh picture-add`.
  - A report that was never queued: the running version can still import it. The command is in `git show 96d815c:TECHNICAL.md` (`--lookout-import`). Lookout takes it only while its registry has `importOpen: true` for `ember`.
  - Notes of a `parked` or `waiting` report: save them, Lookout has no ticket for it yet. The report itself stays in Ember until Lookout takes it, pictures included, for 12 months at most.
  - A ticket Nico deleted on Lookout's board, or a problem settled by hand: pass the report ids as the second argument (`check.sh ~/lookout 12,15`). They show as `ACCEPTED`.

**Before the deploy, with the old version still running**

1. Set `FEEDBACK_RETENTION_ENABLED=false` in the environment file and leave `FEEDBACK_DELETE_SENT_COPIES` unset. The new version then deletes no feedback until step 10. The old one knows neither setting and goes on deleting by its own rules (12 months; pictures 30 days after the report was resolved).
2. Run `python3 -m unittest discover tools/feedback-move`, then the check. Go on only with exit 0, or exit 2 and every difference understood.
3. Make sure last night's dump holds `emberdb` and `emberops`. After step 9 Lookout's store is the only place with the notes, and after step 10 the only one with most pictures. Check that last night's copy of Lookout's store exists (its own nightly backup).

**The deploy**

4. Deploy `ember-ops` first. The old one reads `Status` and reports the database as unavailable once the column is gone.
5. Stop `api` and `lookout-sender`, deploy Ember, and start the bot first: it applies the migrations. Then start `api` and `lookout-sender`. An old `api` or sender that still runs after the migration fails on the missing columns; the sender resends, nothing is lost.

**After the deploy**

6. Run `ops/sql/ember_dashboard.sql`, then `ops/sql/ember_lookout.sql` (each names its command in its head). Set `LOOKOUT_DB_USER=ember_lookout` and `LOOKOUT_DB_PASSWORD`, create the directory for the deleted refs, set `LOOKOUT_DELETED_REFS_FILE` and `LOOKOUT_DELETED_REFS_DIR` (see "Restoring Lookout's store" in `TECHNICAL.md`), and recreate `lookout-sender`. Its log must name no missing right and must not say that deletions aren't recorded. `SELECT DISTINCT usename FROM pg_stat_activity WHERE datname = 'emberdb'` must list `ember_lookout`: with a mistyped variable name the sender quietly stays on Ember's own login.
7. Run the check again. Statuses are no longer compared (the column is gone); everything else must be held. Then run `ops/sql/mark_imported_feedback.sql` with the ids of its `FEEDBACK_IDS` line. Until then Ember treats the imported reports as not confirmed and keeps their pictures for 12 months. If it refuses because a report is gone, run the check again and use the new ids.
8. Run the check once more. `never queued` may only be left on reports you decided about.
9. Run `ops/sql/drop_feedback_board.sql` right away. It deletes the notes in `emberops` for good. Until then the notes of a deleted account stay: `ember-ops` no longer deletes them.
10. Remove `FEEDBACK_RETENTION_ENABLED` and recreate `api`. The next run at 08:00 (Berlin) deletes, for good, reports older than 12 months and the pictures of confirmed reports older than 29 days. The pictures Lookout holds are listed with their sizes in the check of step 8.
11. Set `FEEDBACK_DELETE_SENT_COPIES=true` only once the deleted refs are being recorded and you saw the first deletion recorded in the file (the restore itself passed a rehearsal on synthetic data on 2026-11-24). Recreate `api` and `lookout-sender`.

**Going back.** Only before step 9, and only the columns `Status`, `ResolvedAt` and `DigestedAt` are gone by then; Lookout holds the status.

1. Stop `api`, `lookout-sender`, the bot and `ember-ops`.
2. From the new checkout, undo the two feedback migrations: `dotnet ef database update` with the migration before `AddFeedbackIntake` as the target. Every report then has the status `open` and counts as not digested, so the old version's next digest covers all of them (a count, no content).
3. Start the old images, and run the old `ops/sql/ember_dashboard.sql` for the old `ember-ops`.
4. The old sender records no deleted refs. **Before the new sender starts again, delete the deleted refs file**, so it starts over and says since when it is complete. Copies of Lookout's store from before that can then not be restored.
