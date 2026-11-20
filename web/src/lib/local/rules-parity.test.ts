import { describe, expect, it } from 'vitest';
import fixture from '../../../../docs/contracts/fixtures/rules-v1.json';
import { firesideLimits } from '../fireside-limits';
import { limits } from './limits';
import { dayStats, habitDays, habitView, pause, retarget, weeklySummary, writeDay } from './rules';
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
  routines: { id: number }[];
  habits: FixtureHabit[];
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
      h.logs.map((l) => ({ habitId: h.id, date: l.date, completed: l.completed, count: l.count })),
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

function sortedLogs(logs: LocalLog[]): Omit<LocalLog, 'habitId'>[] {
  return logs
    .filter((l) => l.habitId === editHabitId)
    .map(({ date, count, completed }) => ({ date, count, completed }))
    .sort((a, b) => a.date.localeCompare(b.date));
}

describe('client rules match the Core rules fixture', () => {
  it('has all 26 cases', () => {
    expect(fixture.cases).toHaveLength(26);
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
        const state = habitDays(data, id, c.today, c.today, c.today)[0].state;
        return state === 'pending' || state === 'partial';
      });
      expect(pending).toEqual(
        (c.expected.routinePending as Record<string, number[]>)[String(r.id)],
      );
    }

    expect(weeklySummary(data, c.today)).toEqual(c.expected.weekly);
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
    expect(sortedLogs(writeDay(data, editHabitId, c.date, edit))[0] ?? null).toEqual(c.expected);
  });

  it.each(fixture.edits.retarget.map((c) => [c.name, c] as const))('retarget: %s', (_name, c) => {
    const data = editData({ timesPerDay: c.timesPerDay }, c.logs);
    expect(sortedLogs(retarget(data, editHabitId, c.newTimesPerDay, today))).toEqual(c.expected);
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
