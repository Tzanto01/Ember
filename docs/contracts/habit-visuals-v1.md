# Habit pictures contract v1 (API v1.30)

Nico, 2026-11-28, on the habit visuals concept: "Both look conceptually good, build them as is please." A build habit can show an optional picture on Today that you tap to check in once. Taps, not real amounts; free; five pictures. This file is the contract for the data, the API, the stores and the export. `api-v1.md` v1.30 points here.

## Inventory this builds on

- Multi-count check-ins: `Habit.TimesPerDay` (1 to `Limits.TimesPerDay` = 10, always 1 for weekly habits) and `HabitLog.Count`. A tap is `PUT /api/habits/{id}/days/{date} { count: current + 1 }`; "undo one" and a pill slot undo are the same call with a lower count. No new day endpoint.
- Optional habit field pattern: `Habit.TimerMinutes` (v1.28) end to end: entity, migration `AddHabitTimerMinutes`, `HabitValidation`, `HabitService.AddHabitAsync`/`EditHabitAsync`, `HabitDto`, export and import, web model, local store and backups.
- Enum pattern: `Habit.Kind` (integer column, lowercase string on the wire, snake case in export).
- Account preference pattern: `User.CalmMode` and `User.OfferHabitNote` through `GET`/`PATCH /api/me`, `SettingsValidation`, `UserSettingsService.SettingsUpdate`, the export `settings` record, `LocalProfile`.
- Tips: `tip-slot.tsx` and `lib/tips.ts` choose one tip at a time; the per-user `TipsSeen` list is not per habit, so the "asked once" answer lives on the habit.

## Habit fields

