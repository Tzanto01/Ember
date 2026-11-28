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

- `picture` (a kind) sets the picture. With it, `portionAmount` + `portionUnit` set the portion. Without them: the same picture as now keeps the current portion (an edit form that resends the whole body loses nothing); a different picture gets that picture's default portion (null for `pills` and `kindling`), so switching pictures never leaves a portion of the wrong kind.
- `portionAmount` + `portionUnit` without `picture` change the portion of the current picture; for a habit without a picture, or one whose picture takes none → 400 on `portionAmount`.
- `clearPicture: true` removes the picture and the portion. It is the only way to clear: `picture: null` is the same as a missing field. `clearPicture` together with `picture` or a portion → 400 `validation` on `picture` ("Can't be combined with clearPicture."). `clearPicture` on a habit without a picture, or on a let-go habit, changes nothing.
- `pictureOfferAnswered: true` records the answer. `false` is accepted and changes nothing (the answer is never taken back).
- Missing fields keep their values, so old clients keep the picture.
- No consent is needed for these fields, as for the schedule and the timer.
- Atomic: the endpoint checks every picture rule against the habit's kind and current picture before its first write (the schedule write comes first today), so a rejected PATCH writes nothing. The picture is written through extra optional parameters on `HabitService.EditHabitAsync` (and `AddHabitAsync` on create).

Validation errors are `400 validation` with the field name as the key, nothing written:

- `picture`: unknown kind → "Must be one of bottle, pills, kindling, shelf, trail."
- Checked in this order, first failure only: picture kind; unknown unit name → on `portionUnit` "Must be one of ml, min, pages, m."; let-go; for `pills`/`kindling` any portion field → on `portionAmount` "Pills and kindling have no portion."; only one of amount and unit sent → on `portionAmount` "Send portionAmount and portionUnit together."; a portion for a habit without a picture → on `portionAmount` "Choose a picture first."; unit not allowed for the picture → on `portionUnit` "Must be ml for bottle." / "Must be min or pages for shelf." / "Must be min or m for trail."; out of range → on `portionAmount` "Must be between {min} and {max}."
- A value of the wrong JSON type is an unreadable body → 400, like any other field.

