import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, expect, it } from 'vitest';
import type { Habit } from '@/lib/models';
import type { NotTodayReason } from '@/lib/today-plan';
import { habit } from '@/test/test-support';
import { HabitRow } from './habit-row';

function show(overrides: Partial<Habit>, showStreaks = true, checkInPaused = false, notToday?: NotTodayReason) {
  const subject: Habit = { ...habit, ...overrides };
  const { container } = render(
    <MemoryRouter>
      <HabitRow
        habit={subject}
        busy={false}
        reward={null}
        onToggle={() => {}}
        onRewardEnd={() => {}}
        showStreaks={showStreaks}
        checkInPaused={checkInPaused}
        notToday={notToday}
      />
    </MemoryRouter>,
  );
  return container;
}

const checkButton = () => screen.queryByRole('button', { name: 'Check in Walk for today' });

describe('HabitRow meta line', () => {
  it('shows the streak of a daily habit', () => {
    const row = show({ stats: { ...habit.stats, streak: 4 } });

    expect(row).toHaveTextContent('4-day streak');
    expect(checkButton()).not.toBeNull();
  });

  it('shows the cadence instead of the streak when streaks are off', () => {
    const row = show({ stats: { ...habit.stats, streak: 4 } }, false);

    expect(row).toHaveTextContent('Every day');
    expect(row).not.toHaveTextContent('streak');
  });

  it('says "Every day" for a daily habit without a streak', () => {
    const row = show({});

    expect(row).toHaveTextContent('Every day');
    expect(row).not.toHaveTextContent('streak');
  });

  it('shows this week\'s progress of a weekly habit', () => {
    const row = show({
      frequency: 'weekly',
      weeklyTarget: 3,
      stats: { ...habit.stats, streak: null, week: { done: 2, target: 3 } },
    });

    expect(row).toHaveTextContent('2 of 3 this week');
  });

  it('counts a weekly habit from zero before anything is done this week', () => {
    const row = show({
      frequency: 'weekly',
      weeklyTarget: 3,
      stats: { ...habit.stats, streak: null, week: { done: 0, target: 3 } },
    });

    expect(row).toHaveTextContent('0 of 3 this week');
    expect(row).not.toHaveTextContent('3 times a week');
  });

  it('says the pause end and offers no check-in for a paused habit', () => {
    const row = show({
      pausedToday: true,
      pausedUntil: '2026-12-15',
      today: { ...habit.today, state: 'paused' },
    });

    expect(row.textContent).toMatch(/Paused until (15 December|December 15)/);
    expect(checkButton()).toBeNull();
  });

  it('under Not for today, offers the check-in for a paused habit and keeps saying it is paused', () => {
    const row = show(
      { pausedToday: true, pausedUntil: '2026-12-15', today: { ...habit.today, state: 'paused' } },
      true,
      true,
      'paused',
    );

    expect(row.textContent).toMatch(/Paused until (15 December|December 15)/);
    expect(checkButton()).toHaveAttribute('aria-pressed', 'false');
  });

  it('says when a habit has not started and offers no check-in', () => {
    const row = show({ startDate: '2026-12-01', today: { ...habit.today, state: 'beforestart' } });

    expect(row.textContent).toMatch(/Starts (1 December|December 1)/);
    expect(checkButton()).toBeNull();
  });

  it('shows the low-day line on a low day and still allows checking in', () => {
    const row = show({ today: { ...habit.today, state: 'lowday' } });

    expect(row).toHaveTextContent('Low day, resting. Checking in is still welcome.');
    expect(checkButton()).not.toBeNull();
  });
});

describe('HabitRow picture', () => {
  const water: Habit = { ...habit, name: 'Water', timesPerDay: 3, picture: 'kindling', today: { ...habit.today, count: 1 } };

  function showPicture(subject: Habit, checkInPaused = false) {
    render(
      <MemoryRouter>
        <HabitRow
          habit={subject}
          busy={false}
          reward={null}
          onToggle={() => {}}
          onRewardEnd={() => {}}
          onSetCount={() => {}}
          showStreaks
          checkInPaused={checkInPaused}
        />
      </MemoryRouter>,
    );
  }

  it('shows the picture next to the check button', () => {
    showPicture(water);

    expect(screen.getByRole('button', { name: 'Water, 1 of 3 logs. Add a log to the fire.' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Check in Water for today, 1/3 done' })).toBeInTheDocument();
  });

  it('shows no picture for a paused habit, even where it can still be checked in', () => {
    showPicture({ ...water, pausedToday: true, today: { ...water.today, state: 'paused' } }, true);

    expect(screen.getByRole('button', { name: 'Check in Water for today, 1/3 done' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /logs/ })).toBeNull();
  });
});

describe('HabitRow under Not for today', () => {
  const restDays = { frequency: 'weekdays' as const, days: ['mon' as const] };
  const restLine = 'Rest day, nothing planned. Checking in is still welcome.';
  const checkedInLine = 'Rest day, and you checked in anyway.';

  it.each([
    ['open', { state: 'offday', completed: null, count: 0 }, 1, restLine, 'false'],
    ['on a low day', { state: 'lowday', completed: null, count: 0 }, 1, restLine, 'false'],
    ['checked in', { state: 'done', completed: true, count: 1 }, 1, checkedInLine, 'true'],
    ['partly checked in', { state: 'partial', completed: null, count: 1 }, 2, checkedInLine, 'false'],
  ] as const)('says a rest day is a rest day when it is %s', (_, today, timesPerDay, line, pressed) => {
    const row = show({ ...restDays, timesPerDay, today: { ...habit.today, ...today } }, true, true, 'restDay');

    expect(row).toHaveTextContent(line);
    expect(row).not.toHaveTextContent(/Low day|Done today|of 2 today/);
    expect(screen.getByRole('button', { name: /^Check in Walk for today/ })).toHaveAttribute('aria-pressed', pressed);
  });

  it('says "Done for this week" for a weekly habit met before today, even after another check-in today', () => {
    const row = show(
      {
        frequency: 'weekly',
        weeklyTarget: 2,
        today: { ...habit.today, state: 'done', completed: true, count: 1 },
        stats: { ...habit.stats, week: { done: 3, target: 2 } },
      },
      true,
      true,
      'doneForWeek',
    );

    expect(row).toHaveTextContent('Done for this week');
    expect(row).not.toHaveTextContent('Done today');
  });

  it('keeps the day-state line for a For today row in the same state', () => {
    expect(show({ today: { ...habit.today, state: 'done', completed: true, count: 1 } })).toHaveTextContent('Done today');
  });
});
