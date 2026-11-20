# Ember visual system inventory

Phase 1 of the design-system work: a record of what Ember's visual system is today, taken from `v2/next` at `6d5dcb4`. It describes what exists, not what should exist. Findings in section 5 and the split in section 6 are input for the global system draft; nothing here is a decision.

- References are `path:line` from the repository root and resolve on the commit above.
- `docs/design/tokens.json` holds the full token matrix (every token, every theme, light and dark, hex conversion, file:line), the calm-mode overrides and the Android drift check, extracted by script. Section 1 summarizes it.
- Screens were read from the committed visual baselines in `web/e2e/visual.spec.ts-snapshots/` (Hearthlight only, see 5.6) and from the code.

Contents: 1 Tokens, 2 Components, 3 Patterns, 4 What makes Ember feel like Ember, 5 Inconsistencies and one-offs, 6 Candidate split (proposal), 7 Open questions for Nico.

## 1. Tokens

### 1.1 How the token layer is built

| Layer | Where | What it does |
| --- | --- | --- |
| Contract | `docs/contracts/design-tokens-v1.md:19-60` | Names every token, its contrast pairs (text 4.5:1, UI 3:1, light and dark) and the rules every theme keeps (no red, no "missed" colour, `docs/contracts/design-tokens-v1.md:68`) |
| Theme files | `web/src/themes/{hearthlight,fieldnotes,tidepool,marigold}.css` | Each sets all 53 tokens on `:root[data-theme="<id>"]` (light, lines 1-59) and the colour tokens plus `--shadow-card` again on `.dark` (lines about 60-105). Hearthlight also matches `:root:not([data-theme])` (`web/src/themes/hearthlight.css:3`) |
| Theme index | `web/src/themes/index.css:1-4`, `web/src/themes/index.ts:2-7` | Imports all four theme files and all six font packages |
| Tailwind mapping | `web/src/index.css:8-63` | `@theme inline` clears Tailwind's palette (`--color-*: initial`, `web/src/index.css:9`) and maps every colour token to a utility; derives radii, spacing, fonts and default transition from theme tokens |
| Calm mode | `web/src/calm.css:19-355` | A second hand-written copy of every colour token for every theme and mode, at lower chroma |
| Checker | `design/contrast.mjs:13` | Reads `web/src/themes/*.css` and fails on a missing token, a failed contrast pair, an unknown reward keyword or a red hue. Holds no colour values of its own |
| Runtime readers | `web/src/components/today/reward.ts:12-24`, `web/src/lib/theme.ts:15-44` | JS reads `--ember-reward` and the duration tokens from computed style; `theme.ts` keeps a hex copy of each theme's `--background` and a notification tint |
| Pre-paint | `web/public/theme-init.js:10-16` | Sets `data-theme`, `.dark` and `data-calm` on `<html>` from `localStorage` before first paint |

Because `--color-*: initial` removes Tailwind's default palette, palette classes such as `bg-white` or `text-gray-500` cannot compile; a sweep found none in `web/src`.

### 1.2 Colour: primitives

There is no separate primitive layer. Each theme writes semantic tokens directly as `oklch()` literals; the same literal is often repeated across tokens (for example Hearthlight light `--foreground`, `--card-foreground`, `--popover-foreground`, `--accent-foreground` and `--ring` are all `oklch(0.255 0.022 50)`, `web/src/themes/hearthlight.css:6-21`).

Hue families per theme (OKLCH hue of the main roles, from the theme files):

| Theme | Neutrals (bg, card, text) | Primary / done | Rest (lowday) | Notice and destructive | Flame | Character |
| --- | --- | --- | --- | --- | --- | --- |
| Hearthlight (default) | warm 50-80 | amber 55-62 | lavender 290 | honey 62-82 | 62 / 85 | warm cream and charcoal |
| Fieldnotes | olive-paper, ink blue text | green 140-145 | blue | honey | orange | notebook, hairlines |
| Tidepool | cool blue-grey | teal | lavender | honey | orange | cool, airy |
| Marigold | butter yellow | ink (`#292016`) light, marigold yellow dark | periwinkle | honey | marigold | sticker sheet, ink outlines |

Flame and notice stay warm in every theme, so even Tidepool keeps an ember accent (`design/README.md:19-25`).

### 1.3 Colour: semantic roles

All 39 colour tokens, grouped by role. Hex values are the sRGB conversion of the oklch source; full matrix in `docs/design/tokens.json`. Hearthlight lines: `web/src/themes/hearthlight.css:5-43` (light), `:64-102` (dark).

| Role group | Tokens | Hearthlight light | Hearthlight dark | Notes |
| --- | --- | --- | --- | --- |
| Page | `--background`, `--foreground` | `#FBF5EC`, `#2C2019` | `#1C1410`, `#F1EAE0` | |
| Surface | `--card`, `--card-foreground`, `--popover`, `--popover-foreground` | `#FFFCF8`, `#2C2019` | `#2A1F19`, popover `#2F241E` | popover equals card in light in all four themes; slightly lighter than card in dark |
| Action | `--primary`, `--primary-foreground` | `#944E12`, `#FEFBF8` | `#F1AF64`, `#1C1410` | also links and the active nav item |
| Quiet fills | `--secondary`, `--muted`, `--accent` (+ foregrounds) | `#F9E6D1`, `#F4EBE0`, `#F9E6D1` | `#3E2C1E`, `#342923`, `#3E2C1E` | `--secondary` equals `--accent` in Hearthlight, Fieldnotes and Tidepool; Marigold splits them |
| Text, secondary | `--muted-foreground` | `#665448` | `#C1B1A0` | must pass on background, card, muted, accent, secondary |
| Lines | `--border`, `--input`, `--ring` | `#E7DCD0`, `#937B69`, `#2C2019` | `#3F322B`, `#907C6C`, `#F1EAE0` | ring is the text colour in Hearthlight and Tidepool, a blue in Fieldnotes and Marigold |
| Problems | `--destructive` (+fg), `--notice`, `--notice-foreground`, `--notice-border` | `#7F490C`, `#FCEDD1`, `#6F400C`, `#A57636` | `#EABF84`, `#382814`, `#EECB95`, `#9E7848` | destructive is honey, never red (`docs/contracts/design-tokens-v1.md:35`) |
| Positive | `--success` (+fg) | `#3B694C` | `#8EC7A1` | green, used for confirmations only |
| Day states | `--done` (+fg), `--pending`, `--empty`, `--paused`, `--beforestart`, `--lowday` (+fg) | done `#AE5C01`, pending `#AE5C01`, empty `#9D8877`, lowday `#E8E5FB` / `#524980` | done `#E79647`, pending `#F1AF64` | Hearthlight light pending equals done; dark pending equals primary |
| Heatmap | `--heat-1`, `--heat-2` | `#C3814F`, `#B86E30` | `#956335`, `#BD7C3E` | steps toward `--done` |
| Brand | `--flame`, `--flame-core` | `#F69835`, `#FFDD93` | `#FAAA51`, `#FEE6AA` | decorative only; match the favicon and app icon fills (5.3) |
| Charts | `--chart-1` to `--chart-3` | `#AE5C01`, `#7167A8`, `#497F5D` | | `--chart-1` equals `--done` in every theme and mode |

