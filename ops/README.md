# Ember ops

The Lookout provider for Ember. It runs on the homeserver as one container (`ember-ops`, .NET 10). The [Lookout hub](https://github.com/Tzanto01/lookout) calls it over the Docker network `lookout-net` and draws its pages. The container publishes no host port and serves no UI. The provider contract is in the Lookout repo: `contract/provider-v1.md` (the hub's rules) and `contract/ember-provider-v1.md` (what Ember serves).

It reads the production database `emberdb` as the role `ember_dashboard`, which can only read the columns the pages need. Its own data (comments, card order, reminder history, the notice log) lives in the database `emberops`.

## Pages

The hub asks for these pages at `/lookout/v1/pages/{pageId}`. Each one is built from the data below. Lookout draws the charts, cards and board.

| Page | What it shows |
|---|---|
| `overview` | Users (total, active in the last 7 and 30 days by sign-ins, new per week by first habit), habits (build and let-go, paused, with reminder), routines, streak settings |
| `reminders` | Reminders still open per day and channel, sent reminders per day from the dashboard's own snapshots, Discord DMs on, failing and paused, second reminders, push subscriptions and phones, overdue reminders |
| `feedback` | The reports, one column per Ember status (open, planned, implemented, released, declined). Each card has its description, pictures, platform, version, send time, whether it was sent anonymously, and its comments. A move sets the status and the card position. Moves are not logged |
| `notices` | Live, scheduled and ended notices: info or maintenance, English and German, up to 280 characters each, up to 10 notices in a 16 KiB document. Publish, end now or delete. Each change is written to `NoticeLog` in `emberops` with the author |
| `system` | Container states, whether reminders are advancing, `emberdb` size and latest migration, free space per disk, newest pre-deploy dump, deployed SHA of production and staging, the provider's build SHA, CI of `main`, `test` and `v2/next`, the live notice, the published APK version |

The overview, reminders, notices and system pages refresh every 30 seconds. The feedback page has no timer (`refreshSeconds: 0` in the manifest). The server computes counters at most every 20 seconds and the 12-week series at most every 5 minutes, shared by all viewers.

Limits of the data:
- Ember stores no sign-up date, so "new users" is the week of a user's first habit.
- "Active" means a web or app sign-in was used in the window (`AuthSessions`, last seen or created). Ember deletes a session when it expires or signs out, and Discord-only use has no sign-in, so both undercount.
- The dashboard reads no check-ins, journal, low days, let-go slips or Fireside data: those are health data.
- Ember deletes a reminder row once it closes. "Still open" counts only the rows Ember still keeps. "Sent" comes from the dashboard's snapshots of the reminder id sequences every 5 minutes, so it starts when the dashboard was first deployed and includes second reminders and resends.
- Days are Europe/Berlin days.

## Privacy

Aggregates only. The database role enforces it: `ember_dashboard` has column-level `SELECT` on ids, dates, counts, flags and kinds, plus the feedback columns the board shows, and `UPDATE` on `Feedback` (`Status`, `ResolvedAt`) only. It cannot read names, emails, hashes, tokens, push endpoints, or any check-in, journal, mood, low-day, let-go slip or Fireside table. Re-running the script revokes grants older versions gave on those tables. The full column list is in [sql/ember_dashboard.sql](sql/ember_dashboard.sql).

`Feedback.UserId` is readable only so the board can say whether a report was anonymous. The app uses it only as `"UserId" IS NULL` and never returns it.

Moving a card to released or declined sets `ResolvedAt` the same way `--feedback-status` does, so Ember deletes the pictures 30 days later. Moving it back clears `ResolvedAt`.

## Access

`GET /healthz` is open and returns `ok`. Everything under `/lookout` is for the Lookout hub only (provider contract v1). Any other path returns 404. The app checks a request under `/lookout` in this order:

1. The TCP peer must be `Ops__Lookout__HubAddress`, the hub at `172.31.250.18` on `lookout-net`. Otherwise 403.
2. `Authorization: Bearer <key>` must equal the key in `Ops__Lookout__KeyFile`, compared in constant time. The file is read on every request. A key under 32 bytes, an empty or missing file, or no key file set means every request gets 401.
3. The path must match a Lookout route. Otherwise 404 with a problem body.

403 and 401 have no body.

Writes take the author from the `Lookout-Actor` header. It must appear once, percent-decoded and trimmed, with 1 to 100 characters and no control characters. Otherwise the write gets 400.

Routes, all under `/lookout/v1`:

| Method and path | What it does |
|---|---|
| `GET /manifest` | Name, build SHA as `version`, pages and refresh times |
| `GET /summary` | The summary card: four tiles, a state and up to five alerts |
| `GET /pages/{pageId}` | One page: `overview`, `reminders`, `feedback`, `notices` or `system`. Other ids get 404 |
| `GET /widgets/feedback/cards/{id}/comments` | Comments on a report |
| `POST /widgets/feedback/cards/{id}/comments` | Add a comment (201). Needs `Lookout-Actor`. 1 to 4000 characters, no NUL |
| `PUT /widgets/feedback/cards/{id}/move` | Set a report's status and position (204). Needs `Lookout-Actor` |
| `GET /widgets/feedback/cards/{id}/images/{imageId}` | One picture |
| `POST /widgets/notices/notices` | Publish a notice. Replaces the notice with the same id. Needs `Lookout-Actor` |
| `POST /widgets/notices/notices/{id}/end` | End a notice now (409 when it has already ended). Needs `Lookout-Actor` |
| `DELETE /widgets/notices/notices/{id}` | Delete a notice. Needs `Lookout-Actor` |

The container joins `homeserver-net` (Postgres, and outbound calls to GitHub and beta.ember-glow.app) and `lookout-net` at the fixed address `172.31.250.19`. The hub calls `http://172.31.250.19:8080`. `AllowedHosts` lists `localhost`, `127.0.0.1` and `172.31.250.19`.

### Publishing notices

Publishing a notice sends the whole `notices.json` document to `PUT https://beta.ember-glow.app/_ops/notices` with the token from `~/.config/ember-ops/notices-token`. Writes go one at a time. For 90 seconds after a write, a read that still shows the document it replaced (the edge caches reads up to 60 seconds) counts as the document it wrote, until a read shows that document; any other read, such as a change from Hosting's `publish-ember-notice.sh`, counts as live. In those 90 seconds a CLI change the edge hasn't served yet, or one that restores exactly the replaced document, can still be overwritten, since the endpoint is last writer wins.

## Run it on the homeserver

### One-time setup

1. Passwords: create `~/.config/ember-ops/ember_dashboard` and `~/.config/ember-ops/emberops` (one random password each, at least 16 characters, mode 600, directory mode 700). Hosting writes the notices token to `~/.config/ember-ops/notices-token` (mode 600); the container does not start without it. To rotate it, overwrite the file in place or run `docker compose -f ops/compose.yml up -d --force-recreate`, because the mount follows the original file.
2. Lookout: Lookout creates the network `lookout-net` and the key `~/.config/lookout/providers/ember.key` (mode 600, directory mode 700). `up` needs both to exist. The key is mounted as a secret. With an empty or short key, every Lookout request gets 401.
3. Database: a superuser runs the two scripts from the repo root. Each prints one line when done.

   ```bash
   OPS_PW="$(cat ~/.config/ember-ops/emberops)" docker exec -i -e OPS_PW homeserver-postgres psql -U finvarex -d postgres -f - < ops/sql/emberops.sql
   DASH_PW="$(cat ~/.config/ember-ops/ember_dashboard)" docker exec -i -e DASH_PW homeserver-postgres psql -U finvarex -d emberdb -f - < ops/sql/ember_dashboard.sql
   ```

   Both scripts can run again safely. Re-run `ember_dashboard.sql` after an Ember release that adds tables or columns the dashboard reads, then restart the container.
4. Checkout: `git clone --branch v2/next-ops <Ember repo> ~/ember-ops`.
5. Host status timer: it writes `~/.local/state/ember-ops/status.json` every minute (free space of every local disk, newest dump, deployed SHAs, container states; it needs `jq`). The container reads only that folder, read-only.

   ```bash
   mkdir -p ~/.config/systemd/user
   ln -sf ~/ember-ops/ops/host/ops-status.service ~/ember-ops/ops/host/ops-status.timer ~/.config/systemd/user/
   systemctl --user daemon-reload && systemctl --user enable --now ops-status.timer
   ```

### Start, update, stop

From `~/ember-ops`:

```bash
# start or update
git pull && BUILD_SHA=$(git rev-parse --short HEAD) docker compose -f ops/compose.yml up -d --build
# check
docker ps --filter name=ember-ops
docker inspect --format '{{.State.Health.Status}}' ember-ops   # healthy
docker exec ember-ops dotnet /app/Ember.Ops.dll --healthcheck; echo $?   # 0 when /healthz answers
# logs
docker logs --tail 50 ember-ops
# stop (keeps both databases)
docker compose -f ops/compose.yml down
systemctl --user disable --now ops-status.timer
```

The container publishes no host port. Any peer on its networks can reach port 8080, and the peer and key checks above decide who gets into `/lookout`. The contract check under Develop covers the same routes.

After the first deploy of this version, nothing uses the network `ember-ops-edge`. Once `docker network inspect ember-ops-edge` lists no containers, remove it with `docker network rm ember-ops-edge`.

## Develop

Everything runs from the repo root. The solution is `ops/Ember.Ops.slnx`, separate from Ember's.

| Task | Command |
|---|---|
| Build | `dotnet build ops/Ember.Ops.slnx` |
| .NET tests (need Docker) | `dotnet test ops/Ember.Ops.slnx` |
| Test host on http://127.0.0.1:5185 | `dotnet run --project ops/tests/Ember.Ops.TestHost` (add `-- --prod-schema` for production's schema) |

The test host starts a throwaway PostgreSQL container (Testcontainers, needs Docker), applies Ember's migrations and both SQL scripts, and seeds synthetic data. Notices go to an in-process fake endpoint with a random token, never to the real one. Its Lookout routes accept the hub only from `127.0.0.1`, so call it as `http://127.0.0.1:5185`, not `localhost`. Its key comes from the file named by `LOOKOUT_KEY_FILE`. Without that variable it uses a built-in synthetic key.

To run the Lookout contract check against the test host, create a key file outside the repo, start the test host with it, then run the check from the Lookout repo's `web/` folder (after `bun install`):

```bash
D=$(mktemp -d); (umask 077; head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n' > "$D/test.key")
LOOKOUT_KEY_FILE="$D/test.key" dotnet run --project ops/tests/Ember.Ops.TestHost
# in the Lookout repo's web/ folder, with the same key file:
bun run contract:check http://127.0.0.1:5185 "$D/test.key"
```

The .NET tests run both SQL scripts on the full schema and on production's schema, check what `ember_dashboard` can and cannot read, count the aggregates against a hand-counted dataset, and cover the Lookout gate, routes and notice rules.

## Configuration

Set through environment variables in [compose.yml](compose.yml).

| Variable | Purpose |
|---|---|
| `Ops__EmberConnection`, `Ops__EmberPasswordFile` | Connection to `emberdb` as `ember_dashboard`, password from a file |
| `Ops__OpsConnection`, `Ops__OpsPasswordFile` | Connection to `emberops` as `emberops`. The app creates its tables on startup |
| `AllowedHosts` | Host names the app answers to |
| `Ops__StatusFile` | The host status file |
| `Ops__GitHubRepo`, `Ops__NoticesUrl`, `Ops__ApkUrl` | Sources for CI, the live notice and the APK version. Left empty, the section is skipped. CI makes one GitHub request every 10 minutes with `If-None-Match` |
| `Ops__Lookout__HubAddress` | Address of the Lookout hub, the only peer allowed on `/lookout`. Empty means every Lookout request gets 403 |
| `Ops__Lookout__KeyFile` | File with the key the hub sends as bearer. Read on every request; without a key of at least 32 bytes every Lookout request gets 401 |
| `Ops__NoticesPublishUrl`, `Ops__NoticesTokenFile` | Where notices are published (HTTPS only) and the file with its bearer token. Without a token, or unless both URLs are HTTPS, the Notices page is read-only and writes get 503 |
