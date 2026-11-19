import { isOffday } from './calendar';
import { startOfWeek } from './local-date';
import {
  type AnyHabit,
  buildHabits,
  type Habit,
  isLetGo,
  type LetGoHabit,
  type LocalDateString,
  reminderTimesOf,
  type Routine,
  type TimeOfDay,
} from './models';

/** The Today order key of a habit outside a routine (let-go habits included). */
export const habitKey = (id: number): string => `h:${id}`;

/** The Today order key of a routine. */
export const routineKey = (id: number): string => `r:${id}`;

/** A top-level For today item: a habit outside a routine, or a routine with its members that are for today. */
export type TodayItem =
  | { kind: 'habit'; key: string; habit: Habit }
  | { kind: 'routine'; key: string; routine: Routine; habits: Habit[] };

/** A Not for today row; `doneForWeek` when a weekly habit met its target before this day. */
export interface NotTodayItem {
  key: string;
  habit: Habit;
  doneForWeek: boolean;
}

/** Today's three sections, each in its final order. */
export interface TodayPlan {
  today: TodayItem[];
  letGo: LetGoHabit[];
  notToday: NotTodayItem[];
}

type Place = 'today' | 'notToday' | 'doneForWeek';

function isPaused(habit: Habit, date: LocalDateString): boolean {
  if (habit.today.date === date && habit.pausedToday) return true;
  return (
    habit.pausedUntil !== null &&
    date <= habit.pausedUntil &&
    (habit.pausedFrom === null || date >= habit.pausedFrom)
  );
}

/**
 * A weekly habit's check-in days this week before `date`: `week.done` without today's own check-in (`count > 0`)
 * on its day, all of it later in the same Monday-to-Sunday week, nothing in a new week.
 */
function weekDoneBefore(habit: Habit, date: LocalDateString): number {
  const done = habit.stats.week?.done ?? 0;
  if (habit.today.date === date) return done - (habit.today.count > 0 ? 1 : 0);
  return startOfWeek(date) === startOfWeek(habit.today.date) ? done : 0;
}

/**
 * Where a build habit goes on `date`, from its schedule alone (never its day state, so a check-in or a low day
 * never moves it). Before its start it stays with today's habits on its own today and is not for any other day.
 */
function placeOf(habit: Habit, date: LocalDateString): Place {
  if (date < habit.startDate) return habit.today.date === date ? 'today' : 'notToday';
  if (isPaused(habit, date) || isOffday(habit, date)) return 'notToday';
  const week = habit.stats.week;
  if (habit.frequency === 'weekly' && week && weekDoneBefore(habit, date) >= week.target)
    return 'doneForWeek';
  return 'today';
}

interface Sortable {
  key: string;
  id: number;
  name: string;
  time: TimeOfDay | null;
}

const byName = new Intl.Collator(undefined, { sensitivity: 'base' });

function automatic(a: Sortable, b: Sortable): number {
  if (a.time !== b.time) {
    if (a.time === null) return 1;
    if (b.time === null) return -1;
    return a.time < b.time ? -1 : 1;
  }
  return byName.compare(a.name, b.name) || a.id - b.id || a.key.localeCompare(b.key);
}

/**
 * Orders one section's items: by reminder time, items without one after those, then by name and id; with a saved
 * order, its keys first in that order, and each missing item (in automatic order) before the first placed item with a
 * later time, or at the end when it has no time.
 */
export function orderItems<T extends Sortable>(
  items: readonly T[],
  saved: readonly string[] | null,
): T[] {
  const sorted = [...items].sort(automatic);
  if (!saved) return sorted;
  const rank = new Map(saved.map((key, index) => [key, index] as const));
  const placed = sorted
    .filter((item) => rank.has(item.key))
    .sort((a, b) => rank.get(a.key)! - rank.get(b.key)!);
  for (const item of sorted) {
    if (rank.has(item.key)) continue;
    const later =
      item.time === null ? -1 : placed.findIndex((p) => p.time !== null && p.time > item.time!);
    placed.splice(later === -1 ? placed.length : later, 0, item);
  }
  return placed;
}

const habitSort = (habit: AnyHabit) => ({
  key: habitKey(habit.id),
  id: habit.id,
  name: habit.name,
  time: reminderTimesOf(habit)[0] ?? null,
});