Component-level colour use beyond the utilities: `color-mix()` over tokens appears about 51 times, for hover states (`web/src/components/ui/button.tsx:12-18`), done tints (`web/src/components/today/habit-row.tsx:174`, `web/src/components/today/check-button.tsx:67`), sheet scrims (`web/src/components/today/note-sheet.tsx:110`) and illustration fills (`web/src/components/today/hearth-illustration.tsx:5-12`). There is no hover, overlay or tint token; each site picks its own percentage.

### 1.4 Colour: Android copies and drift

Android keeps hand copies of web tokens in three places. A script compared all 60 copied values with the oklch source converted to sRGB.

| Copy | What it mirrors | Result |
| --- | --- | --- |
| `mobile/android/app/src/main/kotlin/app/ember/habits/widget/WidgetPalette.kt:17-24` | card, foreground, muted-foreground, primary, done, border for all four themes, light and dark | 47 of 48 exact. Marigold light border is `#D3C9A9` against token `#292016` (`web/src/themes/marigold.css:18`), on purpose per the doc comment at `WidgetPalette.kt:5-6` |
| `mobile/android/app/src/main/res/values/ember_colors.xml:3-9`, `values-night/ember_colors.xml:3-9` | Hearthlight background, card, foreground, muted-foreground, primary, border | 12 of 12 exact |
| `mobile/android/app/src/main/res/drawable/widget_flame_{0..3}.xml` | Hearthlight `--flame` / `--flame-core` (`#F69835` / `#FFDD93`) | exact, light only; the widget flame does not follow the selected theme |

What the copies do not carry: calm mode (the widget always uses the full-chroma palette), the dark flame values, and any theme other than Hearthlight for the status bar and splash (by contract, `docs/contracts/design-tokens-v1.md:16`). `widget_glow.xml:6-8` uses `#F5C978` at two alphas, which is not a token value. `ic_launcher_background.xml:3` is `#2A211C`, the icon tile colour, also not a token.

The Android `AppTheme` (`mobile/android/app/src/main/res/values/styles.xml:5-10`) points at `colorPrimary`, `colorPrimaryDark` and `colorAccent`, which the app never defines. They resolve to Capacitor's template defaults (`#3F51B5` indigo, `#303F9F`, `#FF4081` pink; `@capacitor/android` 8.6.0 `res/values/colors.xml`). The launch activity uses `AppTheme.NoActionBarLaunch` (`mobile/android/app/src/main/AndroidManifest.xml:19`) and `AppTheme.NoActionBar` has its own AppCompat parent (`styles.xml:12`), so these values are probably never visible. Not confirmed on a device.

### 1.5 Typography

| Theme | Body (`--ember-font-sans`) | Display (`--ember-font-display`) | Heading weight | Tracking | Lines |
| --- | --- | --- | --- | --- | --- |
| Hearthlight | Nunito Variable, `ui-rounded` | Nunito Variable, SF Pro Rounded | 800 | -0.015em | `web/src/themes/hearthlight.css:50-53` |
| Fieldnotes | Atkinson Hyperlegible Next | Fraunces (serif) | 600 | -0.01em | `web/src/themes/fieldnotes.css:49-52` |
| Tidepool | Lexend | Lexend | 500 | -0.02em | `web/src/themes/tidepool.css:49-52` |
| Marigold | Figtree | Bricolage Grotesque | 800 | -0.015em | `web/src/themes/marigold.css:49-52` |

Base rules: body 1rem, line height 1.55 (`web/src/index.css:92-93`); h1 to h3 use the display stack, heading weight and tracking, line height 1.2 (`web/src/index.css:96-103`). Fonts are self-hosted Fontsource packages (`web/src/themes/index.ts:2-7`).

There is no type scale token. Sizes are arbitrary values: `text-[..]` on 219 lines in 82 files against 11 uses of `text-base` and none of `text-sm`, `text-lg` and so on. The de facto scale:

| Size | Use | Example |
| --- | --- | --- |
| 12px | partial count in a day cell | `web/src/components/day-cell.tsx:119` |
| 13px | phone tab labels | `web/src/components/app-shell.tsx:134` |
| 14px (`0.875rem`, most common) | hints, meta, badges, errors, footer | `web/src/components/form-field.tsx:72`, `web/src/components/ui/badge.tsx:6` |
| 16px | body, inputs, buttons | `web/src/components/ui/button.tsx:7` |
| 18px (`1.125rem`) | h3, habit name, header wordmark | `web/src/components/today/habit-row.tsx:184`, `web/src/components/app-shell.tsx:115` |
| 20px (`1.25rem`) | some h2 ("For today") | `web/src/pages/today.tsx:618` |
| 22px (`1.375rem`) | most h2, empty-state title, auth wordmark | `web/src/components/settings/settings-card.tsx:30`, `web/src/components/empty-state.tsx:24` |
| 28px / 36px md | h1 (PageHeader) | `web/src/components/page-header.tsx:18` |
| 48px | landing hero | `web/src/pages/landing.tsx:78` |

