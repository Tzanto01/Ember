import { type PluginListenerHandle, registerPlugin } from '@capacitor/core';
import type { Language } from '../i18n';
import type { AnyHabit, LocalDateString, Routine } from '../models';
import type { ColorScheme, ThemeId } from '../theme';
import { widgetDay } from '../today-plan';

/** A habit's state on the widget: `rest` is a low day, never "missed". */
export type WidgetHabitState = 'done' | 'partial' | 'open' | 'rest';

export interface WidgetHabit {
  id: number;
  name: string;
  state: WidgetHabitState;
  count: number;
  timesPerDay: number;
}

/** One day on the widget: its habits (at most {@link widgetRows}), how many more, and `done` of `total` over all of them. */
export interface WidgetDay {
  date: LocalDateString;
  habits: WidgetHabit[];
  more: number;
  done: number;
  total: number;
}

/** What the "Today in Ember" widget shows: today's habits by the Today rules (`today-plan.ts`). */
export interface WidgetSnapshot extends WidgetDay {
  userId: string;
  /** The account's zone, so the widget can tell when the snapshot is from an earlier day. */
  timeZone: string;
  /** Tomorrow from the schedule alone, all open, shown after local midnight until the app pushes a fresh snapshot. */
  next?: WidgetDay;
}

/** The widget's look: the Ember theme, the colour scheme, and whether the flame may flicker. */
export interface WidgetStyle {
  theme: ThemeId;
  /** "Gentle motion" on this device and calm mode off. */
  motion: boolean;
  colorScheme: ColorScheme;
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
  /** Tells the native side the widget's theme, colour scheme and motion; missing on older native builds. */
  setStyle?(options: WidgetStyle): Promise<void>;
  /** Stores this session's widget key and the API origin (absolute `https://`) for check-ins from the widget; missing on older native builds. */
  setKey?(options: { key: string; apiBase: string }): Promise<void>;
  /** True when a widget key is stored; missing on older native builds. */
  hasKey?(): Promise<{ value: boolean }>;
  /** Returns and removes the widget check-ins not yet saved, each with the absolute count its tap aimed for; missing on older native builds. */
  takePending?(): Promise<{ checkIns: Array<{ userId: string; habitId: number; date: LocalDateString; count: number }> }>;
  /** True when the phone has a screen-time overview Ember can open; missing on older native builds. */
  canOpenScreenTime?(): Promise<{ available: boolean }>;
  /** Opens the phone's own screen-time overview; Ember reads nothing from it. Missing on older native builds. */
  openScreenTime?(): Promise<void>;
}

/** Habits a snapshot carries before "+N more" (the most an account can have). */
export const widgetRows = 25;

const EmberWidget = registerPlugin<EmberWidgetPlugin>('EmberWidget');

/** Loads the plugin wrapped in an object (a Capacitor proxy must never be awaited itself). */
export const capacitorWidget = async (): Promise<{ EmberWidget: EmberWidgetPlugin }> => ({ EmberWidget });

/**
 * The snapshot day for `date`: the Today rules' widget day (For today in the account's order, routines flattened, done
 * kept, let-go and Not for today left out), each from `habit.today` when it is for `date`, else open. Names only go
 * to the app-private widget, never to a notification.
 */
export function widgetSnapshot(
  userId: string,
  timeZone: string,
  date: LocalDateString,
  habits: AnyHabit[],
  routines: Routine[] = [],
  order: string[] | null = null,
): WidgetSnapshot {
  const shown = widgetDay(habits, routines, order, date).map((habit): WidgetHabit => {
    const timesPerDay = habit.timesPerDay ?? 1;
    const fresh = habit.today.date === date;
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
    return { id: habit.id, name: habit.name, state, count, timesPerDay };
  });
  return {
    userId,
    date,
    timeZone,
    habits: shown.slice(0, widgetRows),
    more: Math.max(0, shown.length - widgetRows),
    done: shown.filter((habit) => habit.state === 'done').length,
    total: shown.length,
  };
}
