import { matchPicture } from './habit-visuals/keywords';
import type { Habit, HabitPicture, LocalDateString, TipId } from './models';
import { tipIds } from './models';
import { Store } from './store';

/** Where a tip shows: above Today's habit list, below it, or under the day-note form (Today and History). */
export type TipPlace = 'top' | 'below' | 'journal';

/** A tip {@link pickTip} can choose: a `TipIds` entry, or the picture offer, whose "seen" lives on each habit. */
export type ShownTip = TipId | 'picture';

export const tipPlaces: Record<ShownTip, TipPlace> = {
  streaks: 'top',
  'private-journal': 'journal',
  routines: 'below',
  android: 'below',
  discord: 'below',
  picture: 'top',
};

/** The habit the picture tip offers a picture to, and that picture. */
export interface PictureOffer {
  habit: Habit;
  /** Kindling is never offered. */
  picture: Exclude<HabitPicture, 'kindling'>;
}

/**
 * The first of `forToday` (Today's For today habits, in Today order) the picture tip may ask about: the account
 * suggests pictures, and the habit is not paused, has no picture, was never asked, and its name matches a picture.
 */
export function pictureOfferFor(forToday: readonly Habit[], suggestPictures: boolean | undefined): PictureOffer | null {
  if (suggestPictures === false) return null;
  for (const habit of forToday) {
    if (habit.picture || habit.pictureOfferAnswered || habit.pausedToday) continue;
    const picture = matchPicture(habit.name);
    if (picture && picture !== 'kindling') return { habit, picture };
  }
  return null;
}

/** What {@link pickTip} decides on. Null counts and flags are still loading. */
export interface TipFacts {
  today: LocalDateString;
  /** The account's first local day as far as this device can tell. */
  firstDay: LocalDateString;
  /** The local day of the last answered or dismissed tip (or of a shown What's new card), or null. */
  waitDay: LocalDateString | null;
  /** The account's seen tips plus this device's marks still waiting to be sent. */
  seen: readonly TipId[];
  whatsNew: boolean;
  calm: boolean;
  native: boolean;
  /** Signed in with an account (not no-account mode). */
  account: boolean;
  showStreaks: boolean;
  plainJournal: boolean;
  discordLinked: boolean;
  habits: number;
  activeHabits: number;
  routines: number | null;
  /** Local days with any check-in. */
  checkInDays: number | null;
  /** Whether the account has a saved day note. */
  hasNote: boolean | null;
  /** A habit on Today the picture tip may ask about ({@link pictureOfferFor}). */
  pictureOffer: boolean;
}

const atLeast = (count: number | null, min: number) => (count === null ? null : count >= min);

/**
 * The one tip to show now, by the contract's priority order with the picture offer last, or null. Returns null while a
 * higher-priority tip's condition is still unknown, so a lower one never flashes first.
 */
export function pickTip(f: TipFacts): ShownTip | null {
  if (f.whatsNew) return null;
  const quiet = f.calm || f.today <= f.firstDay || (f.waitDay !== null && f.today <= f.waitDay);
  const conditions: Record<TipId, () => boolean | null> = {
    streaks: () => f.habits > 0 && !f.showStreaks,
    'private-journal': () => f.account && f.plainJournal && f.hasNote,
    routines: () => f.activeHabits >= 3 && (f.routines === null ? null : f.routines === 0),
    android: () => !f.native && atLeast(f.checkInDays, 3),
    discord: () => f.account && !f.discordLinked && atLeast(f.checkInDays, 7),
  };
  for (const id of tipIds) {
    if (f.seen.includes(id) || (id !== 'streaks' && quiet)) continue;
    const holds = conditions[id]();
    if (holds === null) return null;
    if (holds) return id;
  }
  return f.pictureOffer && !quiet ? 'picture' : null;
}

/** Per user on this device: the first local day seen, the last answered day, and marks not yet sent. */
export interface TipMemory {
  first: LocalDateString | null;
  wait: LocalDateString | null;
  pending: TipId[];
}

/** Bumped on every {@link writeTipMemory}, so every tip slot re-reads. */
export const tipMemoryChanged = new Store(0);

const memoryKey = (userId: string) => `ember.tips.${userId}`;
const day = (value: unknown) => (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value) ? value : null);

/** This device's tip memory for `userId`; empty when nothing is stored or storage fails. */
export function readTipMemory(userId: string): TipMemory {
  try {
    const raw = JSON.parse(localStorage.getItem(memoryKey(userId)) ?? '{}') as Record<string, unknown> | null;
    const list: unknown = raw?.['pending'];
    const pending = Array.isArray(list) ? tipIds.filter((id) => list.includes(id)) : [];
    return { first: day(raw?.['first']), wait: day(raw?.['wait']), pending };
  } catch {
    return { first: null, wait: null, pending: [] };
  }
}

/** Merges `patch` into the tip memory for `userId`; a failing storage is ignored. */
export function writeTipMemory(userId: string, patch: Partial<TipMemory>): void {
  try {
    localStorage.setItem(memoryKey(userId), JSON.stringify({ ...readTipMemory(userId), ...patch }));
  } catch {}
  tipMemoryChanged.update((n) => n + 1);
}
