# Ember ops dashboard

A private dashboard for running Ember: live stats, a feedback board with comments, live notices, and system health. It runs on the homeserver as one container (`ember-ops`, .NET 10 API plus a React app), bound to `127.0.0.1:8096`, and is reachable only over the tailnet through Tailscale Serve.

It reads the production database `emberdb` as the role `ember_dashboard`, which can only read the columns the dashboard needs. Its own data (board comments, card order, reminder history) lives in the database `emberops`.

## Pages

| Page | What it shows |
|---|---|
| Overview | Users (total, active in the last 7 and 30 days by completed check-ins, new per week by first habit), check-ins per day (90 days), habits (build and let-go, paused, with reminder), routines, journal days, low days, streak settings, Fireside (users on, friendships, warmth per day), let-go slips per week |
| Reminders | Reminders still open per day and channel, sent reminders per day from the dashboard's own snapshots, Discord DMs on, failing and paused, second reminders, push subscriptions and phones, overdue reminders |
| Feedback | Kanban board with the Ember statuses (open, planned, implemented, released, declined). Drag a card or use its Move menu to change the status. Card details show the description, pictures, platform, version, send time and whether it was sent anonymously. Comments are kept per card. Filter by status and platform, search title and description |
| Notices | Publish a live notice (info or maintenance, English and German, up to 280 characters each, optional show from and until in your local time) with a preview like the app banner and a confirm step. Lists live, scheduled and ended notices with End now and Delete. Each change is written to `NoticeLog` in `emberops` with the author |
| System | Container states, whether reminders are advancing, `emberdb` size and latest migration, disk free, newest pre-deploy dump, deployed SHA of production and staging, CI of `main`, `test` and `v2/next`, the live notice, the published APK version |

The stat pages refresh every 30 seconds. The server computes counters at most every 20 seconds and the 90-day and 12-week series at most every 5 minutes, shared by all viewers.

Limits of the data:
- Ember stores no sign-up date, so "new users" is the week of a user's first habit.
- Ember deletes a reminder row once it closes. "Still open" counts only the rows Ember still keeps. "Sent" comes from the dashboard's snapshots of the reminder id sequences every 5 minutes, so it starts when the dashboard was first deployed and includes second reminders and resends.
- Fireside and let-go sections show "Not on production yet" until those tables reach production and the grant script is re-run.
- Days are Europe/Berlin days.

## Privacy

Aggregates only. The database role enforces it: `ember_dashboard` has column-level `SELECT` on ids, dates, counts, flags and kinds, plus the feedback columns the board shows, and `UPDATE` on `Feedback` (`Status`, `ResolvedAt`) only. It cannot read names, emails, hashes, tokens, journal text, moods, Fireside names or messages, or push endpoints. The full column list is in [sql/ember_dashboard.sql](sql/ember_dashboard.sql).

`Feedback.UserId` is readable only so the board can say whether a report was anonymous. The app uses it only as `"UserId" IS NULL` and never returns it.

Moving a card to released or declined sets `ResolvedAt` the same way `--feedback-status` does, so Ember deletes the pictures 30 days later. Moving it back clears `ResolvedAt`.

## Access

Every request needs a Tailscale identity, otherwise the app answers 403 (only `GET /healthz` is open, it returns `ok`). The app accepts the identity headers only when the request comes from the trusted proxy address, the gateway of the container's own network `ember-ops-edge` (`172.31.250.1`). That is the address Docker uses for connections to the `127.0.0.1:8096` publish, so only processes on the host, such as Tailscale Serve, can send them. Other containers on `homeserver-net` connect from their own address and get 403.

A request passes when one of these holds:
- `Tailscale-User-Login` is in `Ops__Access__AllowedLogins` (Tailscale Serve sets it for user-owned devices).
- `Tailscale-App-Capabilities` contains the capability `Ops__Access__AppCapability`. Tailscale sends no login for tagged devices, and all of Nico's devices are tagged, so this is the path in use. The comment author is the capability's `author` value.

Publishing a notice sends the whole `notices.json` document to `PUT https://beta.ember-glow.app/_ops/notices` with the token from `~/.config/ember-ops/notices-token`. Writes go one at a time. For 90 seconds after a write, a read that still shows the document it replaced (the edge caches reads up to 60 seconds) counts as the document it wrote, until a read shows that document; any other read, such as a change from Hosting's `publish-ember-notice.sh`, counts as live. In those 90 seconds a CLI change the edge hasn't served yet, or one that restores exactly the replaced document, can still be overwritten, since the endpoint is last writer wins.

Writes also need the antiforgery token from `GET /api/session` in the `X-CSRF-TOKEN` header, and are refused when `Sec-Fetch-Site` is cross-site.

Everything under `/lookout` is for the Lookout hub only (provider contract v1) and skips the Tailscale check: the TCP peer must be `Ops__Lookout__HubAddress` (the hub on `lookout-net`, `172.31.250.18`), else 403, and `Authorization: Bearer` must equal the key in `Ops__Lookout__KeyFile`, else 401. The key file is read on every request; unset, missing or under 32 bytes means every Lookout request gets 401. A path under `/lookout` that is not a Lookout route gets 404. Requests through Tailscale Serve get 403 there. Lookout writes take their author from the `Lookout-Actor` header and need no antiforgery token.

### Tailscale setup (one time, Nico)

1. In the Tailscale admin console, add this to the policy file's `grants`:

   ```json
   {
     "src": ["tag:pc", "tag:phone", "tag:worklaptop"],
     "dst": ["tag:homeserver"],
     "ip": ["tcp:8096"],
     "app": { "nicowoellnitz.de/cap/ember-ops": [{ "author": "nico" }] }
   }
   ```

