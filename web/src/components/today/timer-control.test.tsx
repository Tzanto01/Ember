import { act, fireEvent, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { LiveRegion } from '@/components/live-region';
import type { Habit } from '@/lib/models';
import { auth, habitTimer } from '@/lib/services';
import { habit, me } from '@/test/test-support';
import { inGerman } from '@/test/test-utils';
import { HabitRow } from './habit-row';

const walk: Habit = { ...habit, timerMinutes: 30 };
const read: Habit = { ...habit, id: 2, name: 'Read', timerMinutes: 90 };

function Rows({ habits, onToggle }: { habits: Habit[]; onToggle?: () => void }) {
  const [message, setMessage] = useState('');
  return (
    <MemoryRouter>
      <ul>
        {habits.map((h) => (
          <HabitRow
            key={h.id}
            habit={h}
            busy={false}
            reward={null}
            onToggle={() => onToggle?.()}
            onRewardEnd={() => {}}
            showStreaks
            checkInPaused
            onAnnounce={setMessage}
          />
        ))}
      </ul>
      <LiveRegion message={message} />
    </MemoryRouter>
  );
}

const startWalk = () => screen.getByRole('button', { name: 'Start the 30 minute timer for Walk' });

describe("a Today row's timer", () => {
  beforeEach(() => {
    auth.setMe(me);
  });

  afterEach(() => {
    vi.useRealTimers();
    auth.clearSession();
    localStorage.clear();
  });

  it('starts, pauses, resumes and stops, says each once, keeps the focus and leaves no trace', async () => {
    const user = userEvent.setup();
    render(<Rows habits={[walk]} />);

    await user.click(startWalk());
    expect(screen.getByRole('timer', { name: 'Time left for Walk' })).toHaveTextContent('30:00');
    expect(screen.getByText('Walk: timer started.')).toBeInTheDocument();
    expect(screen.getByText("Ember tells you here when it's up.")).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Pause the timer for Walk' }));
    expect(screen.getByText('Walk: timer paused.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Resume the timer for Walk' })).toHaveFocus();

    await user.click(screen.getByRole('button', { name: 'Resume the timer for Walk' }));
    expect(screen.getByText('Walk: timer resumed.')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Stop the timer for Walk' }));
    expect(screen.queryByRole('timer')).toBeNull();
    expect(localStorage.getItem('ember.habitTimer')).toBeNull();
    expect(startWalk()).toHaveFocus();
    expect(document.body.textContent).not.toMatch(/missed|failed|unfinished|cancel/i);
  });

  it('keeps the check button working while the timer runs', async () => {
    const user = userEvent.setup();
    const toggled = vi.fn();
    render(<Rows habits={[walk]} onToggle={toggled} />);
    await user.click(startWalk());

    await user.click(screen.getByRole('button', { name: 'Check in Walk for today' }));

    expect(toggled).toHaveBeenCalledOnce();
    expect(screen.getByRole('timer')).toBeInTheDocument();
  });

  it('counts down each second, with hours from an hour', () => {
    vi.useFakeTimers();
    render(<Rows habits={[read]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Start the 90 minute timer for Read' }));
    expect(screen.getByRole('timer')).toHaveTextContent('1:30:00');

    act(() => {
      vi.advanceTimersByTime(2_000);
    });
    expect(screen.getByRole('timer')).toHaveTextContent('1:29:58');
  });

  it('asks before another timer makes room, and keeps or switches as told', async () => {
    const user = userEvent.setup();
    render(<Rows habits={[walk, read]} />);
    await user.click(startWalk());
    const startRead = () => screen.getByRole('button', { name: 'Start the 90 minute timer for Read' });

    await user.click(startRead());
    let dialog = within(await screen.findByRole('dialog', { name: 'Switch timers?' }));
    await user.click(dialog.getByRole('button', { name: 'Keep Walk' }));
    expect(screen.getByRole('timer', { name: 'Time left for Walk' })).toBeInTheDocument();
    expect(startRead()).toHaveFocus();

    await user.click(startRead());
    dialog = within(await screen.findByRole('dialog', { name: 'Switch timers?' }));
    await user.click(dialog.getByRole('button', { name: 'Start Read' }));
    expect(screen.getByRole('timer', { name: 'Time left for Read' })).toBeInTheDocument();
    expect(screen.getAllByRole('timer')).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Pause the timer for Read' })).toHaveFocus();
  });

  it('offers no start without a length, on a paused habit or before its start', () => {
    render(
      <Rows
        habits={[
          habit,
          { ...read, pausedToday: true, today: { ...habit.today, state: 'paused' } },
          { ...read, id: 3, name: 'Draw', startDate: '2026-12-01', today: { ...habit.today, state: 'beforestart' } },
        ]}
      />,
    );

    expect(screen.queryByRole('button', { name: /^Start the/ })).toBeNull();
  });

  it('still shows a running timer whose habit lost its length', () => {
    habitTimer.start(walk);
    render(<Rows habits={[habit]} />);

    expect(screen.getByRole('button', { name: 'Stop the timer for Walk' })).toBeInTheDocument();
  });

  it('speaks German', async () => {
    await inGerman();
    render(<Rows habits={[walk]} />);
    await userEvent.click(screen.getByRole('button', { name: 'Timer für Walk starten, 30 Min.' }));

    expect(screen.getByRole('timer', { name: 'Verbleibende Zeit für Walk' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Timer für Walk pausieren' })).toBeInTheDocument();
  });
});