The rules live in one Core class, `Ember.Core.Rules.PictureRules` (like `LetGoRules`): the allowed units, ranges, defaults, and a `ThrowIfInvalid` that `HabitService` calls. `HabitValidation` (API errors), the import path (which builds `Habit` entities directly in `ImportEndpoints`) and the service all use it, so no path can write an invalid combination. The shared rules fixtures (`RulesFixtureTests` and web `rules-parity.test.ts`) get the picture cases so the web's local copy cannot drift; the cases live in `docs/contracts/fixtures/pictures-v1.json` (a let-go error goes on `picture` when one is sent, else on `portionAmount`).

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
- Import (`POST /api/me/import`, backups): all optional (habit fields missing: null/false; settings fields nullable, null leaves the account's value, so a new account keeps true/metric), same checks as the API, errors on `habits[i].picture`, `habits[i].portion_amount`, `habits[i].portion_unit`, `settings.units`. Account merge moves habits with their picture fields; the signed-in account's settings win as for the other settings.

## Migration

One migration `AddHabitPictures`: `Habits` gains nullable integer `Picture`, `PortionAmount`, `PortionUnit` and `PictureOfferAnswered boolean NOT NULL DEFAULT false`; `AspNetUsers` gains `SuggestPictures boolean NOT NULL DEFAULT true` (existing accounts get the preference on, as the concept wants) and `Units integer NOT NULL DEFAULT 0`. The EF model sets the same defaults (`HasDefaultValue`) so new rows match. `PostgresMigrationTests` covers the defaults on existing rows. With the preference on, every existing matching habit becomes tip eligible at once; the tip still shows one habit at a time under the existing tip pacing.

## Web

- `models.ts` `Habit` gains optional `picture?: HabitPicture | null`, `portionAmount?: number | null`, `portionUnit?: PortionUnit | null`, `pictureOfferAnswered?: boolean` (missing from older servers and caches: plain, not answered). `HabitPicture = 'bottle' | 'pills' | 'kindling' | 'shelf' | 'trail'`, `PortionUnit = 'ml' | 'min' | 'pages' | 'm'`. Create and patch bodies take the API fields above, patch also `clearPicture`.
- `Me` gains `suggestPictures?: boolean` (missing: true) and `units?: 'metric' | 'imperial'` (missing: metric); `MePatch` takes both.
- Local no-account store: `LocalHabit` gets the same four fields, `LocalProfile` the two preferences, with the same rules in `lib/local/backend.ts` (mirroring the limits in `lib/local/limits.ts`). Backups carry the habit fields (the preferences stay out of local backups, like `calmMode`; moving into an account sends them with `PATCH /api/me`); older backups restore as plain and not answered; moving device data into an account sends them.
- Conversion (display only): 1 US fl oz = 29.5735 ml, 1 mi = 1609.344 m. Metric shows ml below 1000 and L with up to two decimals above ("1.25 L"); km with one decimal. Imperial shows whole fl oz and mi with one decimal. A portion typed in fl oz or mi is converted to whole ml or m and stored canonically.
- Goal from a target (form only, daily and weekdays habits; hidden for weekly): a bottle offers a daily goal (default 2 L) and a portion (default 250 ml) and sets the form's existing `timesPerDay` field to `ceil(goal / portion)`. The goal itself is not stored; the edit form shows it as `timesPerDay × portion`. Above 10 taps the form keeps 10 and says so, suggesting a bigger portion (for example 3 L needs 300 ml or more per tap). The text under it states the real total when rounding changes it ("7 taps, 2.1 L"). On edit it changes `timesPerDay` exactly as typing the number does today, with the same existing behavior for past days.

## Today behavior (web and Android, one code path)

- The picture replaces nothing: the row keeps its name, check button and undo-one; the picture sits beside them like `TimerControl`. Paused habits and let-go habits show no picture.
- **Tap** on the picture = one check-in: `count + 1`, never above `timesPerDay`. At `timesPerDay` the picture is full and a tap does nothing but say so in its label. Every count the client sends is clamped to 0 to `timesPerDay` before it is sent or queued offline (`offline-queue.ts` drops 400s).
- **Bottle hold** (press and hold about 500 ms, or a separate small "More" button that is in the tab order, for keyboard and screen readers): a small picker with `+1`, `+2`, `+3` portions labelled in the display unit ("+250 ml", "+500 ml", "+750 ml"), only those that fit what's left, and Close. Escape closes it and returns focus. One choice sends one count write.
- **Pill box**: `timesPerDay` slots. Labels are the habit's reminder times (locale time format) when `reminderTimes.length === timesPerDay`, otherwise ordinals ("1st, 2nd, 3rd" / "1., 2., 3."). Tapping an open slot is one check-in (fills the next open slot, `count + 1`). Tapping a taken slot `i` (0 based) undoes back to it: `count = i`. Each slot is a toggle button with `aria-pressed`.
- **Kindling, shelf, trail**: one tap = one check-in, as above.
- **Text progress**, always visible, `aria-live="polite"`: `"{count} of {timesPerDay}"`, plus the total for pictures with a portion: bottle `"5 of 8, 1.25 L"`, shelf `"2 of 3, 30 min"` or `"2 of 3, 40 pages"`, trail `"1 of 2, 10 min"` or `"1 of 2, 1.5 km"`. Pills and kindling show the count only. The total is converted once from `count × portion` in the canonical unit, never per tap (4 × 250 ml = 1000 ml = "1 L" = "34 fl oz"). Metric volume: ml below 1000, else L with up to two decimals, trailing zeros dropped ("1 L", "1.25 L"). Metric distance: m below 1000 rounded to 10 m, else km with one decimal. Imperial volume: whole fl oz. Imperial distance: mi with two decimals below 1 mi, one decimal above.
- **Label**: the picture button's accessible name says what it is, the progress and what a tap does ("Drink water, 5 of 8 glasses. Add 250 ml.").
- A new day starts at zero without any comment; nothing drains, wilts or empties. Calm mode and reduced motion: no fill or grow transition, calm color tokens. Never color only.

## Offers

- **Keywords** (on device, `web/src/lib/habit-visuals/keywords.ts`, EN and DE, nothing sent): bottle: water, drink, hydrate, wasser, trinken; pills: pill, tablet, medication, meds, vitamin, tablette, medikament; shelf: read, book, lesen, buch; trail: walk, run, steps, hike, spazieren, laufen, gehen. Matching: the name lowercased and split into words on anything that is not a letter; a keyword of 4 letters or fewer matches the start of a word ("run" matches "running", not "brunch"), a longer one matches anywhere in a word (German compounds: "spazierengehen"). Checked in the order bottle, pills, shelf, trail; first match wins. Kindling is never offered.
- **New habit**: when `suggestPictures` is on, the kind is build, the picture is still plain and the name matches, a small offer appears under the name with the matched picture: [Try the {picture}] sets the picture in the form; [Keep it plain] hides the offer. Either answer creates the habit with `pictureOfferAnswered: true`. Ignoring it leaves `false`, so Today may ask once later.
- **Existing habits (Today tip)**: a new tip in `lib/tips.ts` with the lowest priority, after every existing tip, under the same one-tip-at-a-time and quiet-day pacing. It is not a `TipIds` entry: its "seen" state is the habit's `pictureOfferAnswered`. Eligible: `suggestPictures` on, a build habit due today and not paused, no picture, not answered, name matches. The first eligible habit in Today order is offered. [Show me] sends `PATCH { picture, pictureOfferAnswered: true }` and then says where to undo it ("You can switch it off in the habit's settings."); [Not for this habit] sends `PATCH { pictureOfferAnswered: true }`.
- `suggestPictures` off: neither offer appears; chosen pictures stay.

## Settings rows (components only, not mounted)

`HabitPicturesSetting` (the `suggestPictures` switch) and `UnitsSetting` (metric or imperial) in `web/src/components/habit-visuals/`, each taking the current value and an `onChange`, with tests. The department lead mounts them after the Settings regroup merges.

## Other places that carry the fields

API contract goldens (`tests/Ember.Api.Tests/Contract`), `ExportCsvGoldenTests`, `MergeTests`, web `backup.ts` (`damaged()` rejects a picture or portion on a let-go habit and an invalid combination, as it does for `timer_minutes`), `upgrade.ts` (moving device data into an account).


## Unaffected

- Discord bot: no picture text, no new command option; it reads habits through Core services and keeps working (export goes through `ExportBuilder`).
- Android widget: stays text, no new field.
- Server pushes and reminders: unchanged.
