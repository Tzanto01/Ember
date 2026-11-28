import {
  defaultBottleGoal,
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
  /** The exact goal in ml while `goal` shows a derived amount (times per day × portion), null once typed. */
  goalMl: number | null;
  /** Times per day before the bottle goal changed it, restored when the bottle is switched off. */
  timesBeforeGoal: number | null;
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
  goalMl: null,
  timesBeforeGoal: null,
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

function currentGoal(state: PictureFormState, units: UnitSystem): number | null {
  return state.goalMl ?? goalAmount(state.goal, units);
}

function applyGoal<T extends PictureFieldState>(state: T, units: UnitSystem): T {
  if (state.picture !== 'bottle' || state.frequency === 'weekly') return state;
  const goal = currentGoal(state, units);
  const portion = portionAmount(state, units);
  if (!goal || !portion) return state;
  const { timesPerDay } = goalTaps(goal, portion);
  if (timesPerDay === state.timesPerDay) return state;
  return { ...state, timesPerDay, timesBeforeGoal: state.timesBeforeGoal ?? state.timesPerDay };
}

/** The goal as typed; for a daily or weekday bottle it sets times per day to `ceil(goal / portion)`. */
export function typeGoal<T extends PictureFieldState>(
  state: T,
  goal: string,
  units: UnitSystem,
): T {
  return applyGoal({ ...state, goal, goalMl: null }, units);
}

/** The portion as typed; for a daily or weekday bottle the goal then sets times per day. */
export function typePortion<T extends PictureFieldState>(
  state: T,
  portion: string,
  units: UnitSystem,
): T {
  return applyGoal({ ...state, portion }, units);
}

/**
 * The state with `picture` chosen: its first unit and a starting portion. Leaving the bottle restores the times per
 * day its goal replaced. The bottle's goal is times per day × portion, so choosing it changes nothing else, except
 * with `startGoal` (a new habit at once a day): then it starts from 2 L (64 fl oz in imperial), which sets times
 * per day.
 */
export function choosePicture<T extends PictureFieldState>(
  state: T,
  picture: HabitPicture | null,
  units: UnitSystem,
  startGoal = false,
): T {
  if (picture === state.picture) return state;
  const base: T = {
    ...state,
    timesPerDay: state.timesBeforeGoal ?? state.timesPerDay,
    goal: '',
    goalMl: null,
    timesBeforeGoal: null,
  };
  if (!picture) return { ...base, picture: null };
  const unit = portionRanges[picture][0]?.unit ?? null;
  const next: T = {
    ...base,
    picture,
    portionUnit: unit,
    portion: unit ? startPortion(picture, unit, units) : '',
  };
  if (picture !== 'bottle') return next;
  const portion = portionAmount(next, units) ?? 250;
  if (startGoal && next.frequency !== 'weekly' && next.timesPerDay === 1) {
    const goal = units === 'metric' ? defaultBottleGoal : toCanonical(64, 'ml', units);
    return applyGoal({ ...next, goal: goalText(goal, units), goalMl: goal }, units);
  }
  const goal = next.timesPerDay * portion;
  return { ...next, goal: goalText(goal, units), goalMl: goal };
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

/**
 * The bottle goal rewritten as times per day × portion after another field changed (times per day, how often), so
 * the goal keeps describing the taps; times per day itself never changes here.
 */
export function goalForTimes<T extends PictureFieldState>(state: T, units: UnitSystem): T {
  if (state.picture !== 'bottle') return state;
  const portion = portionAmount(state, units);
  if (!portion) return state;
  const goal = state.timesPerDay * portion;
  return { ...state, goal: goalText(goal, units), goalMl: goal };
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
  return currentGoal(state, units)
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
  const goal = currentGoal(state, units);
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
