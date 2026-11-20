import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AnyHabit, DayEntry } from '@/lib/models';
import { auth } from '@/lib/services';
import { type FetchMock, habit, letGoHabit, me, mockFetch } from '@/test/test-support';
import HistoryPage from './history';

const localToday = '2026-11-04';

function renderHistory(path = '/history') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      { path: '/history', Component: HistoryPage },
      { path: '/history/:habitId', Component: HistoryPage },
    ],
    { initialEntries: [path] },
  );
  const view = render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return view.container;
}

async function answerHabits(f: FetchMock, habits: AnyHabit[]) {
  (await f.next('GET', '/api/me')).respond(200, { ...me, localToday });
  (await f.next('GET', '/api/habits')).respond(200, habits);
}

async function answerMonth(f: FetchMock, id: number, from: string, to: string, days: DayEntry[] = []) {
  (await f.next('GET', `/api/habits/${id}/days?from=${from}&to=${to}`)).respond(200, days);
  (await f.next('GET', `/api/low-days?from=${from}&to=${to}`)).respond(200, []);
}

const dayButtonIds = (container: HTMLElement) =>
  Array.from(container.querySelectorAll('button[id^="day-"]')).map((b) => b.id);

describe('HistoryPage', () => {
  let f: FetchMock;

  beforeEach(() => {
    f = mockFetch();
    auth.setMe(me);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
  });

  it('makes only the editable days of the shown month day buttons', async () => {
    const container = renderHistory();
    await answerHabits(f, [{ ...habit, startDate: '2026-11-02' }]);
    await answerMonth(f, 1, '2026-11-02', '2026-11-04');

    await waitFor(() =>
      expect(dayButtonIds(container)).toEqual(['day-2026-11-02', 'day-2026-11-03', 'day-2026-11-04']),
    );
    for (const id of dayButtonIds(container)) {
      expect(container.querySelector(`#${id}`)).toHaveAttribute('aria-expanded', 'false');
    }
  });

  it('follows the server\'s local day, not the stale cached one, when choosing the month', async () => {
    auth.setMe({ ...me, localToday: '2026-10-31' });
    renderHistory('/history/1');

    (await f.next('GET', '/api/me')).respond(200, { ...me, localToday: '2026-11-02' });
    (await f.next('GET', '/api/habits')).respond(200, [habit]);

    await answerMonth(f, 1, '2026-11-01', '2026-11-02');
    expect(f.all.some((c) => c.url.includes('to=2026-10-'))).toBe(false);
  });

  it('marks a day done from the day panel and shows it as done', async () => {
    const container = renderHistory();
    await answerHabits(f, [habit]);
    await answerMonth(f, 1, '2026-11-01', '2026-11-04');
    const day = await waitFor(() => {
      const button = container.querySelector<HTMLButtonElement>('#day-2026-11-03');
      expect(button).not.toBeNull();
      return button!;
    });
    expect(day).toHaveAttribute('data-state', 'notdone');

    await userEvent.click(day);
    expect(day).toHaveAttribute('aria-expanded', 'true');
    const panel = container.querySelector<HTMLElement>('#day-panel')!;
    expect(panel.querySelector('#day-panel-heading')).toHaveFocus();

    await userEvent.click(within(panel).getByRole('button', { name: 'Done' }));
    const put = await f.next('PUT', '/api/habits/1/days/2026-11-03');
    expect(put.body).toEqual({ completed: true });
    put.respond(200, habit);
    await answerMonth(f, 1, '2026-11-01', '2026-11-04', [
      { date: '2026-11-03', state: 'done', completed: true, count: 1 },
    ]);

    await waitFor(() => expect(day).toHaveAttribute('data-state', 'done'));
    expect(await screen.findByText(/marked done\.$/)).toBeInTheDocument();
  });

  it("shows the selected habit's note for the open day and edits it in the note sheet", async () => {
    const container = renderHistory();
    await answerHabits(f, [habit]);
    await answerMonth(f, 1, '2026-11-01', '2026-11-04');
    const day = await waitFor(() => container.querySelector<HTMLButtonElement>('#day-2026-11-03')!);

    await userEvent.click(day);
    (await f.next('GET', '/api/habit-notes?from=2026-09-04&to=2026-11-03&habitId=1')).respond(200, [
      { habitId: 1, date: '2026-11-03', text: 'Felt easy, seeing progress.' },
      { habitId: 1, date: '2026-11-01', text: 'Short one, still counts.' },
    ]);

    const card = (await screen.findByRole('heading', { name: 'Note on Walk' })).closest('section')!;
    expect(await within(card).findByText('Felt easy, seeing progress.')).toBeInTheDocument();
    expect(within(card).queryByText('Short one, still counts.')).toBeNull();

    await userEvent.click(within(card).getByRole('button', { name: 'Edit note' }));
    const dialog = await screen.findByRole('dialog', { name: 'Walk' });
    expect(within(dialog).getByRole('textbox', { name: 'A note for this day, just for you' })).toHaveValue(
      'Felt easy, seeing progress.',
    );
  });

  it("pages months only between the habit's start month and the current month", async () => {
    renderHistory('/history/1');
    await answerHabits(f, [{ ...habit, startDate: '2026-10-15' }]);
    await answerMonth(f, 1, '2026-11-01', '2026-11-04');
    await answerMonth(f, 1, '2026-10-15', '2026-11-04');

    const previous = await screen.findByRole('button', { name: 'Previous month' });
    const next = screen.getByRole('button', { name: 'Next month' });
    expect(next).toHaveAttribute('aria-disabled', 'true');
    expect(previous).not.toHaveAttribute('aria-disabled');

    await userEvent.click(next);
    expect(f.pending).toEqual([]);

    await userEvent.click(previous);
    await answerMonth(f, 1, '2026-10-15', '2026-10-31');
    expect(await screen.findByRole('button', { name: 'Previous month' })).toHaveAttribute('aria-disabled', 'true');
    expect(screen.getByRole('button', { name: 'Next month' })).not.toHaveAttribute('aria-disabled');

    await userEvent.click(screen.getByRole('button', { name: 'Previous month' }));
    expect(f.pending).toEqual([]);
  });

  it('closes the open day when the month changes, so coming back does not reopen it', async () => {
    const container = renderHistory('/history/1');
    await answerHabits(f, [{ ...habit, startDate: '2026-10-15' }]);
    await answerMonth(f, 1, '2026-11-01', '2026-11-04');
    await userEvent.click(await screen.findByRole('button', { name: /3 November|November 3/ }));
    expect(container.querySelector('#day-panel')).not.toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Previous month' }));
    await answerMonth(f, 1, '2026-10-15', '2026-10-31');
    await userEvent.click(await screen.findByRole('button', { name: 'Next month' }));
    await answerMonth(f, 1, '2026-11-01', '2026-11-04');

    await waitFor(() => expect(container.querySelector('#day-2026-11-03')).not.toBeNull());
    expect(container.querySelector('#day-2026-11-03')).toHaveAttribute('aria-expanded', 'false');
    expect(container.querySelector('#day-panel')).toBeNull();
  });
  it('names a partly done day "2 of 3 done" and steps its count with an absolute value', async () => {
    const container = renderHistory();
    await answerHabits(f, [{ ...habit, timesPerDay: 3 }]);
    await answerMonth(f, 1, '2026-11-01', '2026-11-04', [
      { date: '2026-11-03', state: 'partial', completed: null, count: 2 },
    ]);

    const day = await screen.findByRole('button', { name: /(3 November|November 3).*, 2 of 3 done$/ });
    await userEvent.click(day);
    const panel = container.querySelector<HTMLElement>('#day-panel')!;
    expect(within(panel).getByText('2 of 3 done')).toBeInTheDocument();

    await userEvent.click(within(panel).getByRole('button', { name: 'One more check-in' }));
    expect((await f.next('PUT', '/api/habits/1/days/2026-11-03')).body).toEqual({ count: 3 });
  });

  it('shows the kept days of the last 7 and 30 days for a daily habit, low days included', async () => {
    renderHistory();
    await answerHabits(f, [habit]);
    await answerMonth(f, 1, '2026-11-01', '2026-11-04');
    (await f.next('GET', '/api/habits/1/days?from=2026-10-06&to=2026-11-04')).respond(200, [
      { date: '2026-10-10', state: 'done', completed: true, count: 1 },
      { date: '2026-11-03', state: 'done', completed: true, count: 1 },
    ]);
    (await f.next('GET', '/api/low-days?from=2026-10-06&to=2026-11-04')).respond(200, ['2026-11-02']);

    expect((await screen.findByText('Last 7 days')).nextSibling).toHaveTextContent('Kept 2 of 7');
    expect(screen.getByText('Last 30 days').nextSibling).toHaveTextContent('Kept 3 of 30');
  });
});