This matches the retired Hearth scale in `web/DESIGN.md:56-64` (13, 14, 16, 18, 22, 28, 36), plus 12 and 20. Weights in use: `font-medium` (37), `font-semibold` (34), `font-bold` (5). `tabular-nums` appears 5 times (for example `web/src/components/today/check-button.tsx:78`). No uppercase or eyebrow labels anywhere.

### 1.6 Spacing and sizes

- Tailwind's spacing step is the theme's density unit: `--spacing: var(--space-unit)` (`web/src/index.css:56`). `--space-unit` is 4px in three themes and 5px in Tidepool (`web/src/themes/tidepool.css:48`), so `p-4` is 16px or 20px.
- Touch targets are literal: `min-h-[44px]` appears 49 times in 40 files (`web/src/components/ui/button.tsx:23`, `web/src/components/ui/input.tsx:8`) and do not scale with density. Small controls get a 44px hit area through an `after:` inset (`web/src/components/ui/checkbox.tsx:11`, `web/src/components/ui/switch.tsx:10`).
- Container widths (no tokens): `max-w-[40rem]` narrow page column (16 uses, `web/src/pages/settings.tsx:235`), `max-w-[38rem]` reading measure (`web/src/components/page-header.tsx:21`), `max-w-[28rem]` auth card and toasts (`web/src/components/centered-shell.tsx:82`), `max-w-[72rem]` shell measure growing to 88, 104 and 120rem on wide screens (`web/src/components/app-shell.tsx:30`).
- Shell gutters: 8px, 12px from 360px, 16px from 380px, 24px from `md` (`web/src/components/app-shell.tsx:29`).
- About 486 arbitrary px, rem or % values across 114 files, mostly sizes (`size-[18px]`, `size-[56px]`) and small nudges (`mt-[3px]`, `web/src/components/today/habit-row.tsx:126`).

### 1.7 Radii, borders, shadows

| Token | Hearthlight | Fieldnotes | Tidepool | Marigold |
| --- | --- | --- | --- | --- |
| `--radius` | 16px | 6px | 18px | 12px |
| `--cell-radius` | 9999px (round) | 3px | 14px (squircle) | 6px |
| `--border-width` | 0px | 1px | 1px | 2px |
| `--shadow-card` light | two soft warm layers | `none` | one faint cool layer | `3px 3px 0 0` hard ink offset |
| `--shadow-card` dark | two dark layers | `none` | one dark layer | hard offset |

Lines: `web/src/themes/*.css:44-48` (light) and the `--shadow-card` line near the end of each dark rule (`web/src/themes/hearthlight.css:104`).

- Derived radii: `rounded-sm` radius-4, `rounded-md` radius-2, `rounded-lg` radius, `rounded-xl` radius+4, `rounded-cell` (`web/src/index.css:50-54`). Usage: `rounded-md` 39, `rounded-full` 28, `rounded-xl` 24, `rounded-lg` 14, `rounded-cell` 4, `rounded-sm` 4. Cards are `rounded-xl` (`web/src/components/ui/card.tsx:6`), controls `rounded-md`.
- Borders follow the theme where it matters: cards use `border-[length:var(--border-width)]` (`web/src/components/ui/card.tsx:6`); controls use `max(1px, var(--border-width))` so inputs keep an edge in Hearthlight (`web/src/components/ui/input.tsx:8`); checkbox, radio and switch use `max(1.5px, ...)` (`web/src/components/ui/checkbox.tsx:11`).
- One elevation token. `shadow-card` is used 14 times; outliers are listed in 5.2.

### 1.8 Z-index and breakpoints

| Layer | Value | Where |
| --- | --- | --- |
| Decorative scene, reward halos | `-1` | `web/src/motion.css:19`, `web/src/components/today/reward.css:5` |
| Sticky header, phone tab bar, raised cells | `z-10` | `web/src/components/app-shell.tsx:110,124` |
| Update toast | `z-20` | `web/src/components/update-toast.tsx:38` |
| Sheets, scrims, row menu | `z-40` | `web/src/components/today/note-sheet.tsx:110-113`, `web/src/components/today/row-menu.tsx:9` |
| Select popover, moving toast, router-level overlay | `z-50` | `web/src/components/ui/select.tsx:34`, `web/src/pages/today.tsx:611`, `web/src/router.tsx:114` |

No z-index tokens; the ladder is consistent by convention.

Breakpoints are Tailwind defaults (`sm` 40rem, `md` 48rem, `lg` 64rem, `2xl` 96rem; `xl` unused) plus custom widths: `min-[360px]` and `min-[380px]` for phones (`web/src/components/app-shell.tsx:29`), `max-[359px]`, `max-[379px]`, and `min-[137.5rem]` / `min-[200rem]` for 21:9 and 32:9 (`web/src/components/app-shell.tsx:30`, mirrored in `web/src/motion.css:51-67`). No container queries. Roles: `md` switches the tab bar to a header nav, `lg` switches Today, History and the habit editors to two columns.

### 1.9 Motion

Theme tokens (`web/src/themes/*.css:53-58`):

| Theme | fast | base | slow | easing | reward |
| --- | --- | --- | --- | --- | --- |
| Hearthlight | 120ms | 220ms | 420ms | `cubic-bezier(0.2, 0.8, 0.2, 1)` | glow |
| Fieldnotes | 110ms | 200ms | 440ms | `cubic-bezier(0.32, 0.72, 0.24, 1)` | fill |
| Tidepool | 140ms | 260ms | 480ms | `cubic-bezier(0.22, 1, 0.36, 1)` | rise |
| Marigold | 90ms | 160ms | 300ms | `cubic-bezier(0.3, 0.9, 0.3, 1)` | fill |

