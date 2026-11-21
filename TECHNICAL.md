# Ember: technical notes

Everything about the Discord commands, running your own instance and working on the code. For what Ember is and how to get it, see the [README](README.md).

## Discord commands

Most commands open a menu or buttons. Three take an option: `/export`, `/timezone set` and `/habit second-reminder`.

### Habits

| Command | What it does |
|---|---|
| `/habit add` | Create a new habit in a form |
| `/habit template` | Start from a ready-made habit |
| `/habit checkin` | Log today's check-in |
| `/habit history` | See the last 14 days of a habit, fill in or fix any day, and mark low days |
| `/habit list` | See all habits with their progress and reminders |
| `/habit streak` | See the full stats: streak, 7 days, 30 days, best and total |
| `/habit share` | Post a public streak card in the current channel |
| `/habit edit` | Rename a habit, change its reminder time or remove the reminder |
| `/habit delete` | Remove a habit and stop tracking it |
| `/habit pause` | Pause a habit: today only, 3 days or 1 week, or resume it early. Reminders stop and the streak is kept |
| `/habit frequency` | Set daily, 1 to 6 times a week, specific weekdays, or how many times a day (up to 10) |
| `/habit grace` | Set how many flex days a week you get before a streak breaks (0 to 3) |
| `/habit second-reminder` | Get one more nudge after a reminder if you haven't checked in yet (15 minutes, 30 minutes, 1 hour or 2 hours after, or off) |

### Timezone

| Command | What it does |
|---|---|
| `/timezone set` | Set your local timezone (start typing your city or region) |
| `/timezone show` | See your current timezone |

> Set your timezone before adding reminders so they arrive at the right local time.

### Privacy and your data

| Command | What it does |
|---|---|
| `/export [format: csv\|json]` | Get your settings, habits, check-ins, low days and routines as CSV (default) or JSON in your DMs. Day notes, moods and account details are never sent through Discord; the full export is in the web app's Settings |
| `/privacy delete` | Permanently delete everything Ember stores about you, after you confirm with a button. An account with a password or two-step sign-in is deleted in the web app's Settings instead, and the bot links there |

### Other

| Command | What it does |
|---|---|
| `/help` | See all commands in one place |
| `/link` | Open the web app (Sign in with Discord uses the same account), or link Discord to an email account and optionally move your habits there |
| `/feedback` | Tell the Ember team what would make Ember better: a title, a description and up to 5 pictures (JPEG, PNG or WebP, 10 MB together), with your Ember account or anonymously (up to 5 per hour) |

<details>
<summary>How days, reminders and consent work</summary>

- **Backfill.** `/habit history` shows the last 14 days of a habit as buttons. Each tap moves a day on: not logged, then one more check-in per tap until the day is done, then not done, then not logged again. Days before the habit started are greyed out. The web app's History goes back month by month to the habit's start.
- **Open days.** Today and yesterday stay open. An open day that isn't logged yet doesn't count against a streak. It only becomes a gap once it closes.
- **Low days.** Pick a day in the 🌙 menu under the history grid, or tap **It was a low day** in the "Log yesterday" DM. A low day covers all your habits. It doesn't break or extend a streak, and it counts as kept in the 7 and 30 day views. Pick it again to remove it.
- **Reminder buttons** stop working at 10:00 the next morning, or once that day is logged anywhere (Discord or the web), whichever comes first. A routine's reminder also stops once every habit in it that's due that day is done; habits on a rest day, paused or on a low day don't hold it open. Then the message says `✅ Logged.` or `This reminder has closed.` You can still fill in the day with `/habit history` or on the web.
- **Second reminder.** `/habit second-reminder` sends one more nudge after a reminder if the day is still open and the habit isn't done. It replaces the first message, and there is never a third. **Skip for today** and **Snooze 1h** cancel a pending nudge.
- **Where reminders go.** Every reminder goes to each place you switched on in Settings under "Where Ember reminds you": Discord DMs (Ember asks once before it DMs you), each browser and each phone running the app. Each habit and routine can skip some of them under "Remind me on". Logging anywhere clears the browser notification too.
- **Snooze.** **Snooze 1h** moves that reminder one hour later. It only works if the snoozed reminder still falls on the same local day, and within 24 hours of the original.
- **Done outside Discord.** A web push reminder for a single habit has a **Done ✅** button that logs the day without opening Ember. The notification doesn't name the habit. The Android app's single habit reminders have **Done** too, even when habit names are hidden. Names are off by default.
- **Reminders that fail.** A send that fails for a temporary reason (a timeout, a network error, or Discord answering 5xx or 429) is tried up to two more times, 5 minutes apart. If 3 DMs in a row can't be delivered, your reminders pause. Ember tells you the next time you use it, with a **Try again** button. Reminders more than 2 hours late, for example after downtime, are skipped.
- **Consent.** Habit names, check-ins, low days, day notes and moods can reveal things about your health, so Ember asks for your consent before it stores them. It asks again when the consent text changes. If you choose **No thanks**, the Discord prompt waits 30 days before it asks again. You can withdraw by deleting your data with `/privacy delete`.
- **Unconfirmed accounts.** A web signup whose email is never confirmed is deleted after 7 days, unless it has a linked login or any data.

