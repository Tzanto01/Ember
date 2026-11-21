import { describe, expect, it } from 'vitest';
import fixture from '../../../../docs/contracts/fixtures/letgo-v1.json';
import { habitView, letGoDays } from './rules';
import type { SlipReason } from '../models';
import type { LocalData } from './types';

type FixtureCase = (typeof fixture.cases)[number];

function toLocalData(c: FixtureCase): LocalData {
  return {
    profile: {
      userId: 'u',
      timezone: c.timezone,
      graceDaysPerWeek: 0,
      theme: null,
      createdAt: '2026-01-01T00:00:00Z',
      lastBackupAt: null,
      backupReminderSnoozedUntil: null,
      nextIds: { habit: 2, routine: 1, mood: 1 },
    },
    habits: [
      {
        kind: 'letGo',
        id: 1,
        name: 'Let go',
        frequency: 'daily',
        weeklyTarget: null,
        days: null,
        timesPerDay: 1,
        reminderTime: null,
        reminderChannels: [],
        pausedFrom: c.habit.pausedFrom,
        pausedUntil: c.habit.pausedUntil,
        createdAt: `${c.habit.startDate}T12:00:00Z`,
        routineId: null,
        routinePosition: null,
      },
    ],
    logs: [],
    slips: c.habit.slips.map((s) => ({ habitId: 1, date: s.date, count: s.count, reasons: s.reasons as SlipReason[] })),
    madeIt: c.habit.madeIt.map((date) => ({ habitId: 1, date })),
    lowDays: [],
    routines: [],
    moods: [],
    notes: [],
  };
}

describe('client let-go rules match the shared let-go fixture', () => {
  it('has all 9 cases', () => {
    expect(fixture.cases).toHaveLength(9);
  });

  it.each(fixture.cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
    const data = toLocalData(c);
    const view = habitView(data, 1, c.today);

    expect(letGoDays(data, 1, c.from, c.today)).toEqual(c.expected.days);
    expect(view.stats).toEqual(c.expected.stats);
    expect(view.today).toEqual(c.expected.today);
    expect(view.kind).toBe('letGo');
  });
});