- Defaults: every bare `transition-*` utility uses `--ember-dur` and `--ember-ease` (`web/src/index.css:61-62`). Buttons press to `scale(0.98)` (`web/src/components/ui/button.tsx:7`).
- Check-in reward: JS reads the `--ember-reward` keyword (`web/src/components/today/reward.ts:12-16`) and applies `ember-reward-<kind>` classes; the keyframes live in `web/src/components/today/reward.css:100-182` and use `--ember-dur-slow` and `--ember-ease`. Glow is a flame-coloured halo, fill wipes a card-coloured cover off the check and sweeps a done tint across the row, rise lifts a small flame dot.
- Page arrival: `ember-arrive` (fade up 16px, 500ms, staggered children) on `main[data-arrive]` (`web/src/motion.css:376-393`, keyframe `:521`). Sheets reuse it at `--ember-dur` (`web/src/components/today/note-sheet.tsx:114`).
- Ambient scene: 12 keyframes in `web/src/motion.css:528-683` (glow, stars, drifting clouds, sparks, campfire flicker, breathing halo), applied in `web/src/motion.css:396-491` with hard-coded durations from 1.9s (flame flicker) to 150s (clouds). Paused when hidden (`web/src/motion.css:501-504`), removed in forced colours (`web/src/motion.css:510-519`).
- JS motion: `motion` 14.9 (`web/package.json:40`) for the Today ember, the rolling check-in number and the History day panel (`web/src/components/history/day-panel.tsx:62-68`); durations come from the theme tokens via `themeSeconds` (`web/src/components/today/reward.ts:19-24`).
- Reduced motion: a global rule cuts every animation and transition to 0.01ms (`web/src/index.css:110-119`); decorative animation only starts under `prefers-reduced-motion: no-preference` (`web/src/motion.css:375`); the reward is skipped (`web/src/components/today/reward.ts:13`); `motion` components use `useReducedMotion`.
- Calm mode (an account setting, `web/src/components/settings/appearance-card.tsx:81-94`, applied as `data-calm` by `web/src/lib/theme.ts:177-182` and `web/public/theme-init.js:10`): no reward (`web/src/calm.css:2-5`), no animation or transition at all (`web/src/calm.css:11-17`), every `data-calm-hide` element hidden (`web/src/calm.css:7-9`: illustrations, embers, the ambient campfire), and a calmer palette. The calm palette is the base palette with chroma halved (306 of 312 values within rounding), hue unchanged, and lightness adjusted on 56 values to keep contrast. It is written out by hand, not derived.

## 2. Components

Stack: Radix through the `radix-ui` umbrella package 1.8 (`web/package.json:41`), `class-variance-authority` (`web/package.json:35`), `cn()` = `twMerge(clsx())` (`web/src/lib/utils.ts:5-7`), lucide-react (`web/package.json:38`). There is no `components.json`; the `ui/` files are shadcn-style by hand, restyled to the token contract. Focus is one global rule, `:focus-visible { outline: 3px solid var(--ring); outline-offset: 2px }` (`web/src/index.css:104-107`); no `ui/` primitive sets its own ring; a few feature components adjust it (5.4).

### 2.1 Primitives (`web/src/components/ui/`)

| Component | Base | Variants and sizes | States | Accessibility | Used in |
| --- | --- | --- | --- | --- | --- |
| Button (`button.tsx:6-71`) | cva + Radix Slot (`asChild`) | default, secondary, ghost, link, destructive, outline (`:10-21`); size default `min-h-[44px]`, icon `size-[44px]` (`:22-25`); `block` | hover via `color-mix` per variant; active `scale-[0.98]`; `disabled` opacity 50; `aria-disabled` opacity 60 and click swallowed (`:57-65`) | 44px target; icons 18px | 79 files |
| BusyButton (`web/src/components/auth/busy-button.tsx:8-26`) | wraps Button | | busy: `aria-disabled` plus Spinner | pairs spinner with text | auth, settings |
| Card (`card.tsx:5-22`) | cva | default (`p-4 md:p-5 gap-3`), farewell (notice colours), stat (secondary fill, no shadow) | none | plain `div` | 27 files, `cardVariants()` on sections in about 10 more |
| Badge (`badge.tsx:5-22`) | cva | default, secondary, outline | none | none | 4 files (streak chip) |
| Notice (`notice.tsx:6-41`) | custom (restyled shadcn Alert) | tones info, success, lowday, notice | none | `role="alert"` for notice tone, `role="status"` otherwise (`:29`); icon `aria-hidden` | 57 files |
| Input, PasswordInput (`input.tsx:7-33`) | custom, shared `controlClass` (`:7-8`) | | `disabled`, `aria-invalid` border to destructive | 44px; show-password toggle `aria-pressed` (`:25`) | 24 files; `controlClass` reused by 4 |
| Label (`label.tsx:6-8`) | Radix Label | | | | 21 files |
| Checkbox (`checkbox.tsx:7-21`) | Radix Checkbox | | checked fills primary; invalid border | 24px box, 44px hit area | 6 files |
| RadioGroup (`radio-group.tsx:6-23`) | Radix RadioGroup | | checked border primary | 44px hit area; forced-colours dot | 7 files |
| Switch (`switch.tsx:6-18`) | Radix Switch | 44x26 track | checked primary track | 44px tall hit area | 11 files |
| Select (`select.tsx:7-66`) | Radix Select, plus `NativeSelect` | | highlighted item `bg-accent` | | only `NativeSelect` is used (6 files); the Radix Select parts have no importer |
| Separator (`separator.tsx:5-28`) | Radix Separator | plain line or labelled | | decorative by default | 1 file |
| Skeleton (`skeleton.tsx:5-7`) | custom | | | `aria-hidden` | 19 files |

Radix parts used directly outside `ui/`: Dialog for all four sheets and DropdownMenu for the Today row menu (`web/src/components/today/row-menu.tsx:23-44`), Accordion in the Guide (`web/src/components/guide/guide.tsx`).

### 2.2 Shared Ember components (`web/src/components/`)