</details>

## Self-hosting

You can run your own instance from this source code. Ember has two containers:

- `ember`, the Discord bot (`docker/bot.Dockerfile`).
- `api`, the HTTP API (`docker/api.Dockerfile`). It also serves the built web app. Only the web app and the Android app need it.

You need:

- Docker with Compose.
- A Discord application with a bot user. For sign-in with Discord, add this OAuth2 redirect URI: `<WEB_ORIGIN>/api/auth/discord/callback`.
- A PostgreSQL server that the containers reach as host `postgres` on an existing external Docker network called `homeserver-net`. The database is `emberdb`.

Steps:

1. Create a `.env` file next to `docker-compose.yml`. Git ignores it. Fill in the variables below.
2. Start the bot: `docker compose up -d --build`. On startup the bot applies pending database migrations and registers its slash commands.
3. Optional. Start the API and the web app: `docker compose --profile api up -d --build`. The API doesn't apply migrations, so start the bot once on a new database before the API.
4. Check the API: `docker compose ps` shows its health status. The health check calls the API's `/health` endpoint, which also checks the database.

Back up the database before you deploy a build with a new migration.

| Variable | Purpose |
|---|---|
| `BOT_TOKEN` | Discord bot token. Required by `ember`. |
| `DB_USER`, `DB_PASSWORD` | PostgreSQL login for `emberdb`. |
| `DEV_GUILD_ID` | Optional. Registers the slash commands on this server right away. Ember also registers them globally. |
| `PRIVACY_POLICY_URL` | Optional. Privacy policy link shown in the consent prompt and in `/help`. If it ends in `/legal/privacy`, `/help` also links the imprint at `/legal/imprint` on the same site. |
| `WEB_ORIGIN` | Public `https://` address of the web app, for example `https://example.com`. Required by `api`. The bot uses it for links, and `api` uses it for links in emails and for CORS. |
| `VAPID_PUBLIC_KEY`, `VAPID_PRIVATE_KEY`, `VAPID_SUBJECT` | Optional. Web push keys and a contact address. Set all three or none. Without them, no web push reminders are sent. The API serves the public key to the web app. |
| `API_KNOWN_PROXY`, `API_KNOWN_NETWORK` | `api` only. Set at least one. The reverse proxy address, or the network it sits in (CIDR), whose forwarded client IP the API trusts. See below. |
| `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD`, `SMTP_FROM` | `api` only. Mail server for account emails. `SMTP_HOST` and `SMTP_FROM` are required. The port defaults to 587. |
| `SMTP_REPLY_TO` | `api` only, optional. `Reply-To` address for account emails. Without it, replies go to `SMTP_FROM`. |
| `FEEDBACK_DIGEST_TO` | `api` only, optional. Address that gets the daily feedback digest (08:00 Europe/Berlin, only when there is new feedback) through the SMTP settings above. Without it, feedback is stored but not mailed. |
| `EMBER_DELETION_LEDGER_FILE` | `ember` and `api`, optional. The deletion ledger inside the containers, `/ledger/deletions.jsonl` on production. Without it Ember keeps no ledger. See "Restoring a backup" below. |
| `EMBER_DELETION_LEDGER_DIR` | Optional. Host directory mounted at `/ledger` in `ember` and `api`. Defaults to `./deletion-ledger`. Set it together with `EMBER_DELETION_LEDGER_FILE`: otherwise Docker creates the default directory owned by root and every ledger write fails. |
| `LOOKOUT_INGEST_URL` | `lookout-sender` only, optional. Lookout's ingest address as an IP literal, `http://172.31.250.18:8081` on the homeserver. Set it only on production; without it the sender sends nothing. |
| `DISCORD_CLIENT_ID`, `DISCORD_CLIENT_SECRET` | `api` only. Discord OAuth application for signing in with Discord and linking it, on the web and in the Android app. |
| `VITE_PUBLIC_ORIGIN` | Optional build argument for the `api` image. Your public origin, for example `https://example.com`. It adds a canonical link and a sitemap to the web build. |

