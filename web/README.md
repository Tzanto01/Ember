# Ember web

React 19, TypeScript, Vite, Tailwind CSS v4, TanStack Query, React Router, Capacitor 8 (Android in `../mobile/android`). Runtime and package manager: bun (`bun --bun run <script>`); the scripts also run on Node 22+.

| Task | Command (from `web/`) |
| --- | --- |
| Install | `bun install --frozen-lockfile` |
| Dev server on port 4200 (proxies `/api`, `/health`, `/test` to the API on 5081) | `bun --bun run dev` |
| Production build (PWA, output `dist/ember-web/browser`) | `bun --bun run build` |
| Mobile build (no service worker, settings in `.env.mobile`, output `dist/ember-mobile/browser`) | `bun --bun run build:mobile` |
| Sync the Android project | `bunx --bun cap sync android` |
| Unit tests (Vitest) | `bun --bun run test` |
| Lint (ESLint with typescript-eslint `strictTypeChecked` and React Hooks, config `eslint.config.js`) | `bun --bun run lint` |
| Local check before pushing: typecheck, lint and unit tests | `bun --bun run check` |
| End-to-end tests (Playwright) | see `playwright.config.ts`: start `tests/Ember.Api.TestHost` on port 5081 with `--webroot web/dist/ember-web/browser`, then `E2E_BASE_URL=http://localhost:5081 bunx playwright test` (Playwright runs on Node: under `--bun` its request fixture fails on responses that set cookies) |

## Architecture

```
pages/ -> components/<feature>/ + components/ui/ -> lib/
                       TanStack Query (keys in lib/query-keys.ts) -> lib/api-client.ts -> lib/http.ts
                                                       account: fetch /api, offline cache and queue
                                                       no account: lib/local/backend.ts (IndexedDB)
```

- `src/pages/`: one file per route (`router.tsx`). `src/components/<feature>/`: the screens' parts; `components/ui/` holds the shared primitives and knows nothing about Ember.
- `src/hooks/`: React hooks shared by several screens (`use-*.ts`); they use `lib/` only.
- `src/lib/`: the app's logic, without React apart from `useStore` (`store.ts`), `useCalmMode` (`theme.ts`) and the i18n setup. `lib/services.ts` builds the app's single instances; tests build their own with fakes.
- No-account mode: `Http` hands every `/api` request (except those marked `server`, such as feedback) to `lib/local/backend.ts`, which answers from IndexedDB with the rules in `lib/local/rules.ts`. Screens don't know which backend they talk to.
- Rules parity: `lib/local/rules.ts` must give the server's answers. `docs/contracts/fixtures/*.json` holds the shared cases; `lib/local/rules-parity.test.ts` and `tests/Ember.Tests/RulesFixtureTests.cs` both run them.
- Offline with an account: `lib/offline-queue.ts` keeps day and journal edits in `lib/device-store.ts` and replays them; `withDayEdit` there is the one optimistic day edit, used by Today and the offline answers.
- Query keys: data that several screens read or refresh (Today, History, Year, a habit, routines, the journal) uses the keys in `lib/query-keys.ts`; a key used by one feature stays next to it.
- Tests: Vitest files sit next to their code; the shared helpers (fetch fake, fixtures, `renderPage`, the journal fake, the setup file) live in `src/test/`. Playwright journeys are in `e2e/`.
- Models: `lib/models.ts` follows `docs/contracts/api-v1.md` by hand.

Design: `design/layout.md` (layout and screens), `docs/contracts/design-tokens-v1.md` (theme tokens), `src/themes/` (themes; `index.css` picks one). The record of the port from the former Angular app: `PARITY.md` (archived).