| Component | What it is | Notable details |
| --- | --- | --- |
| AppShell (`app-shell.tsx`) | header, nav, main, notices, footer | sticky 56px header (`:110-111`); phone bottom tab bar, desktop pills (`:122-145`); `aria-current="page"` (`:132`); `main` focusable (`:166-167`); ambient scene behind (`:109`) |
| CenteredShell (`centered-shell.tsx:22-95`) | auth and legal frame | radial `--notice` glow background (`:32`); auth brand panel with a 96px FlameMark (`:62`) |
| PageHeader (`page-header.tsx:4-26`) | h1 plus lede plus aside | h1 28px / 36px, `tabIndex={-1}` for route focus (`:18`) |
| EmptyState (`empty-state.tsx:4-29`) | illustration, title, body, action | illustration `aria-hidden` and `data-calm-hide` (`:20`) |
| DayCell (`day-cell.tsx:14-174`) | the day dot used on Today, History, previews | sizes cell, slot 56px, swatch 24px, pip 6px (`:36-41`); states done, notdone, pending, lowday, paused, beforestart, offday, partial (`:43-56`); each state has a non-colour cue; editable cells are buttons with `aria-expanded`/`aria-controls` (`:138-158`) |
| CheckButton (`today/check-button.tsx:31-86`) | the 56px check-in toggle | `rounded-cell border-2`, pending border, done fill, multi-count text, busy spinner |
| FlameMark (`flame-mark.tsx:4-21`) | the brand flame SVG | `fill-flame` / `fill-flame-core`, `aria-hidden` |
| FormField (`form-field.tsx:9-95`) | label, hint, control, error | wires `aria-describedby`, `aria-invalid`, `aria-required`; error `role="alert"` (`:83`); `focusFirstInvalid` |
| LiveRegion (`live-region.tsx:5-11`) | polite announcer | `sr-only aria-live="polite" aria-atomic` |
| NativeConfirmation (`native-confirmation.tsx:9-36`) | 4s success confirmation | LiveRegion plus a hidden-from-AT Notice |
| Spinner (`spinner.tsx:5-7`) | LoaderCircle `animate-spin` | `aria-hidden`; documented to pair with text |
| ThemePicker (`theme-picker.tsx:8-103`) | Appearance radio cards | each card previews its theme by scoping `data-theme` |
| UpdateToast (`update-toast.tsx:10-68`) | service-worker update notice | fixed above the tab bar, `z-20` |
| LegalLinks, BackLink (`legal-links.tsx:13-28`, `back-link.tsx:26-38`) | footer links, back navigation | 44px targets |

All of these are custom. None is from a shadcn registry.

## 3. Patterns

### 3.1 Layout and page structure

- One column inside a centred measure (72rem, growing on 21:9), gutters from 8px to 24px (`web/src/components/app-shell.tsx:29-30`). Safe-area insets on header, tab bar and sheets; `scroll-padding` keeps focus clear of the header and tab bar (`web/src/index.css:79-84`).
- Two page templates. Narrow: `max-w-[40rem] flex-col gap-6` with optional BackLink and PageHeader (`web/src/pages/settings.tsx:235`, `web/src/pages/routines.tsx:199`). Wide: the full measure with a two-column grid at `lg` (Today `web/src/pages/today.tsx:65-66`, History `web/src/pages/history.tsx:313`, habit edit `web/src/pages/habit-edit.tsx:91`, habit new `web/src/pages/habit-new.tsx:212`). Settings uses a rail from `md` (`web/src/pages/settings.tsx:289`).
- Auth and legal pages use CenteredShell: a 12-column split at `lg` with a brand panel (`web/src/components/centered-shell.tsx:39-82`).
- Navigation: Today (Flame), History (CalendarDays), Settings (SlidersHorizontal) (`web/src/components/app-shell.tsx:24-26`), plus Fireside (FlameKindling) after Today when it is on (`web/src/components/fireside/fireside-nav.ts:1,8`). Phone tabs are 64px with a 3px top bar in primary when active (`:134-137`); desktop items are pills with `bg-secondary` when active (`:135`).

### 3.2 Section headers

No shared component. Sections are `section aria-labelledby` with a hand-written heading. h2 is 22px in most places and 20px on Today (5.4); h3 is 18px. Headings are never uppercase and carry no eyebrow label. The display font and heading weight come from the global h1 to h3 rule (`web/src/index.css:96-103`), so the theme sets the character.

### 3.3 The Today row family

| Row | Anatomy | File |
| --- | --- | --- |
| HabitRow | Card, grid `1fr 56px`: name (h3 18px), meta line (14px muted with a 14px icon: streak badge, "n of t this week" pips, paused, rest day, low day, done), row actions (undo one, edit pencil, "..." menu), CheckButton on the right. Done rows get an 8% done tint and a 3px inset edge | `web/src/components/today/habit-row.tsx:94-229` |
| Routine block | section with h3 name, pencil and menu, member HabitRows | `web/src/pages/today.tsx:468-510` |
| Let-go row | Card with h3, days-free meta, edit, menu and a secondary "I slipped" button instead of a check | `web/src/components/letgo/letgo-section.tsx:36-89` |
| Not for today | disclosure h2 with chevron; rows at 85% opacity | `web/src/pages/today.tsx:673-703` |
| Habit preview | separate Card with an 18px h2 and a 56px pending square | `web/src/components/habits/habit-preview.tsx:9-17` |

Supporting pieces: the low-day Notice, the "Mark today as a low day" ghost button with a Moon, and Today's ember (a 64px flame that grows with each check-in, `web/src/components/today/todays-ember.tsx:26-31`).

### 3.4 Sheets, dialogs, pages

| Surface | When | How |
| --- | --- | --- |
| Bottom sheet that becomes a centred dialog | short, focused input from a row: note, "I slipped", send to a friend | Radix Dialog; bottom sheet `rounded-t-2xl max-h-[85dvh]`, centred from `sm` (`web/src/components/today/note-sheet.tsx:108-117`), from `md` for SendSheet (`web/src/components/fireside/send-sheet.tsx:71-73`) |
| Side panel | the "How this works" guide on wide screens | non-modal Radix Dialog, 24rem from `lg`, bottom sheet below (`web/src/components/guide/guide.tsx:14,121-209`) |
| Dropdown | row actions (note, move up or down) | Radix DropdownMenu, `modal={false}` (`web/src/components/today/row-menu.tsx:23-44`) |
| Inline panel | History and Year day detail, Not for today, habit delete confirm | no overlay (`web/src/components/history/day-panel.tsx:62-78`) |
| Page | create and edit habits and routines, moments, settings groups | router (`web/src/router.tsx:213-225`) |

