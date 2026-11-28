import type { Habit, TimeOfDay } from '../models';

/** A count as the client may send it: a whole number from 0 to `timesPerDay`. */
export function clampCount(count: number, timesPerDay: number): number {
  if (!Number.isFinite(count)) return 0;
  return Math.min(Math.max(Math.trunc(count), 0), Math.max(timesPerDay, 0));
}

/** The count after tapping the picture once, or null when it is already full (the tap then only says so). */
export function nextCount(count: number, timesPerDay: number): number | null {
  const current = clampCount(count, timesPerDay);
  return current >= timesPerDay ? null : current + 1;
}

/** The bottle hold choices (+1, +2, +3 portions) that still fit what's left today. */
export function holdChoices(count: number, timesPerDay: number): number[] {
  const left = timesPerDay - clampCount(count, timesPerDay);
  return [1, 2, 3].filter((taps) => taps <= left);
}

/** The label source of one pill box slot: a reminder time, or its 1-based ordinal to translate. */
export type PillSlotLabel =
  { kind: 'time'; time: TimeOfDay } | { kind: 'ordinal'; position: number };

/** One pill box slot. */
export interface PillSlot {
  /** 0-based. */
  index: number;
  /** Checked in; shown pressed. */
  taken: boolean;
  label: PillSlotLabel;
}

/**
 * The pill box slots of a habit at `count`: `timesPerDay` slots, labelled with its reminder times when there is one
 * per slot, else with ordinals.
 */
export function pillSlots(
  habit: Pick<Habit, 'timesPerDay' | 'reminderTimes'>,
  count: number,
): PillSlot[] {
  const times = habit.reminderTimes ?? [];
  const taken = clampCount(count, habit.timesPerDay);
  return Array.from({ length: habit.timesPerDay }, (_, index) => ({
    index,
    taken: index < taken,
    label:
      times.length === habit.timesPerDay
        ? { kind: 'time', time: times[index] }
        : { kind: 'ordinal', position: index + 1 },
  }));
}

/**
 * The count after tapping pill slot `index`: an open slot checks in once (the next open slot fills), a taken slot
 * undoes back to it (`count = index`).
 */
export function slotTapCount(index: number, count: number, timesPerDay: number): number {
  const current = clampCount(count, timesPerDay);
  return index < current ? clampCount(index, timesPerDay) : Math.min(current + 1, timesPerDay);
}
