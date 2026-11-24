import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AnyHabit, Habit } from '@/lib/models';
import { auth, habitTimer } from '@/lib/services';
import { type FetchMock, habit, me, mockFetch } from '@/test/test-support';
import { TimerNotice } from './timer-notice';

const key = 'ember.habitTimer';
const now = Date.parse('2026-11-01T11:00:00Z');
const walk: Habit = { ...habit, timerMinutes: 30 };
const doneWalk: Habit = { ...walk, today: { ...walk.today, state: 'done', completed: true, count: 1 } };

describe('TimerNotice', { timeout: 15_000 }, () => {
  let fetch: FetchMock;

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'], now });
    fetch = mockFetch();
    auth.setMe(me);
  });

  afterEach(() => {
    auth.clearSession();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  async function end(habits: AnyHabit[], date = '2026-11-01') {
    localStorage.setItem(
      key,
      JSON.stringify({ userId: me.id, habitId: 1, name: 'Walk', minutes: 30, date, endsAt: now - 1000, remainingMs: null, told: true }),
    );
    habitTimer.check();
    render(<TimerNotice />);
    await answer(habits);
  }

  const answer = async (habits: AnyHabit[]) => (await fetch.next('GET', '/api/habits')).respond(200, habits);
  const mark = () => screen.findByRole('button', { name: 'Mark as done' });
  const writes = () => fetch.all.filter((call) => call.method !== 'GET');

  it('says the time is up, checks nothing in by itself, and checks in once on "Mark as done"', async () => {
    const user = userEvent.setup();
    await end([walk]);
    const button = await mark();
    expect(screen.getAllByText('Your time for Walk is up.').length).toBeGreaterThan(0);
    expect(writes()).toEqual([]);

    await user.click(button);
    await answer([walk]);
    const put = await fetch.next('PUT', '/api/habits/1/days/2026-11-01');
    expect(put.body).toEqual({ completed: true });
    put.respond(200, doneWalk);

    expect(await screen.findByText('Walk: checked in.')).toBeInTheDocument();
    expect(screen.queryByText('Your time for Walk is up.')).toBeNull();
    expect(writes()).toHaveLength(1);
  });

  it('shows no button before the offer is known', async () => {
    localStorage.setItem(
      key,
      JSON.stringify({ userId: me.id, habitId: 1, name: 'Walk', minutes: 30, date: '2026-11-01', endsAt: now, remainingMs: null, told: true }),
    );
    habitTimer.check();
    render(<TimerNotice />);

    expect(screen.queryByRole('button')).toBeNull();
    await answer([walk]);
    expect(await mark()).toBeInTheDocument();
  });

  it('can be dismissed while a slow network keeps the offer open', async () => {
    const user = userEvent.setup();
    localStorage.setItem(
      key,
      JSON.stringify({ userId: me.id, habitId: 1, name: 'Walk', minutes: 30, date: '2026-11-01', endsAt: now, remainingMs: null, told: true }),
    );
    habitTimer.check();
    render(<TimerNotice />);

    const button = await screen.findByRole('button', { name: 'Mark as done' }, { timeout: 3000 });
    await user.click(button);
    expect(writes()).toEqual([]);
    expect(button).toHaveAttribute('aria-disabled', 'true');
    await user.click(screen.getByRole('button', { name: 'Not now' }));

    expect(localStorage.getItem(key)).toBeNull();
  });

  it('"Not now" does nothing while the check-in is being saved', async () => {
    const user = userEvent.setup();
    await end([walk]);
    await user.click(await mark());
    await answer([walk]);
    const put = await fetch.next('PUT', '/api/habits/1/days/2026-11-01');

    await user.click(screen.getByRole('button', { name: 'Not now' }));
    expect(localStorage.getItem(key)).not.toBeNull();

    put.respond(200, doneWalk);
    expect(await screen.findByText('Walk: checked in.')).toBeInTheDocument();
    expect(localStorage.getItem(key)).toBeNull();
  });

  it('only says the time is up when the day is already done', async () => {
    const user = userEvent.setup();
    await end([doneWalk]);

    await user.click(await screen.findByRole('button', { name: 'Okay' }));

    expect(screen.queryByRole('button', { name: /Mark as done/ })).toBeNull();
    expect(screen.queryByText('Your time for Walk is up.')).toBeNull();
    expect(localStorage.getItem(key)).toBeNull();
    expect(writes()).toEqual([]);
  });

  it('names yesterday when the timer started then', async () => {
    await end([walk], '2026-10-31');
    (await fetch.next('GET', '/api/habits/1/days?from=2026-10-31&to=2026-10-31')).respond(200, [
      { date: '2026-10-31', state: 'pending', completed: null, count: 0, skipped: false },
    ]);

    expect(await screen.findByRole('button', { name: 'Mark as done for yesterday' })).toBeInTheDocument();
  });

  it('keeps the notice and says why when the check-in is refused', async () => {
    const user = userEvent.setup();
    await end([walk]);
    await user.click(await mark());
    await answer([walk]);
    (await fetch.next('PUT', '/api/habits/1/days/2026-11-01')).respond(500);

    expect(await screen.findByRole('alert')).toHaveTextContent('Your check-in for Walk');
    expect(await mark()).toBeInTheDocument();
    expect(localStorage.getItem(key)).not.toBeNull();
  });

  it('turns into the plain sentence when the day got done meanwhile', async () => {
    const user = userEvent.setup();
    await end([walk]);
    await user.click(await mark());
    await answer([doneWalk]);

    expect(await screen.findByRole('button', { name: 'Okay' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Mark as done' })).toBeNull();
    expect(writes()).toEqual([]);
  });

  it('asks again when the window regains focus', async () => {
    await end([walk]);
    await mark();

    act(() => void window.dispatchEvent(new Event('focus')));
    await answer([doneWalk]);

    expect(await screen.findByRole('button', { name: 'Okay' })).toBeInTheDocument();
  });

  it('"Not now" leaves nothing of the timer', async () => {
    const user = userEvent.setup();
    await end([walk]);
    await user.click(await screen.findByRole('button', { name: 'Not now' }));

    expect(screen.queryByText('Your time for Walk is up.')).toBeNull();
    expect(localStorage.getItem(key)).toBeNull();
    expect(writes()).toEqual([]);
  });
});