No native `<dialog>`. All sheets share one scrim, `color-mix(foreground 35%, transparent)` at `z-40`, with no fade. Focus trap and return come from Radix; the guide and the slip sheet redirect focus on close (`web/src/components/guide/guide.tsx:108-115`).

### 3.5 Iconography

- One set: lucide-react, default stroke 2, except the check-in check at 2.5 (`web/src/components/today/check-button.tsx:76`).
- Sizes in use: 14px (meta, day cells, badges), 16px, 18px (button default, Notice, rail, back links), 20px, 22px (phone nav, Settings group list, guide trigger), 24px (landing), 26px (check-in). No size tokens.
- Custom SVG only for the brand: FlameMark and the illustrations below.

### 3.6 Illustration and imagery

All inline SVG drawn with tokens, all `data-calm-hide`:

| Art | Where | Use |
| --- | --- | --- |
| HearthIllustration (stones, flame, sparks) | `web/src/components/today/hearth-illustration.tsx:4` | empty Today and Routines |
| NotebookIllustration, CalendarIllustration | `web/src/components/history/illustrations.tsx:5,38` | empty History and Year, "pick a day" |
| EnvelopeSpark, ShieldLeaf | `web/src/components/auth/illustrations.tsx:4,22` | email code, consent |
| DriftingSpark, StraySpark | `web/src/components/habits/habit-edit-sections.tsx:725`, `web/src/pages/not-found.tsx:6-25` | missing habit or routine, 404 |
| TodaysEmber | `web/src/components/today/todays-ember.tsx:31-66` | grows from 0.85 to 1.1 scale with check-ins |
| AmbientScene | `web/src/components/ambient-scene.tsx:44-80` | full-width landscape behind every app page: hills, moon, stars, drifting clouds, a campfire on the moment screen |

Raster only for the landing screenshots, the Open Graph image and the app icons (`web/public/`).

### 3.7 Copy as a visual rule

- Sentence case everywhere in `web/src/locales/en.json`; no title case found. No exclamation marks. No em or en dashes (a written rule for German in `docs/contracts/i18n-v1.md:12-15`).
- `…` for in-progress labels ("Signing out…", `web/src/locales/en.json:46`) and a middle dot as separator ("Not for today · {{count}}", `web/src/locales/en.json:821`).
- Counts read as progress, never as loss ("2 of 3 this week"); streaks show only above zero (`web/DESIGN.md:91,104`).
- Emoji appear in mood chips (`web/src/components/journal/moods.ts:13-17`), template names (`web/src/locales/en.json:698-716`), local-mode weekly headlines and the "Done ✅" notification action (`web/src/locales/en.json:57`).
- Written tone rules: `web/DESIGN.md:80-95` and the product principles in `docs/v2-plan.md`. No casing rule is written down anywhere.

## 4. What makes Ember feel like Ember

These come from the code, not from intent statements. They are the traits the global system could leave to Ember rather than absorb.

1. **A small fire, everywhere and quiet.** The flame is the logo (`web/src/components/flame-mark.tsx`), the Today nav icon (`web/src/components/app-shell.tsx:24`), the streak chip icon, Today's ember that grows with each check-in (`web/src/components/today/todays-ember.tsx:26-27`), the campfire in the ambient scene and the hearth in empty states. `--flame` and `--flame-core` stay warm in every theme, including the cool Tidepool.
2. **Warm light, not white.** Hearthlight neutrals sit at OKLCH hue 50 to 80 with low chroma: cream paper by day, wood-smoke charcoal by night. Shadows are tinted brown in light mode (`web/src/themes/hearthlight.css:48`). Hearthlight has no borders at all (`--border-width: 0px`); edges come from soft shadow.
3. **Rest is a colour, failure is not.** There is no red token and the destructive role is honey (`docs/contracts/design-tokens-v1.md:35,68`). Low days are lavender with a moon, paused days a soft hatch, empty days just empty (`web/src/components/day-cell.tsx:43-56`). Nothing says "missed".
4. **Reward as warmth, not celebration.** A check-in gets one glow, fill or rise of about 300 to 480ms (`web/src/components/today/reward.css`); no confetti, no sound, no counters jumping.
5. **A living but slow world.** The ambient landscape moves on cycles from about 2 seconds (flame flicker, sparks) to 150 seconds (clouds), all eased and looping (`web/src/motion.css:396-491`); pages arrive with a short fade up. Everything stops under reduced motion, and calm mode goes further: no motion, no decorations, half the colour.
6. **Soft, round, generous.** Rounded display type at weight 800 (Nunito), round coin-like day cells (`--cell-radius: 9999px`), 16px card radius, 56px check targets, sentence-case copy that speaks gently in the second person.

## 5. Inconsistencies and one-offs

Findings for the system draft. None is fixed in this slice.

### 5.1 Token sources outside the token files

| Finding | Where |
| --- | --- |
| Calm palette is a full hand copy of every theme (312 values), derivable by rule (chroma times 0.5 plus contrast nudges) | `web/src/calm.css:19-355` |
| Theme-colour and notification hexes are hand copies; `colors` equal each theme's `--background`, but `notification` is `--flame` for Hearthlight and the dark `--primary` for the other three | `web/src/lib/theme.ts:22-44` |
| Static Hearthlight copies: `theme-color` metas, manifest colours, boot-splash flame, notification icon tint | `web/index.html:8-9,45,48`, `web/public/manifest.webmanifest:9-10`, `web/capacitor.config.ts:13` |
| Boot-splash text fallback `#6b5a4c`, close to but not equal to `--muted-foreground` (`#665448`) | `web/src/index.css:128` |
| Android widget, status bar and splash copies (exact today, see 1.4); widget glow `#F5C978` is not a token | `WidgetPalette.kt:17-24`, `ember_colors.xml`, `mobile/android/app/src/main/res/drawable/widget_glow.xml:6-8` |
| Discord bot embeds use their own colours: `0xE8873A` orange (10 sites), `0xF4845F` coral (4 sites), `0x5865F2` Discord blurple (2 sites). Coral sits near the "no red or pink" line and blurple is listed as avoided in `web/DESIGN.md:15` | `src/Ember.Bot/Jobs/WeeklySummaryJob.cs:109`, `src/Ember.Bot/Modules/ExportModule.cs:63`, `src/Ember.Bot/Jobs/MissedDayReflectionJob.cs:132` |
| Marigold's theme file carries a component selector, which the contract says themes must not do | `web/src/themes/marigold.css:105-107` |