**Client IP behind a tunnel.** Rate limits are keyed on the client IP. The API only reads it from `X-Forwarded-For` when the request comes from a trusted proxy. From anywhere else it ignores the header, and then every visitor shares one limit. If cloudflared runs as a container on `homeserver-net`, Docker assigns its address and may change it on restart, so trust the network rather than one address: get the subnet with `docker network inspect homeserver-net --format '{{(index .IPAM.Config 0).Subnet}}'` and set it as `API_KNOWN_NETWORK` (for example `172.18.0.0/16`). Every container on that network can then claim any client IP, so only do this if you trust all of them. Otherwise give cloudflared a fixed `ipv4_address` and set that as `API_KNOWN_PROXY`. Keep the API port bound to `127.0.0.1` (the compose file does this) so the tunnel is the only way in from outside. To check the setup, send a burst of login attempts from one connection, then make sure a second connection still gets through.

**Deploying (maintainer).** Copy `deploy.ps1.example` to `deploy.ps1` (gitignored), fill in your server details and the path to the homeserver-deploy tool (a separate, private repository), then run `.\deploy.ps1`. Its usage line is `.\deploy.ps1 [-DryRun] [-SkipTests]`. The example's config runs `dotnet test` on `tests/Ember.Tests` first, unless you pass `-SkipTests`. Set `EMBER_REMOTE_PATH` to deploy to another directory on the server, for example a staging copy.

**Migrations without the bot.** The bot applies database migrations when it starts. An instance without a bot (for example staging) can apply them and exit with `docker compose run --rm ember --migrate-only` (or `dotnet Ember.Bot.dll --migrate-only`); it needs only the connection string.

**Restoring a backup (maintainer).** A restore brings back what users deleted after the backup was taken. Ember keeps a deletion ledger for that: one line per account deletion, health-data deletion and consent withdrawal (including a "No thanks" that withdraws), with only the account id, the kind and the time, kept for 40 days. To restore:

1. Stop Ember: `docker compose --profile api --profile lookout stop`.
2. Restore the dump into `emberdb`.
3. If the backup is older than the deployed build, apply the migrations: `docker compose run --rm ember --migrate-only`.
4. Repeat the deletions since the backup's time, in UTC: `docker compose --profile api run --rm api --replay-deletions 2026-11-19T03:00:00Z`. Accounts are deleted the normal way, so Lookout gets their deletion too, health data is deleted again unless the account is gone, and a consent the backup still has as given is withdrawn again from the original time, so the deletion after 30 days runs on schedule. A consent given again after the withdrawal isn't recorded, so those users are simply asked again. It prints counts only and is safe to run again. It refuses a missing or unreadable ledger, a time without `Z` or an offset, and a backup time more than 40 days ago or before the ledger started (its first line), because then some deletions are missing.
5. Start Ember again.

When unsure of the backup's time, give an earlier one: that only repeats more deletions.

On production the ledger lives on the backup disk, outside the database it repairs. Both containers run as uid 1654, the .NET images' `APP_UID`:

```bash
mkdir -p ~/backups/ember-ledger
setfacl -m u:1654:rwx ~/backups/ember-ledger
```

Then set `EMBER_DELETION_LEDGER_DIR=/home/nico/backups/ember-ledger` and `EMBER_DELETION_LEDGER_FILE=/ledger/deletions.jsonl` in `.env` and recreate the containers (`docker compose --profile api up -d`). A deletion whose ledger line can't be written still happens; the container logs a warning.

**Reading feedback (maintainer).** Feedback and its pictures stay in the database; the daily digest names picture ids only. On the homeserver, from the Ember compose directory:

```bash
# Open, planned and implemented reports with their picture ids (add --all for released and declined ones too)
docker compose exec -T api dotnet Ember.Api.dll --list-feedback
# Save pictures by report id or picture id to ./feedback/{reportId}/{pictureId}.{ext}
tools/feedback-images.sh ./feedback 42 0193a6f2-...
# Set a status: open, planned, implemented, released or declined; released or declined closes the report and its pictures are deleted 30 days later
docker compose exec -T api dotnet Ember.Api.dll --feedback-status planned 42
```

Pictures are never kept past 12 months after the report. There is no HTTP endpoint for any of this.

**Sending feedback to Lookout (maintainer).** Every new report, and every account deletion of a user who sent feedback, goes into an outbox in `emberdb`. The `lookout-sender` container (profile `lookout`; enable it with `--profile lookout` or `COMPOSE_PROFILES`) delivers the outbox to Lookout's ingest at `LOOKOUT_INGEST_URL` from the pinned address `172.31.250.20`, with the key from `~/.config/lookout/apps/ember.key`: account deletions first, then reports, each in order. Without that profile and `LOOKOUT_INGEST_URL` nothing is sent. The container runs the `api` service's image, so build or start it together with `--profile api`. Reports Lookout refuses are parked (`ParkedAt`, `ParkedStatus` in `LookoutOutbox`) and logged without content. The one-time import of the reports stored before the outbox, with the ops board's comments and order, runs only while Lookout's import is open:

```bash
docker exec homeserver-postgres psql -U finvarex -d emberops -At -c "SELECT json_build_object('comments', (SELECT coalesce(json_agg(c ORDER BY c.created_at, c.id), '[]') FROM comments c), 'positions', (SELECT coalesce(json_agg(p), '[]') FROM card_positions p))" \
  | docker compose exec -T lookout-sender dotnet Ember.Api.dll --lookout-import -
```

It prints one line per report and is safe to run again. The import and the sender share a database lock around each request, so Lookout never gets two at once; the import waits while the sender has one open.

## Development

Code layout:

- `src/`: the .NET projects. `Ember.Core` (shared logic and the database model), `Ember.Bot` (the Discord bot) and `Ember.Api` (the HTTP API).
- `web/`: the React web app. `mobile/android`: the Android project. `tests/`: the .NET test projects. `docs/`: API contract and legal status. `design/`: theme and layout notes.

.NET (from the repository root):

```bash
dotnet build
dotnet test
```

`Ember.slnx` is the solution, including the ops dashboard projects under `ops/`. Many API tests and the PostgreSQL migration tests need Docker. Without Docker they are skipped locally (set `CI=true` to force them to run), except the ops tests, which always need Docker.

Web (from `web/`, using Bun 1.3.14):

| Task | Command |
|---|---|
| Install | `bun install --frozen-lockfile` |
| Dev server on port 4200 | `bun --bun run dev` |
| Unit tests | `bun --bun run test` |
| Lint (ESLint, config `eslint.config.js`) | `bun --bun run lint` |
| Local check before pushing: typecheck, lint and unit tests | `bun --bun run check` |
| Production build (output `dist/ember-web/browser`) | `bun --bun run build` |
| Mobile build (output `dist/ember-mobile/browser`) | `bun --bun run build:mobile` |
| Sync the Android project | `bunx --bun cap sync android` |

The dev server proxies `/api` to an API on port 5081.

