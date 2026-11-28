import { limits } from '../local/limits';
import type { Habit, PortionUnit, UnitSystem } from '../models';

const mlPerFluidOunce = 29.5735;
const metersPerMile = 1609.344;

/** The bottle's daily goal the new-habit form starts from, in ml. */
export const defaultBottleGoal = 2000;

/** The unit an amount is shown in. */
export type DisplayUnit = 'ml' | 'L' | 'fl oz' | 'm' | 'km' | 'mi' | 'min' | 'pages';

/** An amount converted for display. */
export interface DisplayAmount {
  /** The rounded number shown, for plural rules. */
  value: number;
  /** `value` formatted for the locale. */
  number: string;
  unit: DisplayUnit;
  /** `number` and its unit symbol ("1.25 L", "34 fl oz", "30 min"); null for pages, whose word needs translating. */
  text: string | null;
}

/** The unit a portion is typed in for a unit system: fl oz and mi for imperial volume and distance, else canonical. */
export type InputUnit = 'ml' | 'fl oz' | 'm' | 'mi' | 'min' | 'pages';

function shown(
  value: number,
  unit: DisplayUnit,
  locale: string,
  minDigits: number,
  maxDigits: number,
): DisplayAmount {
  const number = new Intl.NumberFormat(locale, {
    minimumFractionDigits: minDigits,
    maximumFractionDigits: maxDigits,
  }).format(value);
  return { value, number, unit, text: unit === 'pages' ? null : `${number} ${unit}` };
}

function rounded(value: number, digits: number): number {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}

/**
 * Shows an amount in its canonical unit (a portion, or a total converted once from count × portion) in the unit
 * system. Metric: ml below 1000, else L with up to two decimals; m below 1000 rounded to 10 m, else km with one
 * decimal. Imperial: whole fl oz; mi with two decimals below 1 mi, else one. Minutes and pages are the same in both.
 */
export function formatAmount(
  amount: number,
  unit: PortionUnit,
  units: UnitSystem,
  locale: string,
): DisplayAmount {
  if (unit === 'ml') {
    if (units === 'imperial')
      return shown(Math.round(amount / mlPerFluidOunce), 'fl oz', locale, 0, 0);
    if (amount < 1000) return shown(Math.round(amount), 'ml', locale, 0, 0);
    return shown(rounded(amount / 1000, 2), 'L', locale, 0, 2);
  }
  if (unit === 'm') {
    if (units === 'imperial') {
      const miles = amount / metersPerMile;
      const hundredths = rounded(miles, 2);
      return hundredths < 1
        ? shown(hundredths, 'mi', locale, 2, 2)
        : shown(rounded(miles, 1), 'mi', locale, 1, 1);
    }
    const meters = Math.round(amount / 10) * 10;
    if (meters < 1000) return shown(meters, 'm', locale, 0, 0);
    return shown(rounded(amount / 1000, 1), 'km', locale, 1, 1);
  }
  return shown(amount, unit, locale, 0, 0);
}

/** The total done today (`count` × portion) for display, or null for a habit without a portion (pills, kindling, plain). */
export function progressTotal(
  count: number,
  habit: Pick<Habit, 'portionAmount' | 'portionUnit'>,
  units: UnitSystem,
  locale: string,
): DisplayAmount | null {
  if (habit.portionAmount == null || habit.portionUnit == null) return null;
  return formatAmount(count * habit.portionAmount, habit.portionUnit, units, locale);
}

/** The unit a portion of `unit` is typed in. */
export function inputUnit(unit: PortionUnit, units: UnitSystem): InputUnit {
  if (units === 'imperial' && unit === 'ml') return 'fl oz';
  if (units === 'imperial' && unit === 'm') return 'mi';
  return unit;
}

/** A typed portion (in {@link inputUnit}) as the whole canonical amount that is stored. */
export function toCanonical(typed: number, unit: PortionUnit, units: UnitSystem): number {
  if (units === 'imperial' && unit === 'ml') return Math.round(typed * mlPerFluidOunce);
  if (units === 'imperial' && unit === 'm') return Math.round(typed * metersPerMile);
  return Math.round(typed);
}

/** A stored portion in its {@link inputUnit}, to prefill a form: fl oz to one decimal, mi to two. */
export function toTyped(amount: number, unit: PortionUnit, units: UnitSystem): number {
  if (units === 'imperial' && unit === 'ml') return rounded(amount / mlPerFluidOunce, 1);
  if (units === 'imperial' && unit === 'm') return rounded(amount / metersPerMile, 2);
  return amount;
}

/** The taps a daily goal needs at a portion. */
export interface GoalTaps {
  /** `ceil(goal / portion)`, at least 1 and at most the times-per-day limit. */
  timesPerDay: number;
  /** The goal needs more taps than the limit allows; `timesPerDay` is the limit. */
  capped: boolean;
  /** What `timesPerDay` taps really add up to, in the canonical unit; differs from the goal when rounding changed it. */
  total: number;
  /** The smallest portion that reaches the goal within the limit. */
  minPortion: number;
}

/** Turns a goal and a positive portion (both in the same canonical unit) into the habit's `timesPerDay`. */
export function goalTaps(goal: number, portion: number): GoalTaps {
  const needed = Math.max(1, Math.ceil(goal / portion));
  const timesPerDay = Math.min(needed, limits.timesPerDay);
  return {
    timesPerDay,
    capped: needed > limits.timesPerDay,
    total: timesPerDay * portion,
    minPortion: Math.ceil(goal / limits.timesPerDay),
  };
}