### 5.2 Values that bypass tokens

- Type: no scale token; 219 arbitrary sizes (1.5).
- Hover and tint percentages: `color-mix` with ad hoc amounts (8%, 14%, 22%, 30%, 35%) in about 20 components (1.3).
- Shadows: `shadow-md` on the update toast (`web/src/components/update-toast.tsx:40,48`) and `shadow-sm` in the feedback form (`web/src/components/settings/feedback-form.tsx:306`) use Tailwind defaults instead of `--shadow-card`; Today's moving toast has its own `shadow-[0_10px_28px_...]` (`web/src/pages/today.tsx:432`).
- Radii: `rounded-[4px]`, `[6px]`, `[8px]` on heatmap cells (`web/src/components/year/year-heatmap.tsx:21,208`), `rounded-[1.75rem]` on the landing screenshot (`web/src/pages/landing.tsx:116`), `rounded-t-2xl` on sheets, and `min(var(--radius-sm), 6px)` on the checkbox (`web/src/components/ui/checkbox.tsx:11`).
- Durations: ambient keyframes and `ember-arrive` 500ms are hard-coded (`web/src/motion.css:378-490`); `duration-300` and `duration-150` (`web/src/components/today/habit-row.tsx:177`, `web/src/pages/today.tsx:433`); `transform 1s linear` inline (`web/src/components/moment/breathing-player.tsx:161`); `ease: 'easeOut'` in the rolling number (`web/src/components/today/rolling-number.tsx`).
- Spinner is not `motion-safe`, so under reduced motion it renders but does not visibly spin (`web/src/components/spinner.tsx:6`).

### 5.3 Near-duplicate components

| Pattern | Copies |
| --- | --- |
| Sheet content class string, scrim and close button | identical in `web/src/components/today/note-sheet.tsx:110-140`, `web/src/components/letgo/slip-sheet.tsx:98-128`, `web/src/components/guide/guide.tsx:123-151`; SendSheet differs (`web/src/components/fireside/send-sheet.tsx:70-83`) |
| Day cell look | DayCell, the let-go history cell (`web/src/components/letgo/letgo-history.tsx:73,94-105`) and the year swatch (`web/src/components/year/year-heatmap.tsx:52-66`) |
| Let-go history | re-implements the History layout, legend, stats and day panel, (`web/src/components/letgo/letgo-history.tsx:234`) with the same `day-panel-heading` id (`web/src/components/letgo/letgo-history.tsx:290`, `web/src/components/history/day-panel.tsx:74`) |
| Rows | HabitRow, let-go row, routine card and habit preview are four anatomies for one idea (3.3) |
| Reorder | up and down buttons on Routines (`web/src/pages/routines.tsx:132-153`), numbered rows (`web/src/components/routines/routine-habits.tsx:78-99`), and a menu plus drag on Today (`web/src/components/today/row-menu.tsx:58-81`) |
| Edit affordance | pencil icon link (`web/src/components/today/habit-row.tsx:202-206`) against a text "Edit" button (`web/src/pages/routines.tsx:154-158`) |
| Pills | nav pills, Month/Year switch, mood chips, routine member chips, streak badge, all hand-styled (`web/src/components/history/view-switch.tsx:22`, `web/src/components/journal/day-journal.tsx:295`) |
| Toggle row | `flex min-h-[44px] items-center justify-between gap-4` in 5 files (`web/src/components/settings/appearance-card.tsx:80`) |
| Hint text | `text-[0.875rem] text-muted-foreground` typed by hand about 100 times, while FormField has its own hint |
| Back links | BackLink and three hand-rolled ArrowLeft links (`web/src/pages/download.tsx:99`, `web/src/pages/legal.tsx:77`, `web/src/pages/whats-new.tsx:70`) |
| Icon buttons | PasswordInput toggle (`web/src/components/ui/input.tsx:22-30`) and the feedback remove button hand-roll what `Button size="icon"` provides |
| Unused | the Radix `Select` parts in `web/src/components/ui/select.tsx:8-61` |

### 5.4 Conflicting choices

- h2 is 22px in most sections and 20px on Today and in let-go and routines (`web/src/pages/today.tsx:618`, `web/src/components/settings/settings-card.tsx:30`).
- Icon sizes: 22px for nav and the Settings group list, 18px for the Settings rail and buttons, 20px against 18px for the same chevron (`web/src/pages/today.tsx:689`, `web/src/components/guide/guide.tsx:167`). CalendarDays means History, the "Habits and days" settings group and "yesterday" on Today.
- Wordmark is 18px in the app header and 22px on auth and landing (`web/src/components/app-shell.tsx:115`, `web/src/components/centered-shell.tsx:44`).
- Sheet breakpoint is `sm` for two sheets, `md` for SendSheet and `lg` for the guide.
- Focus rings: the global rule is a 3px `--ring` outline at 2px offset, but several components change it. The feedback picture button sets its own 2px ring with a negative offset (`web/src/components/settings/feedback-form.tsx:304`); Settings group rows and phone tabs inset it (`web/src/pages/settings.tsx:247,267`, `web/src/components/app-shell.tsx:134`); heatmap cells use offset 1 (`web/src/components/year/year-heatmap.tsx:208`); open day cells keep a persistent ring through `aria-expanded:outline-3` (`web/src/components/day-cell.tsx:152`, `web/src/components/letgo/letgo-history.tsx:105`); the new-habit choice uses `focus-visible:border-ring` (`web/src/pages/habit-new.tsx:177`).
- Day cells shrink to 40px below 360px width (`web/src/components/day-cell.tsx:37`), under the 44px target rule in `docs/contracts/design-tokens-v1.md:64`.
- Emoji: `web/DESIGN.md:82` says emoji only where the server sends them; the client ships its own in moods, local templates and local weekly headlines (3.7).

### 5.5 Stale design docs