describe('HistoryPage for a let-go habit', () => {
  let f: FetchMock;
  const coffee = { ...letGoHabit, startDate: '2026-10-01' };

  beforeEach(() => {
    f = mockFetch();
    auth.setMe({ ...me, showStreaks: false });
  });

  afterEach(() => {
    clearInterval(refetchTimer);
    vi.unstubAllGlobals();
    auth.clearSession();
  });

  async function answerSlips(slips: { date: string; count: number; reasons: string[] }[] = []) {
    (await f.next('GET', `/api/habits/9/slips?from=2026-11-01&to=2026-11-04`)).respond(200, slips);
  }

  let refetchTimer: ReturnType<typeof setInterval> | undefined;
  function answerRefetches(slips: { date: string; count: number; reasons: string[] }[]) {
    clearInterval(refetchTimer);
    const timer = (refetchTimer = setInterval(() => {
      for (const call of f.pending.filter((c) => c.url.startsWith('/api/habits/9/slips?'))) {
        f.pending.splice(f.pending.indexOf(call), 1);
        call.respond(200, slips);
      }
      for (const call of f.pending.filter((c) => c.url.startsWith('/api/habits/9/made-it?'))) {
        f.pending.splice(f.pending.indexOf(call), 1);
        call.respond(200, []);
      }
    }, 5));
    setTimeout(() => clearInterval(timer), 1500);
  }

  it('shows days free, longest stretch and free days this month even with streaks off, and no check-in requests', async () => {
    renderHistory('/history/9');
    await answerHabits(f, [coffee]);
    await answerSlips();

    expect(await screen.findByText('Days without')).toBeInTheDocument();
    expect(screen.getByText('4 days without')).toBeInTheDocument();
    expect(screen.getByText('Longest stretch').nextSibling).toHaveTextContent('12 days');
    expect(screen.getByText('Days without this month').nextSibling).toHaveTextContent('1 day');
    expect(f.all.some((c) => c.url.includes('/days') || c.url.includes('/low-days'))).toBe(false);
  });

  it('keeps all three tiles on a fresh start with nothing counted yet', async () => {
    renderHistory('/history/9');
    await answerHabits(f, [{ ...coffee, stats: { ...coffee.stats, daysFree: 0, longestStretch: 0, freeDaysThisMonth: 0 } }]);
    await answerSlips();

    expect((await screen.findByText('Days without')).nextSibling).toHaveTextContent('Starting again, gently');
    expect(screen.getByText('Longest stretch').nextSibling).toHaveTextContent('0 days');
    expect(screen.getByText('Days without this month').nextSibling).toHaveTextContent('0 days');
  });

  it('marks days with a slip and lets a past day get a slip added and removed', async () => {
    const container = renderHistory('/history/9');
    await answerHabits(f, [coffee]);
    await answerSlips([{ date: '2026-11-02', count: 1, reasons: ['tired'] }]);

    const slipped = await waitFor(() => {
      const b = container.querySelector<HTMLButtonElement>('#day-2026-11-02');
      expect(b).not.toBeNull();
      return b!;
    });
    await waitFor(() => expect(slipped).toHaveAttribute('data-state', 'slipped'));
    expect(slipped).toHaveAccessibleName(/one slip noted/);
    const free = container.querySelector<HTMLButtonElement>('#day-2026-11-03')!;
    expect(free).toHaveAttribute('data-state', 'free');

    await userEvent.click(free);
    const panel = container.querySelector<HTMLElement>('#day-panel')!;
    expect(within(panel).queryByRole('button', { name: "Remove this day's slips" })).toBeNull();
    await userEvent.click(within(panel).getByRole('button', { name: 'Note a slip on this day' }));
    const post = await f.next('POST', '/api/habits/9/slips');
    expect(post.body).toEqual({ date: '2026-11-03' });
    post.respond(200, coffee);
    answerRefetches([
      { date: '2026-11-02', count: 1, reasons: ['tired'] },
      { date: '2026-11-03', count: 1, reasons: [] },
    ]);
    await waitFor(() => expect(container.querySelector('#day-2026-11-03')).toHaveAttribute('data-state', 'slipped'));

    await userEvent.click(await within(panel).findByRole('button', { name: "Remove this day's slips" }));
    (await f.next('DELETE', '/api/habits/9/slips/2026-11-03')).respond(200, coffee);
    answerRefetches([{ date: '2026-11-02', count: 1, reasons: ['tired'] }]);
    await waitFor(() => expect(container.querySelector('#day-2026-11-03')).toHaveAttribute('data-state', 'free'));
  });
});
