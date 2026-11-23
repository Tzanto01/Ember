import 'fake-indexeddb/auto';
import { IDBFactory } from 'fake-indexeddb';
import { describe, expect, it } from 'vitest';
import fixture from '../../../../docs/contracts/fixtures/rules-v1.json';
import { firesideLimits } from '../fireside-limits';
import { addDays } from '../local-date';
import type { AnyHabit, Me, Routine } from '../models';
import { settled, zonedToUtc } from '../native/reminder-plan';
import { widgetSnapshot } from '../native/widget';
import { LocalBackend } from './backend';
import { limits } from './limits';
import { dayStats, habitDays, habitView, pause, retarget, weeklySummary, writeDay } from './rules';
import { LocalStore } from './store';
import type { LocalData, LocalHabit, LocalLog } from './types';

interface FixtureHabit {
  id: number;
  frequency: string;
  weeklyTarget: number | null;
  days: string[] | null;
  timesPerDay: number;
  startDate: string;
  pausedFrom: string | null;
  pausedUntil: string | null;
  routineId: number | null;
  logs: Omit<LocalLog, 'habitId'>[];
}

interface FixtureUser {
  timezone: string;
  graceDaysPerWeek: number;
  lowDays: string[];
  routines: { id: number; habitIds: number[] }[];
  habits: FixtureHabit[];
}

interface FixtureWidgetHabit extends FixtureHabit {
  name: string;
  kind: string;
  reminderTimes: string[];
}

interface FixtureWidgetCase {
  today: string;
  timezone: string;
  graceDaysPerWeek: number;
  lowDays: string[];
  todayOrder: string[] | null;
  habits: FixtureWidgetHabit[];
  routines: { id: number; name: string; reminderTime: string | null; habitIds: number[] }[];
}

const editsStart = '2027-01-01';
const editHabitId = 1;

function localHabit(h: FixtureHabit): LocalHabit {
  return {
    id: h.id,
    name: `Habit ${h.id}`,
    frequency: h.frequency as LocalHabit['frequency'],
    weeklyTarget: h.weeklyTarget,
    days: h.days as LocalHabit['days'],
    timesPerDay: h.timesPerDay,
    reminderTime: null,
    reminderChannels: [],
    pausedFrom: h.pausedFrom,
    pausedUntil: h.pausedUntil,
    createdAt: `${h.startDate}T12:00:00Z`,
    routineId: h.routineId,
    routinePosition: null,
  };
}

function toLocalData(c: FixtureUser): LocalData {
  return {
    profile: {
      userId: 'u',
      timezone: c.timezone,
      graceDaysPerWeek: c.graceDaysPerWeek,
      theme: null,
      createdAt: '2026-01-01T00:00:00Z',
      lastBackupAt: null,
      backupReminderSnoozedUntil: null,
      nextIds: { habit: 99, routine: 99, mood: 1 },
    },
    habits: c.habits.map(localHabit),
    logs: c.habits.flatMap((h) =>
      h.logs.map((l) => ({ habitId: h.id, date: l.date, completed: l.completed, count: l.count, skipped: l.skipped ?? false })),
    ),
    lowDays: c.lowDays,
    routines: c.routines.map((r) => ({
      id: r.id,
      name: `Routine ${r.id}`,
      reminderTime: null,
      reminderChannels: [],
    })),
    moods: [],
    notes: [],
  };
}

/** One daily habit created on {@link editsStart}, as the C# edit cases set it up. */
function editData(
  h: { timesPerDay: number; pausedFrom?: string | null; pausedUntil?: string | null },
  logs: Omit<LocalLog, 'habitId'>[],
): LocalData {
  const habit: FixtureHabit = {
    id: editHabitId,
    frequency: 'daily',
    weeklyTarget: null,
    days: null,
    timesPerDay: h.timesPerDay,
    startDate: editsStart,
    pausedFrom: h.pausedFrom ?? null,
    pausedUntil: h.pausedUntil ?? null,
    routineId: null,
    logs,
  };
  return toLocalData({
    timezone: 'UTC',
    graceDaysPerWeek: 1,
    lowDays: [],
    routines: [],
    habits: [habit],
  });
}

function logShape({ date, count, completed, skipped }: Omit<LocalLog, 'habitId'>): Omit<LocalLog, 'habitId'> {
  return { date, count, completed, skipped: skipped === true };
}

function sortedLogs(logs: LocalLog[]): Omit<LocalLog, 'habitId'>[] {
  return logs
    .filter((l) => l.habitId === editHabitId)
    .map(logShape)
    .sort((a, b) => a.date.localeCompare(b.date));
}

