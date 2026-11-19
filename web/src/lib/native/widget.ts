import { type PluginListenerHandle, registerPlugin } from '@capacitor/core';
import type { Language } from '../i18n';
import { type AnyHabit, buildHabits, type LocalDateString } from '../models';
import { scheduledOn } from './reminder-plan';

/** A habit's state on the widget: `rest` is a low day, never "missed". */
export type WidgetHabitState = 'done' | 'partial' | 'open' | 'rest';

export interface WidgetHabit {
  id: number;
  name: string;
  state: WidgetHabitState;
  count: number;
  timesPerDay: number;
}

/** What the "Today in Ember" widget shows: today's scheduled habits (at most {@link widgetRows}) and how many more. */
export interface WidgetSnapshot {
  userId: string;
  date: LocalDateString;
  /** The account's zone, so the widget can tell when the snapshot is from an earlier day. */
  timeZone: string;
  habits: WidgetHabit[];
  more: number;
  /** Tomorrow from the schedule alone, shown after local midnight until the app pushes a fresh snapshot. */
  next?: Pick<WidgetSnapshot, 'date' | 'habits' | 'more'>;
}

/** The custom native plugin (`EmberWidget`, Android only). */
export interface EmberWidgetPlugin {
  update(snapshot: WidgetSnapshot): Promise<void>;
  clear(): Promise<void>;
  /**
   * A tapped widget row (`habitId`, `date`, `userId`, validated natively) or `{}` for "open Today". With `open: true`
   * (a done or rest row) it only opens Today at that habit; without it the app checks the habit in.
   */
  addListener(event: 'widgetTap', listener: (tap: unknown) => void): Promise<PluginListenerHandle>;
  /** A moment door was used while the app was running (warm start). */
  addListener(event: 'openMoment', listener: () => void): Promise<PluginListenerHandle>;
  /** Returns and clears the pending launch action (cold start from a moment door); missing on older native builds. */
  takeLaunchAction?(): Promise<{ action: 'moment' | null }>;
  /** Tells the native side the app's language (i18n-v1); missing on older native builds. */
  setLanguage?(options: { language: Language }): Promise<void>;
  /** Stores this session's widget key and the API origin (absolute `https://`) for check-ins from the widget; missing on older native builds. */
  setKey?(options: { key: string; apiBase: string }): Promise<void>;
  /** True when a widget key is stored; missing on older native builds. */
  hasKey?(): Promise<{ value: boolean }>;
  /** Returns and removes the widget check-ins not yet saved, each with the absolute count its tap aimed for; missing on older native builds. */
  takePending?(): Promise<{ checkIns: Array<{ userId: string; habitId: number; date: LocalDateString; count: number }> }>;
}

/** Rows the widget shows before "+N more". */
export const widgetRows = 4;

const EmberWidget = registerPlugin<EmberWidgetPlugin>('EmberWidget');

/** Loads the plugin wrapped in an object (a Capacitor proxy must never be awaited itself). */
export const capacitorWidget = async (): Promise<{ EmberWidget: EmberWidgetPlugin }> => ({ EmberWidget });

const hidden = new Set(['offday', 'beforestart', 'paused']);

/**
 * The snapshot for `date`: habits scheduled that day in list order (from `habit.today` when it is for `date`,
 * else from the schedule, as open); let-go habits are left out. Names only go to the app-private widget, never to a notification.
 */
export function widgetSnapshot(
  userId: string,
  timeZone: string,
  date: LocalDateString,
  habits: AnyHabit[],
): WidgetSnapshot {
  const shown: WidgetHabit[] = [];
  for (const habit of buildHabits(habits)) {
    const timesPerDay = habit.timesPerDay ?? 1;
    const fresh = habit.today.date === date;
    if (fresh ? hidden.has(habit.today.state) : !scheduledOn(habit, date)) continue;
    const count = fresh ? habit.today.count : 0;
    const state: WidgetHabitState = !fresh
      ? 'open'
      : habit.today.state === 'done' || count >= timesPerDay
        ? 'done'
        : habit.today.state === 'lowday'
          ? 'rest'
          : count > 0
            ? 'partial'
            : 'open';
    shown.push({ id: habit.id, name: habit.name, state, count, timesPerDay });
  }
  return {
    userId,
    date,
    timeZone,
    habits: shown.slice(0, widgetRows),
    more: Math.max(0, shown.length - widgetRows),
  };
}
