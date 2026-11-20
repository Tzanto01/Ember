import { describe, expect, it } from 'vitest';
import type { Habit } from './models';
import { withDayEdit } from './device-store';
import { habit } from '@/test/test-support';

const weekly = {
  ...habit,
  frequency: 'weekly' as const,
  weeklyTarget: 3,
  stats: { ...habit.stats, week: { done: 2, target: 3 } },
};

const checkedIn = { date: '2026-11-01', state: 'done', completed: true, count: 1 } as const;

describe('withDayEdit', () => {
  it('counts an offline check-in that completes the day into the weekly progress, and takes it back when undone', () => {
    const done = withDayEdit(weekly, { habitId: 1, date: '2026-11-01', completed: true });
    expect(done.stats.week?.done).toBe(3);

    expect(withDayEdit(done, { habitId: 1, date: '2026-11-01', completed: null }).stats.week?.done).toBe(2);
  });

  it('leaves daily habits and edits for other days alone', () => {
    expect(withDayEdit(habit, { habitId: 1, date: '2026-11-01', completed: true }).stats).toBe(habit.stats);
    expect(withDayEdit(weekly, { habitId: 1, date: '2026-10-31', completed: true }).stats.week?.done).toBe(2);
  });

  it('does not count a weekly check-in twice when the server already counted that day under a pause or low day', () => {
    const paused: Habit = {
      ...weekly,
      pausedToday: true,
      today: { date: '2026-11-01', state: 'paused', completed: true, count: 1 },
    };
    const again = withDayEdit(paused, { habitId: 1, date: '2026-11-01', completed: true });
    expect(again.stats.week?.done).toBe(2);

    expect(withDayEdit(paused, { habitId: 1, date: '2026-11-01', completed: null }).stats.week?.done).toBe(1);

    const lowDay: Habit = { ...weekly, today: { date: '2026-11-01', state: 'lowday', completed: true, count: 1 } };
    expect(withDayEdit(lowDay, { habitId: 1, date: '2026-11-01', completed: true }).stats.week?.done).toBe(2);
  });

  it('puts a cleared day back to the rest the server shows: paused, low day, off schedule, else open', () => {
    const clear = { habitId: 1, date: '2026-11-01', completed: null } as const;
    const paused: Habit = { ...habit, pausedToday: true, today: checkedIn };
    const restDay: Habit = { ...habit, frequency: 'weekdays', days: ['mon'], today: checkedIn };

    expect(withDayEdit(paused, clear).today).toEqual({ date: '2026-11-01', state: 'paused', completed: null, count: 0 });
    expect(withDayEdit(restDay, clear).today.state).toBe('offday');
    expect(withDayEdit({ ...habit, today: checkedIn }, clear, true).today.state).toBe('lowday');
    expect(withDayEdit(restDay, clear, true).today.state).toBe('lowday');
    expect(withDayEdit({ ...habit, today: checkedIn }, clear).today.state).toBe('pending');
  });

  it('keeps a multi-count day at rest when its count goes back to zero while paused', () => {
    const paused: Habit = {
      ...habit,
      timesPerDay: 3,
      pausedToday: true,
      today: { date: '2026-11-01', state: 'partial', completed: null, count: 1 },
    };
    expect(withDayEdit(paused, { habitId: 1, date: '2026-11-01', completed: null, count: 0 }).today.state).toBe('paused');
    expect(withDayEdit(paused, { habitId: 1, date: '2026-11-01', completed: null, count: 2 }).today.state).toBe('partial');
  });
});