2. On the homeserver, add the Serve route. It adds port 8096 and leaves the other Serve routes alone:

   ```bash
   tailscale serve --bg --https=8096 --accept-app-caps=nicowoellnitz.de/cap/ember-ops http://127.0.0.1:8096
   ```

The dashboard is then at `https://nicos-homeserver.meteor-pauling.ts.net:8096`.

Keep these true, or the trusted proxy address means less:
- Serve for this port stays an HTTP proxy (`--https`), never `--tcp` or `--tls-terminated-tcp`.
- The homeserver advertises no subnet route that covers `172.31.250.0/29` or `172.19.0.0/16` (check `tailscale debug prefs`, `AdvertiseRoutes`).
- Nothing else on the host forwards traffic to `127.0.0.1:8096`. Any local process that can reach that port can send identity headers, and with them publish notices to every Ember user.

## Run it on the homeserver

### One-time setup

1. Passwords: create `~/.config/ember-ops/ember_dashboard` and `~/.config/ember-ops/emberops` (one random password each, at least 16 characters, mode 600, directory mode 700). Hosting writes the notices token to `~/.config/ember-ops/notices-token` (mode 600); the container does not start without it. To rotate it, overwrite the file in place or run `docker compose -f ops/compose.yml up -d --force-recreate`, because the mount follows the original file.
2. Database: a superuser runs the two scripts from the repo root. Each prints one line when done.

   ```bash
   OPS_PW="$(cat ~/.config/ember-ops/emberops)" docker exec -i -e OPS_PW homeserver-postgres psql -U finvarex -d postgres -f - < ops/sql/emberops.sql
   DASH_PW="$(cat ~/.config/ember-ops/ember_dashboard)" docker exec -i -e DASH_PW homeserver-postgres psql -U finvarex -d emberdb -f - < ops/sql/ember_dashboard.sql
   ```

   Both scripts can run again safely. Re-run `ember_dashboard.sql` after an Ember release that adds tables or columns the dashboard reads, then restart the container.
3. Checkout: `git clone --branch v2/next-ops <Ember repo> ~/ember-ops`.
4. Host status timer: it writes `~/.local/state/ember-ops/status.json` every minute (disk, newest dump, deployed SHAs, container states; it needs `jq`). The container reads only that folder, read-only.

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
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8096/api/overview   # 403 without identity
# logs
docker logs --tail 50 ember-ops
# stop (keeps both databases)
docker compose -f ops/compose.yml down
systemctl --user disable --now ops-status.timer
```

## Develop

Everything runs from the repo root. The solution is `ops/Ember.Ops.slnx`, separate from Ember's.

| Task | Command |
|---|---|
| Build | `dotnet build ops/Ember.Ops.slnx` |
| .NET tests (need Docker) | `dotnet test ops/Ember.Ops.slnx` |
| Web install, build, unit tests | `cd ops/web && bun install --frozen-lockfile && bun --bun run build && bun --bun run test` |
| Seeded test host on http://localhost:5185 | `dotnet run --project ops/tests/Ember.Ops.TestHost` (add `-- --prod-schema` for production's schema) |
| Web dev server on port 4204 (proxies `/api` to 5185) | `cd ops/web && bun --bun run dev` |
| Browser test, against a fresh test host | `cd ops/web && E2E_BASE_URL=http://localhost:5185 bunx playwright test` |

The test host starts a throwaway PostgreSQL container, applies Ember's migrations and both SQL scripts, seeds synthetic data and accepts `Tailscale-User-Login: e2e@example.test` from localhost. Notices go to an in-process fake endpoint with a random token, never to the real one. Send that header from the browser with an extension, or use the dev server. The browser test changes the seeded board, so restart the test host before running it again.

The .NET tests run both SQL scripts on the full schema and on production's schema, check what `ember_dashboard` can and cannot read, the aggregates against a hand-counted dataset, the access rules and CSRF.

## Configuration

Set through environment variables in [compose.yml](compose.yml).

| Variable | Purpose |
|---|---|
| `Ops__EmberConnection`, `Ops__EmberPasswordFile` | Connection to `emberdb` as `ember_dashboard`, password from a file |
| `Ops__OpsConnection`, `Ops__OpsPasswordFile` | Connection to `emberops` as `emberops`. The app creates its tables on startup |
| `Ops__Access__TrustedProxies__N` | Addresses whose identity headers count. Empty means every request gets 403 |
| `Ops__Access__AllowedLogins__N` | Tailscale logins that get in |
| `Ops__Access__AppCapability` | Tailscale app capability that gets in |
| `AllowedHosts` | Host names the app answers to |
| `Ops__StatusFile` | The host status file |
| `Ops__GitHubRepo`, `Ops__NoticesUrl`, `Ops__ApkUrl` | Sources for CI, the live notice and the APK version. Left empty, the section is skipped. CI makes one GitHub request every 10 minutes with `If-None-Match` |
| `Ops__Lookout__HubAddress` | Address of the Lookout hub, the only peer allowed on `/lookout`. Empty means every Lookout request gets 403 |
| `Ops__Lookout__KeyFile` | File with the key the hub sends as bearer. Read on every request; without a key of at least 32 bytes every Lookout request gets 401 |
| `Ops__NoticesPublishUrl`, `Ops__NoticesTokenFile` | Where notices are published (HTTPS only) and the file with its bearer token. Without a token, or unless both URLs are HTTPS, the Notices page is read-only and writes get 503 |