describe('client rules match the Core rules fixture', () => {
  it('has all 29 cases', () => {
    expect(fixture.cases).toHaveLength(29);
  });

  it.each(fixture.cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
    const data = toLocalData(c);

    for (const h of c.habits) {
      const days = habitDays(data, h.id, c.from, c.today, c.today);
      expect(days).toEqual((c.expected.days as Record<string, unknown>)[String(h.id)]);
      expect(habitView(data, h.id, c.today).stats).toEqual(
        (c.expected.stats as Record<string, unknown>)[String(h.id)],
      );
    }

    expect(dayStats(data, c.from, c.today, c.today)).toEqual(c.expected.dayStats);

    for (const r of c.routines) {
      const pending = r.habitIds.filter((id) => {
        const { state, skipped } = habitDays(data, id, c.today, c.today, c.today)[0];
        return (state === 'pending' || state === 'partial') && !skipped;
      });
      expect(pending).toEqual(
        (c.expected.routinePending as Record<string, number[]>)[String(r.id)],
      );
      const routine = { id: r.id, name: `Routine ${r.id}`, reminderTime: null, reminderChannels: [], habitIds: r.habitIds };
      const views = c.habits.map((h) => habitView(data, h.id, c.today));
      expect(settled(views, [routine], { routineId: r.id, date: c.today })).toBe(
        (c.expected.routineDone as Record<string, boolean>)[String(r.id)],
      );
    }

    expect(weeklySummary(data, c.today)).toEqual(c.expected.weekly);
  });
});

/** Device data for a widget case: the fixture habits with names, kinds, reminder times and routine positions, and its Today order. */
function widgetData(c: FixtureWidgetCase): LocalData {
  const data = toLocalData({ ...c, routines: [] });
  data.habits = c.habits.map((h) => ({
    ...localHabit(h),
    kind: h.kind as LocalHabit['kind'],
    name: h.name,
    reminderTime: h.reminderTimes[0] ?? null,
    reminderTimes: h.reminderTimes,
    routinePosition: c.routines.find((r) => r.id === h.routineId)?.habitIds.indexOf(h.id) ?? null,
  }));
  data.routines = c.routines.map((r) => ({ id: r.id, name: r.name, reminderTime: r.reminderTime, reminderChannels: [] }));
  data.profile.todayOrder = c.todayOrder;
  data.profile.todayOrderRestored = true;
  return data;
}

async function get<T>(backend: LocalBackend, url: string): Promise<T> {
  const response = await backend.handle('GET', url, null);
  expect(response.status).toBe(200);
  return (await response.json()) as T;
}

describe('client widget days match the Core rules fixture', () => {
  it('has all 13 widget cases', () => {
    expect(fixture.widgetDays).toHaveLength(13);
  });

  it.each(fixture.widgetDays.map((c) => [c.name, c] as const))('%s', async (_name, c) => {
    (globalThis as unknown as { indexedDB: unknown }).indexedDB = new IDBFactory();
    const store = new LocalStore();
    await store.replace(widgetData(c));
    const now = new Date(zonedToUtc(c.today, '12:00', c.timezone));
    const backend = new LocalBackend(store, { native: true, now: () => now });
    const me = await get<Me>(backend, '/api/me');
    const habits = await get<AnyHabit[]>(backend, '/api/habits');
    const routines = await get<Routine[]>(backend, '/api/routines');
    expect(me.localToday).toBe(c.today);

    const day = (date: string) => {
      const { userId: _u, timeZone: _z, ...rest } = widgetSnapshot(me.id, me.timezone, date, habits, routines, me.todayOrder);
      return rest;
    };
    expect({ today: day(c.today), next: day(addDays(c.today, 1)) }).toEqual(c.expected);
  });
});

describe('client edits match the Core rules fixture', () => {
  const { today } = fixture.edits;

  it.each(fixture.edits.pause.map((c) => [c.name, c] as const))('pause: %s', (_name, c) => {
    const data = editData(
      { timesPerDay: 1, pausedFrom: c.pausedFrom, pausedUntil: c.pausedUntil },
      [],
    );
    expect(pause(data.habits[0], c.until, today)).toEqual(c.expected);
  });

  it.each(fixture.edits.writeDay.map((c) => [c.name, c] as const))('writeDay: %s', (_name, c) => {
    const data = editData({ timesPerDay: c.timesPerDay }, c.log ? [c.log] : []);
    const { count, completed } = c.edit;
    const edit = count === undefined ? { completed: completed ?? null } : { count };
    expect(sortedLogs(writeDay(data, editHabitId, c.date, edit))[0] ?? null).toEqual(c.expected && logShape(c.expected));
  });

  it.each(fixture.edits.retarget.map((c) => [c.name, c] as const))('retarget: %s', (_name, c) => {
    const data = editData({ timesPerDay: c.timesPerDay }, c.logs);
    expect(sortedLogs(retarget(data, editHabitId, c.newTimesPerDay, today))).toEqual(c.expected.map(logShape));
  });
});

describe('client limits match the Core limits in the fixture', () => {
  it('enforces the same habit, routine, mood, note and order limits', () => {
    expect({ ...limits, rangeDays: undefined }).toEqual(fixture.limits.local);
  });

  it('shows the same Fireside limits', () => {
    expect(firesideLimits).toEqual(fixture.limits.fireside);
  });
});