/**
 * Today's sections for `date` (contract C4): For today (habits outside a routine and routines with any member for
 * today, listing those members in routine order), Let go, and Not for today (paused, a rest day of a weekdays habit,
 * or a weekly target met before today; routine members one by one). `order` is the account's saved order or null.
 */
export function planToday(
  habits: readonly AnyHabit[],
  routines: readonly Routine[] | null,
  order: readonly string[] | null,
  date: LocalDateString,
): TodayPlan {
  const build = buildHabits(habits);
  const byId = new Map(build.map((h) => [h.id, h]));
  const placed = new Set<number>();
  const today: (TodayItem & Sortable)[] = [];
  const notToday: (NotTodayItem & Sortable)[] = [];
  const later = (habit: Habit, place: Place) =>
    notToday.push({ ...habitSort(habit), habit, doneForWeek: place === 'doneForWeek' });
  for (const routine of routines ?? []) {
    const members: Habit[] = [];
    for (const id of routine.habitIds) {
      const habit = byId.get(id);
      if (!habit || placed.has(id)) continue;
      placed.add(id);
      const place = placeOf(habit, date);
      if (place === 'today') members.push(habit);
      else later(habit, place);
    }
    if (members.length) {
      today.push({
        kind: 'routine',
        key: routineKey(routine.id),
        id: routine.id,
        name: routine.name,
        time: routine.reminderTime,
        routine,
        habits: members,
      });
    }
  }
  for (const habit of build) {
    if (placed.has(habit.id)) continue;
    const place = placeOf(habit, date);
    if (place === 'today') today.push({ kind: 'habit', ...habitSort(habit), habit });
    else later(habit, place);
  }
  const letGo = orderItems(
    habits.filter(isLetGo).map((habit) => ({ ...habitSort(habit), habit })),
    order,
  );
  return {
    today: orderItems(today, order),
    letGo: letGo.map((item) => item.habit),
    notToday: orderItems(notToday, order).map(({ key, habit, doneForWeek }) => ({
      key,
      habit,
      doneForWeek,
    })),
  };
}

/** The widget's habits for `date`: For today's build habits in the final order, routines flattened, done ones kept, none before its start. */
export function widgetDay(
  habits: readonly AnyHabit[],
  routines: readonly Routine[] | null,
  order: readonly string[] | null,
  date: LocalDateString,
): Habit[] {
  return planToday(habits, routines, order, date)
    .today.flatMap((item) => (item.kind === 'routine' ? item.habits : [item.habit]))
    .filter((habit) => date >= habit.startDate);
}

/** `keys` with `key` moved `by` places (clamped to the list); unchanged when `key` isn't in it. */
export function moveKey<T>(keys: readonly T[], key: T, by: number): T[] {
  const from = keys.indexOf(key);
  if (from < 0) return [...keys];
  const to = Math.max(0, Math.min(keys.length - 1, from + by));
  const next = keys.filter((k) => k !== key);
  next.splice(to, 0, key);
  return next;
}

/**
 * The full account order to save after one section was rearranged: the saved order (or none), with every displayed
 * key it lacks added where it shows, and the rearranged section's keys rewritten in their slots as `next`. Keys of
 * other sections and keys not on screen keep their places.
 */
export function orderAfterMove(
  saved: readonly string[] | null,
  displayed: readonly (readonly string[])[],
  section: number,
  next: readonly string[],
): string[] {
  const full = [...(saved ?? [])];
  for (const keys of displayed) {
    let cursor = -1;
    keys.forEach((key, i) => {
      const at = full.indexOf(key);
      if (at >= 0) {
        cursor = at;
        return;
      }
      const ahead = keys
        .slice(i + 1)
        .map((k) => full.indexOf(k))
        .find((index) => index >= 0);
      const insert = cursor >= 0 ? cursor + 1 : (ahead ?? full.length);
      full.splice(insert, 0, key);
      cursor = insert;
    });
  }
  const moved = new Set(displayed[section]);
  let n = 0;
  return full.map((key) => (moved.has(key) ? next[n++]! : key));
}

/** A routine's full `habitIds` after its shown members were rearranged to `next`; hidden members keep their slots. */
export function routineOrderAfterMove(
  habitIds: readonly number[],
  next: readonly number[],
): number[] {
  const shown = new Set(next);
  let n = 0;
  return habitIds.map((id) => (shown.has(id) ? next[n++]! : id));
}
