import {
  formatAmount,
  goalTaps,
  inputUnit,
  type InputUnit,
  toCanonical,
  toTyped,
} from '@/lib/habit-visuals/units';
import { auth } from '@/lib/services';
import { useStore } from '@/lib/store';
import { currentLanguage, t } from '@/lib/i18n';
import { limits } from '@/lib/local/limits';
import { portionRanges } from '@/lib/local/picture-rules';
import type { Frequency, HabitPicture, PortionUnit, UnitSystem } from '@/lib/models';

/** The unit a portion is typed in on the form: {@link inputUnit}, with metric distance in km. */
export type TypedUnit = InputUnit | 'km';

/** The picture part of the habit form; amounts are kept as typed. */
export interface PictureFormState {
  /** Null is plain. */
  picture: HabitPicture | null;
  /** One tap's portion as typed, in {@link typedUnit} of `portionUnit`. */
  portion: string;
  /** The canonical unit of `portion`; null for pictures without a portion. */
  portionUnit: PortionUnit | null;
  /** The bottle's daily goal as typed, in L (metric) or fl oz (imperial); never stored. */
  goal: string;
  /** The create-time picture offer was answered either way. */
  pictureOfferAnswered: boolean;
}

/** The form state fields the picture field reads and changes. */
export type PictureFieldState = PictureFormState & { frequency: Frequency; timesPerDay: number };

export const plainPicture: PictureFormState = {
  picture: null,
  portion: '',
  portionUnit: null,
  goal: '',
  pictureOfferAnswered: false,
};

const fractionDigits: Record<TypedUnit, number> = {
  ml: 0,
  'fl oz': 1,
  m: 0,
  km: 2,
  mi: 2,
  min: 0,
  pages: 0,
};

export function typedUnit(unit: PortionUnit, units: UnitSystem): TypedUnit {
  return unit === 'm' && units === 'metric' ? 'km' : inputUnit(unit, units);
}

function format(value: number, digits: number): string {
  return new Intl.NumberFormat(currentLanguage(), {
    maximumFractionDigits: digits,
    useGrouping: false,
  }).format(value);
}

function parse(text: string, digits: number): number | null {
  const trimmed = text.trim();
  const pattern = digits ? /^\d+(?:[.,]\d+)?$/ : /^\d+$/;
  return pattern.test(trimmed) ? Number(trimmed.replace(',', '.')) : null;
}

function toStored(typed: number, unit: PortionUnit, units: UnitSystem): number {
  return unit === 'm' && units === 'metric'
    ? Math.round(typed * 1000)
    : toCanonical(typed, unit, units);
}

function toShown(amount: number, unit: PortionUnit, units: UnitSystem): number {
  return unit === 'm' && units === 'metric'
    ? Math.round(amount / 10) / 100
    : toTyped(amount, unit, units);
}

/** A stored portion as the form shows it. */
export function portionText(amount: number, unit: PortionUnit, units: UnitSystem): string {
  return format(toShown(amount, unit, units), fractionDigits[typedUnit(unit, units)]);
}

/** The typed portion as the whole canonical amount, or null when it isn't a number in that unit's format. */
export function portionAmount(
  state: Pick<PictureFormState, 'portion' | 'portionUnit'>,
  units: UnitSystem,
): number | null {
  if (!state.portionUnit) return null;
  const typed = parse(state.portion, fractionDigits[typedUnit(state.portionUnit, units)]);
  return typed === null ? null : toStored(typed, state.portionUnit, units);
}

function startPortion(picture: HabitPicture, unit: PortionUnit, units: UnitSystem): string {
  const metric = units === 'metric';
  const typed =
    unit === 'min'
      ? picture === 'shelf'
        ? 15
        : 10
      : unit === 'pages'
        ? 10
        : unit === 'ml'
          ? metric
            ? 250
            : 8
          : metric
            ? 1
            : 0.5;
  return format(typed, fractionDigits[typedUnit(unit, units)]);
}

/** The bottle goal in ml, or null when it isn't a positive number. */
export function goalAmount(goal: string, units: UnitSystem): number | null {
  const typed = parse(goal, units === 'metric' ? 2 : 1);
  if (!typed) return null;
  return units === 'metric' ? Math.round(typed * 1000) : toCanonical(typed, 'ml', units);
}

/** An amount in ml as the goal field shows it. */
export function goalText(ml: number, units: UnitSystem): string {
  return units === 'metric'
    ? format(Math.round(ml / 10) / 100, 2)
    : format(Math.round(toTyped(ml, 'ml', units)), 1);
}

/** The bottle goal sets times per day, for daily and weekday habits; other states come back unchanged. */
export function withGoal<T extends PictureFieldState>(state: T, units: UnitSystem): T {
  if (state.picture !== 'bottle' || state.frequency === 'weekly') return state;
  const goal = goalAmount(state.goal, units);
  const portion = portionAmount(state, units);
  if (!goal || !portion) return state;
  const { timesPerDay } = goalTaps(goal, portion);
  return timesPerDay === state.timesPerDay ? state : { ...state, timesPerDay };
}

/**
 * The state with `picture` chosen: its first unit and a starting portion, and for the bottle a goal (the current
 * times per day when above one, else 2 L, or 64 fl oz in imperial) that sets times per day.
 */