Android (from `mobile/android`): run `./gradlew assembleDebug` to build a debug APK (on Windows, `gradlew.bat assembleDebug`). Before that, run the mobile build and the Capacitor sync in `web/` (the two commands in the table above, in that order). Release signing and Play upload steps are in [mobile/STORE.md](mobile/STORE.md).

Android tests and lint (from `mobile/android`, after the same two `web/` steps; Gradle finds the Android SDK through `ANDROID_HOME` or `sdk.dir` in `local.properties`): `./gradlew testBetaDebugUnitTest testPlayDebugUnitTest` runs the JVM unit tests, and `./gradlew lintBetaDebug lintPlayDebug` runs Android lint, which reports only issues missing from `app/lint-baseline.xml`. New tests go in Kotlin under `app/src/test/kotlin` (beta-only ones in `app/src/testBeta`). They run against Android's stub `android.jar` with default return values, use the real `org.json` library, and use `InMemoryContext` for code that reads SharedPreferences. After fixing a baselined lint issue, regenerate the baseline with `./gradlew updateLintBaselineBetaDebug` and keep its paths relative.

The HTTP API is described in [docs/contracts/api-v1.md](docs/contracts/api-v1.md).

CI (`.github/workflows/ci.yml`) runs on every push. For .NET it restores, checks for vulnerable packages, builds and runs the tests, the ops suite included. For the web it runs the unit tests and both builds. An Android job runs the JVM unit tests, and an end-to-end job runs the tests below and uploads the Playwright report, traces and test host log when they fail.

### End-to-end tests

Playwright journeys in `web/e2e` run against `tests/Ember.Api.TestHost`: the real API with a throwaway PostgreSQL container (needs Docker), a fake Discord and an in-memory outbox. The test host serves the built web app on the same origin:

```bash
# from web/
bun --bun run build && bunx playwright install chromium
# from the repository root, and keep it running
dotnet run --project tests/Ember.Api.TestHost -- --urls http://localhost:5081 --webroot web/dist/ember-web/browser
# from web/, in a second shell
E2E_BASE_URL=http://localhost:5081 bunx playwright test                    # desktop and phone
E2E_BASE_URL=http://localhost:5081 bunx playwright test --project=chromium # desktop only
E2E_BASE_URL=http://localhost:5081 bunx playwright test --project=phone    # phone only
```

The `chromium` project (Desktop Chrome) runs every journey. The `phone` project (Pixel 7) runs the ones where the phone layout matters: bottom navigation and back links, Today check-ins and routines. Run Playwright on Node (`bunx playwright`, not `bunx --bun`): under the Bun runtime its request fixture fails on responses that set cookies. Tests run in parallel; `E2E_WORKERS` (default 3) sets how many. CI runs both projects the same way, split over three parallel jobs (`--shard=1/3` to `3/3`).

## Tech stack

| Part | Technology |
|---|---|
| Discord bot | C# on .NET 10, Discord.Net 3.19.1 |
| API | ASP.NET Core on .NET 10, ASP.NET Core Identity, sign-in with Discord (AspNet.Security.OAuth.Discord), MailKit for email |
| Scheduler | Quartz.NET 3.18 (in the bot) |
| Web push | Lib.Net.Http.WebPush 3.3 (in the bot) |
| Database | PostgreSQL through Npgsql and EF Core 10 (the tests use PostgreSQL 17) |
| Web app | React 19, TypeScript 5.9, Vite 8, Tailwind CSS 4, TanStack Query 5, React Router 8, Workbox 7 for the service worker |
| Android app | Capacitor 8 around the web build |
| Package manager and runtime | Bun 1.3.14 |
| Tests | xUnit with Testcontainers for PostgreSQL (.NET); Vitest 5 and Playwright (web) |
| Hosting | Docker Compose, with .NET 10 and Bun images |

## Legal

The privacy policy (Datenschutzerklärung), the terms of use (Nutzungsbedingungen) and the imprint (Impressum) are German texts with English summaries. They live in [`web/public/legal/`](web/public/legal/), and the web app serves them at `/legal/privacy`, `/legal/terms` and `/legal/imprint`. They are drafts written from the code and have not been checked by a lawyer yet. [`docs/legal/`](docs/legal/) tracks their status and the open questions.