- `web/DESIGN.md:17-78` still documents the retired Hearth tokens (`--color-bg`, `--color-surface`, weight 650, a `.day` class) that no longer exist; the status line at `web/DESIGN.md:3` says so, but the tables read as current.
- `design/layout.md` has drifted from the code in several places: Settings is a group index with a rail from `md`, not one page with a rail at `lg` (`design/layout.md:239`, `web/src/pages/settings.tsx:66,289`); Sign out shows from `lg`, not `md` (`design/layout.md:90`, `web/src/components/app-shell.tsx:154`); Today's ember stacks under the heading on phones (`design/layout.md:147`); the weekly card reads "Last 7 days", not "Your week" (`design/layout.md:165`); routines, let-go, Fireside and the guide are not in the spec.

### 5.6 Where themes are not covered

- Visual baselines exist only for the default theme in light and dark (`web/e2e/visual.spec.ts:47-72`); Fieldnotes, Tidepool, Marigold and calm mode have none.
- The Android widget ignores calm mode, and its flame is Hearthlight light in every theme (1.4).
- Density: Tidepool's 5px unit scales all spacing by 25%, but literal px sizes (targets, icons, 24px controls, 56px cells) do not, so proportions shift between themes.
- `--space-unit` is set only inside the `[data-theme]` rules, so an element outside any themed root would get an unset spacing scale (`web/src/themes/hearthlight.css:2-4` covers `:root:not([data-theme])`, which keeps the app itself safe).

## 6. Candidate split (proposal for Nico)

A first cut of what could move to the planned global library (`@nw/tokens`, `@nw/ui` in `Tzanto01/design`, not created yet) and what stays Ember's. This is a proposal, not a decision.

### 6.1 Tokens

| Token group | Proposal | Reason |
| --- | --- | --- |
| Surface, text, action, quiet fills, lines (`background` to `ring`) | global (library) | the shadcn-compatible role set any app needs; Ember supplies the values |
| `destructive`, `notice` (+fg, border), `success` | global (library) | generic feedback roles; Ember's "never red" is a value choice it keeps in its theme |
| Contrast pairs and the checker (`design/contrast.mjs`) | global (library) | the pair list is app-neutral and is the main quality gate |
| `done`, `pending`, `empty`, `paused`, `beforestart`, `lowday`, `heat-1/2` | Ember-specific | habit day states; other apps have no such concept |
| `flame`, `flame-core` | Ember-specific | the brand mark; the library can offer a generic `brand` slot |
| `chart-1..3` | global (library) | generic data series |
| `radius`, `cell-radius`, `border-width`, `shadow-card`, `space-unit` | global (library) as knobs, with `cell-radius` renamed to a generic "small control" radius | these knobs are what let each app feel different while sharing components |
| Font stacks, heading weight and tracking | global (library) as slots; the chosen families Ember-specific | |
| Type scale (12 to 48px) | global (library) | needs to exist as named steps first (5.2) |
| `ember-dur-*`, `ember-ease` | global (library), without the `ember-` prefix | generic motion scale |
| `ember-reward` | Ember-specific | check-in reward |
| Ambient scene durations and keyframes | Ember-specific | the scene is Ember's world |
| Calm mode | global (library) as a third axis next to light and dark | generic accessibility and sensory setting; the derivation rule is app-neutral |
| Z-index ladder, breakpoints, container widths, 44px target | global (library) | layout plumbing |
| Android widget palette | generated by the library pipeline, values Ember-specific | removes the hand copies in 1.4 |

### 6.2 Components

| Component | Proposal | Reason |
| --- | --- | --- |
| Button, BusyButton, Card, Badge, Notice, Input, PasswordInput, Label, Checkbox, RadioGroup, Switch, NativeSelect, Separator, Skeleton, Spinner | global (library) | generic primitives already written to tokens |
| FormField, LiveRegion, focus rule, `focusFirstInvalid` | global (library) | accessibility plumbing |
| Sheet (one component from the three copies), DropdownMenu wrapper | global (library) | generic overlays once deduplicated |
| PageHeader, EmptyState, BackLink, section header (new), toggle row (new), hint text (new) | global (library) | generic page parts; EmptyState takes the illustration as a slot |
| AppShell, CenteredShell | global (library) as layouts, Ember fills brand and nav | the shell structure is generic, the content is not |
| ThemePicker | global (library) | any multi-theme app needs it |
| DayCell, CheckButton, HabitRow, YearHeatmap, TodaysEmber, reward | Ember-specific | habit domain |
| FlameMark, illustrations, AmbientScene | Ember-specific | brand and world |
| NativeConfirmation, UpdateToast | global (library) | PWA and Capacitor plumbing shared by future apps |

## 7. Open questions for Nico

1. **Calm mode: keep it as a hand-tuned palette or generate it?** Today it is 312 hand values that follow a rule. Recommendation: generate it in `@nw/tokens` (chroma times 0.5, then nudge lightness until every contrast pair passes), with per-value overrides allowed, and make calm a library-wide mode.
2. **Should all four themes stay, and are they Ember's or the library's?** Recommendation: the library ships one neutral reference theme plus the theme schema; Hearthlight, Fieldnotes, Tidepool and Marigold stay Ember themes. Whether to keep all four is a product call.
3. **One h2 size and named type steps.** Recommendation: adopt the de facto scale as named steps (12, 13, 14, 16, 18, 22, 28, 36, 48) and drop the 20px h2 in favour of 22px.
4. **Density per theme.** Tidepool's 5px unit scales spacing but not targets and icons. Recommendation: keep density as a theme knob in the library, keep touch targets fixed, and let icon and control sizes follow named size tokens instead of literal px.
5. **Emoji policy.** The written rule (server-sent only) and the code (mood chips, templates, local headlines) disagree. Recommendation: allow emoji only as content glyphs in user-facing data (moods, template names), never in UI chrome or headings, and write that into the system.
6. **Discord bot colours.** Recommendation: feed the bot from the same token build (flame for warm embeds, primary for neutral ones) and drop the coral and Discord blurple.
7. **Retire `web/DESIGN.md` and update `design/layout.md`?** Recommendation: once the system draft lands, replace `web/DESIGN.md` with a pointer to it and move its tone and day-state tables into the Ember system; refresh `design/layout.md` against the current screens in the same pass.