export function choosePicture<T extends PictureFieldState>(
  state: T,
  picture: HabitPicture | null,
  units: UnitSystem,
): T {
  if (picture === state.picture) return state;
  if (!picture) return { ...state, picture: null };
  const unit = portionRanges[picture][0]?.unit ?? null;
  const next: T = {
    ...state,
    picture,
    portionUnit: unit,
    portion: unit ? startPortion(picture, unit, units) : '',
  };
  if (picture !== 'bottle') return next;
  const portion = portionAmount(next, units) ?? 250;
  const goal =
    state.frequency !== 'weekly' && state.timesPerDay > 1
      ? state.timesPerDay * portion
      : units === 'metric'
        ? 2000
        : toCanonical(64, 'ml', units);
  return withGoal({ ...next, goal: goalText(goal, units) }, units);
}

/** The state with the portion's unit switched (shelf and trail), starting from that unit's usual portion. */
export function chooseUnit<T extends PictureFieldState>(
  state: T,
  unit: PortionUnit,
  units: UnitSystem,
): T {
  if (!state.picture || unit === state.portionUnit) return state;
  return { ...state, portionUnit: unit, portion: startPortion(state.picture, unit, units) };
}

/** The bottle goal text after times per day was picked by hand, so the goal keeps describing the taps. */
export function goalForTimes<T extends PictureFieldState>(state: T, units: UnitSystem): T {
  if (state.picture !== 'bottle') return state;
  const portion = portionAmount(state, units);
  return portion ? { ...state, goal: goalText(state.timesPerDay * portion, units) } : state;
}

const canonicalPerTyped: Record<TypedUnit, number> = {
  ml: 1,
  'fl oz': 29.5735,
  m: 1,
  km: 1000,
  mi: 1609.344,
  min: 1,
  pages: 1,
};

function bound(amount: number, unit: PortionUnit, units: UnitSystem, up: boolean): string {
  const typed = typedUnit(unit, units);
  const factor = 10 ** fractionDigits[typed];
  const shown = (amount / canonicalPerTyped[typed]) * factor;
  return format(
    (up ? Math.ceil(shown - 1e-9) : Math.floor(shown + 1e-9)) / factor,
    fractionDigits[typed],
  );
}

/** The unit's short symbol shown after the number ("ml", "fl oz", "min", "pages"). */
export function unitSymbol(unit: TypedUnit): string {
  return t(`habitVisuals.form.unit.${unit === 'fl oz' ? 'flOz' : unit}`);
}

/** Null when the picture's portion and the bottle goal (daily and weekday habits) are numbers the server takes. */
export function habitPictureError(
  state: PictureFieldState & { kind: string },
  units: UnitSystem,
): string | null {
  if (state.kind === 'letGo' || !state.picture || !state.portionUnit) return null;
  const range = portionRanges[state.picture].find((r) => r.unit === state.portionUnit);
  if (!range) return null;
  const amount = portionAmount(state, units);
  if (amount === null || amount < range.min || amount > range.max) {
    return t('habitVisuals.form.portionInvalid', {
      min: bound(range.min, range.unit, units, true),
      max: bound(range.max, range.unit, units, false),
      unit: unitSymbol(typedUnit(range.unit, units)),
    });
  }
  return null;
}

/** Null when the bottle goal is a positive amount, or isn't asked for (other pictures, weekly habits). */
export function habitGoalError(
  state: PictureFieldState & { kind: string },
  units: UnitSystem,
): string | null {
  if (state.kind === 'letGo' || state.picture !== 'bottle' || state.frequency === 'weekly')
    return null;
  return goalAmount(state.goal, units)
    ? null
    : t('habitVisuals.form.goalInvalid', { unit: goalSymbol(units) });
}

/** The goal field's unit: L in metric, fl oz in imperial. */
export function goalSymbol(units: UnitSystem): string {
  return units === 'metric' ? t('habitVisuals.form.unit.L') : unitSymbol('fl oz');
}

/** What the goal adds up to, for the line under it; null without a valid goal and portion. */
export function goalSummary(
  state: PictureFieldState,
  units: UnitSystem,
): { text: string; capped: boolean } | null {
  if (state.picture !== 'bottle' || state.frequency === 'weekly') return null;
  const goal = goalAmount(state.goal, units);
  const portion = portionAmount(state, units);
  if (!goal || !portion) return null;
  const taps = goalTaps(goal, portion);
  const locale = currentLanguage();
  const total = formatAmount(taps.total, 'ml', units, locale).text ?? '';
  const goalShown = formatAmount(goal, 'ml', units, locale).text ?? '';
  if (taps.capped) {
    return {
      capped: true,
      text: t('habitVisuals.form.goalCapped', {
        max: limits.timesPerDay,
        total,
        goal: goalShown,
        portion: `${bound(taps.minPortion, 'ml', units, true)} ${unitSymbol(typedUnit('ml', units))}`,
      }),
    };
  }
  return {
    capped: false,
    text:
      total === goalShown
        ? t('habitVisuals.form.goalTaps', { count: taps.timesPerDay })
        : t('habitVisuals.form.goalTapsTotal', { count: taps.timesPerDay, total }),
  };
}

/** The account's unit system for portions and totals; metric until the server says otherwise. */
export function useUnits(): UnitSystem {
  return useStore(auth.me)?.units ?? 'metric';
}