| Field (C# / JSON / export) | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Picture` / `picture` / `picture` | `HabitPicture?` stored as nullable integer; wire `"bottle" \| "pills" \| "kindling" \| "shelf" \| "trail" \| null` | null (plain) | The picture Today shows. Integer values: Bottle 0, Pills 1, Kindling 2, Shelf 3, Trail 4. |
| `PortionAmount` / `portionAmount` / `portion_amount` | `int?` | null | What one tap means, in the canonical unit below. |
| `PortionUnit` / `portionUnit` / `portion_unit` | `PortionUnit?` stored as nullable integer; wire `"ml" \| "min" \| "pages" \| "m" \| null` | null | Milliliters 0, Minutes 1, Pages 2, Meters 3. |
| `PictureOfferAnswered` / `pictureOfferAnswered` / `picture_offer_answered` | `bool`, not null | false | The picture offer for this habit was answered (either way); it is never offered again on any device. |

Portion rules (the server enforces them; amount and unit are always set together or both null):

| Picture | Allowed units | Amount range | Default when the picture is set without a portion |
| --- | --- | --- | --- |
| `bottle` | `ml` | 50 to 2000 | 250 ml |
| `shelf` | `min`, `pages` | min 1 to 240, pages 1 to 500 | 15 min |
| `trail` | `min`, `m` | min 1 to 240, m 100 to 50000 | 10 min |
| `pills`, `kindling` | none | none | none (a portion sent for them → 400) |

- Canonical units are stored; imperial is display only. Trail distance is stored in meters and shown as km or mi.
- Let-go habits never have a picture or portion: any `picture` or `portionAmount` for one → 400 `validation` "Never set for let-go habits." `pictureOfferAnswered` may be set on any habit.
- Weekly habits may have a picture; Today shows one tap of one.

## API

Habit read (`GET /api/habits`, `GET /api/habits/{id}`, create and patch responses): `HabitDto` gains `picture`, `portionAmount`, `portionUnit`, `pictureOfferAnswered`, in that order after `timerMinutes`. A let-go habit always has `picture`, `portionAmount` and `portionUnit` null.

`POST /api/habits` accepts optional `picture`, `portionAmount`, `portionUnit`, `pictureOfferAnswered` (missing: null, null, null, false). A picture without a portion gets the default portion above.

`PATCH /api/habits/{id}`:

- `picture` (a kind) sets the picture. With it, `portionAmount` + `portionUnit` set the portion; without them the portion becomes that picture's default (and null for `pills` and `kindling`), so switching pictures never leaves a portion of the wrong kind.
- `portionAmount` + `portionUnit` without `picture` change the portion of the current picture; for a habit without a picture, or one whose picture takes none → 400 on `portionAmount`.
- `clearPicture: true` removes the picture and the portion. `clearPicture` together with `picture` or a portion → 400 `validation` on `picture` ("Can't be combined with clearPicture."). `clearPicture` on a habit without a picture changes nothing.
- `pictureOfferAnswered: true` records the answer. `false` is accepted and changes nothing (the answer is never taken back).
- Missing fields keep their values, so old clients keep the picture.
- No consent is needed for these fields, as for the schedule and the timer.

Validation errors are `400 validation` with the field name as the key, nothing written:

- `picture`: unknown kind → "Must be one of bottle, pills, kindling, shelf, trail."
- `portionUnit`: not allowed for the picture → "Must be ml for bottle." / "Must be min or pages for shelf." / "Must be min or m for trail."; for `pills`/`kindling` → on `portionAmount` "Pills and kindling have no portion."
- `portionAmount`: only one of amount and unit sent → "Send portionAmount and portionUnit together."; out of range → "Must be between {min} and {max}."
- A value of the wrong JSON type is an unreadable body → 400, like any other field.

The service layer (`HabitService`) enforces the same rules (`ArgumentException`/`ArgumentOutOfRangeException`), so the bot and import cannot write an invalid combination.

## Account preferences

| Field (C# / JSON / export) | Type | Default | Meaning |
| --- | --- | --- | --- |
| `SuggestPictures` / `suggestPictures` / `suggest_pictures` | `bool`, not null | true | "Suggest pictures for habits". Off stops the new-habit offer and the Today tip. Pictures already chosen stay. |
| `Units` / `units` / `units` | `UnitSystem` stored as integer, not null; wire `"metric" \| "imperial"` | metric (0); imperial 1 | How portions and totals are shown. Display only. |

`GET /api/me` gains both. `PATCH /api/me` accepts `suggestPictures?` and `units?` (missing: unchanged; another `units` value → 400 `validation` on `units`, "Must be metric or imperial.").

## Export and import (format v2, additive)

- JSON `habits[]`: `picture` (string or null), `portion_amount`, `portion_unit` (string or null), `picture_offer_answered`, after `timer_minutes`. Enum values on export: the wire strings above.
- JSON `settings`: `suggest_pictures`, `units`.
- CSV `habit` rows: columns `picture`, `portion_amount`, `portion_unit`, `picture_offer_answered` right after `timer_minutes`; the settings row gets `suggest_pictures`, `units`.
- Import (`POST /api/me/import`, backups): all optional (missing: null/false; settings missing: true/metric), same checks as the API, errors on `habits[i].picture`, `habits[i].portion_amount`, `habits[i].portion_unit`, `settings.units`. Account merge moves habits with their picture fields; the signed-in account's settings win as for the other settings.

## Web

- `models.ts` `Habit` gains optional `picture?: HabitPicture | null`, `portionAmount?: number | null`, `portionUnit?: PortionUnit | null`, `pictureOfferAnswered?: boolean` (missing from older servers and caches: plain, not answered). `HabitPicture = 'bottle' | 'pills' | 'kindling' | 'shelf' | 'trail'`, `PortionUnit = 'ml' | 'min' | 'pages' | 'm'`. Create and patch bodies take the API fields above, patch also `clearPicture`.
- `Me` gains `suggestPictures?: boolean` (missing: true) and `units?: 'metric' | 'imperial'` (missing: metric); `MePatch` takes both.
- Local no-account store: `LocalHabit` gets the same four fields, `LocalProfile` the two preferences, with the same rules in `lib/local/backend.ts` (mirroring the limits in `lib/local/limits.ts`). Backups carry them; older backups restore as plain and not answered; moving device data into an account sends them.
- Conversion (display only): 1 US fl oz = 29.5735 ml, 1 mi = 1609.344 m. Metric shows ml below 1000 and L with up to two decimals above ("1.25 L"); km with one decimal. Imperial shows whole fl oz and mi with one decimal. A portion typed in fl oz or mi is converted to whole ml or m and stored canonically.
- Goal from a target (form only): a bottle offers a daily goal (default 2 L) and a portion (default 250 ml) and sets `timesPerDay = ceil(goal / portion)`. Above 10 taps the form keeps 10 and says so, suggesting a bigger portion (for example 3 L needs 300 ml or more per tap).

## Unaffected

- Discord bot: no picture text, no new command option; it reads habits through Core services and keeps working (export goes through `ExportBuilder`).
- Android widget: stays text, no new field.
- Server pushes and reminders: unchanged.
