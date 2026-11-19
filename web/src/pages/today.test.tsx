import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AnyHabit, Habit, ReminderDestination, Routine, WeeklyStats } from '@/lib/models';
import { auth, backup, deviceSync, localMode } from '@/lib/services';
import { type FetchMock, habit, letGoHabit, me, mockFetch } from '@/lib/test-support';
import TodayPage from './today';

vi.mock('@/lib/services', async (original) => {
  const { Store } = await import('@/lib/store');
  return {
    ...(await original<typeof import('@/lib/services')>()),
    localMode: { mode: new Store('account'), evicted: new Store(false) },
    backup: {
      backupReminderDue: vi.fn(async () => false),
      snoozeBackupReminder: vi.fn(async () => {}),
      exportBackup: vi.fn(async () => ({})),
      markBackedUp: vi.fn(async () => {}),
    },
  };
});
vi.mock('@/lib/native/save-file', () => ({ saveExport: vi.fn(async () => true) }));

const weekly: WeeklyStats = {
  totalDone: 0,
  totalPossible: 0,
  headline: 'A calm week.',
  footer: '',
  habits: [],
};

const doneHabit: Habit = { ...habit, today: { ...habit.today, state: 'done', completed: true } };

function renderToday(entry = '/today') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter([{ path: '/today', Component: TodayPage }], { initialEntries: [entry] });
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

async function answerLoad(
  f: FetchMock,
  habits: AnyHabit[],
  {
    localToday = '2026-11-01',
    stats = weekly,
    routines = [],
  }: { localToday?: string; stats?: WeeklyStats; routines?: Routine[] } = {},
) {
  (await f.next('GET', '/api/me')).respond(200, { ...me, localToday });
  (await f.next('GET', '/api/habits')).respond(200, habits);
  (await f.next('GET', `/api/low-days?from=${localToday}&to=${localToday}`)).respond(200, []);
  (await f.next('GET', '/api/stats/weekly')).respond(200, stats);
  (await f.next('GET', '/api/routines')).respond(200, routines);
}

const checkButton = () => screen.findByRole('button', { name: 'Check in Walk for today' });

describe('TodayPage', () => {
  let f: FetchMock;

  beforeEach(() => {
    f = mockFetch();
    auth.setMe(me);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
    document.documentElement.style.removeProperty('--ember-reward');
    localMode.mode.set('account');
  });

  it('keeps both ember summaries and the weekly card in calm mode', async () => {
    renderToday();
    await answerLoad(f, [habit]);
    await checkButton();

    const summaries = screen.getAllByText('Nothing checked in yet today');
    expect(summaries.length).toBeGreaterThan(0);
    for (const summary of summaries) expect(summary.closest('[data-calm-hide]')).toBeNull();
  });

  it('looks up low days for the local day reloaded from the server, not the stale cached one', async () => {
    renderToday();

    const meCall = await f.next('GET', '/api/me');
    expect(f.pending.some((c) => c.url.startsWith('/api/low-days'))).toBe(false);
    meCall.respond(200, { ...me, localToday: '2026-11-02' });

    (await f.next('GET', '/api/low-days?from=2026-11-02&to=2026-11-02')).respond(200, []);
    (await f.next('GET', '/api/habits')).respond(200, []);
    (await f.next('GET', '/api/stats/weekly')).respond(200, weekly);
    (await f.next('GET', '/api/routines')).respond(200, []);

    expect(await screen.findByText(/2 November|November 2/)).toBeInTheDocument();
    expect(f.all.some((c) => c.url.includes('2026-11-01'))).toBe(false);
  });

  it('makes no second /api/me request on first load when me is current', async () => {
    const localToday = new Intl.DateTimeFormat('en-CA', { timeZone: me.timezone }).format(new Date());
    auth.setMe({ ...me, localToday });
    renderToday();

    (await f.next('GET', '/api/habits')).respond(200, [habit]);
    (await f.next('GET', `/api/low-days?from=${localToday}&to=${localToday}`)).respond(200, []);
    (await f.next('GET', '/api/stats/weekly')).respond(200, weekly);
    (await f.next('GET', '/api/routines')).respond(200, []);

    await checkButton();
    expect(f.all.filter((c) => c.url === '/api/me')).toHaveLength(0);
  });

  it('checks in, presses the button and announces it', async () => {
    renderToday();
    await answerLoad(f, [habit]);
    const button = await checkButton();
    expect(button).toHaveAttribute('aria-pressed', 'false');

    await userEvent.click(button);
    const put = await f.next('PUT', '/api/habits/1/days/2026-11-01');
    expect(put.body).toEqual({ completed: true });
    expect(button).toHaveAttribute('aria-pressed', 'true');

    put.respond(200, doneHabit);
    expect(await screen.findByText('Walk: checked in for today.')).toBeInTheDocument();
    expect(await checkButton()).toHaveAttribute('aria-pressed', 'true');
  });

  it('rolls a failed check-in back and says it did not save', async () => {
    renderToday();
    await answerLoad(f, [habit]);
    await userEvent.click(await checkButton());

    (await f.next('PUT', '/api/habits/1/days/2026-11-01')).respond(500, { title: 'Server error' });

    expect(await screen.findByRole('alert')).toHaveTextContent("Your check-in for Walk didn't save");
    expect(await checkButton()).toHaveAttribute('aria-pressed', 'false');
    expect(screen.queryByText('Walk: checked in for today.')).toBeNull();
  });

  it('keeps a newer state when a stale check-in failure arrives after it', async () => {
    renderToday();
    await answerLoad(f, [habit]);
    await userEvent.click(await checkButton());
    const put = await f.next('PUT', '/api/habits/1/days/2026-11-01');

    act(() => deviceSync.synced.update((n) => n + 1));
    await answerLoad(f, [doneHabit], { stats: { ...weekly, totalPossible: 7, headline: 'Synced week.' } });
    expect(await screen.findByText('Synced week.')).toBeInTheDocument();

    put.respond(500, { title: 'Server error' });

    expect(await screen.findByRole('alert')).toHaveTextContent("Your check-in for Walk didn't save");
    expect(await checkButton()).toHaveAttribute('aria-pressed', 'true');
  });

  it('shows a check-in made from a reminder or the widget', async () => {
    renderToday();
    await answerLoad(f, [habit]);
    expect(await checkButton()).toHaveAttribute('aria-pressed', 'false');

    act(() => deviceSync.nativeCheckIn.set({ name: 'Walk' }));
    await answerLoad(f, [doneHabit]);

    await waitFor(async () => expect(await checkButton()).toHaveAttribute('aria-pressed', 'true'));
    act(() => deviceSync.nativeCheckIn.set(null));
  });

  it("plays the theme's reward only after the user's own tap, never on first render", async () => {
    document.documentElement.style.setProperty('--ember-reward', 'glow');
    const read = { ...habit, id: 2, name: 'Read', today: { ...doneHabit.today } };
    renderToday();
    await answerLoad(f, [habit, read]);
    const button = await checkButton();
    const alreadyDone = screen.getByRole('button', { name: 'Check in Read for today' });

    expect(button.className).not.toMatch(/ember-reward-/);
    expect(alreadyDone.className).not.toMatch(/ember-reward-/);

    await userEvent.click(button);
    await waitFor(() => expect(button.className).toMatch(/ember-reward-glow/));
    expect(alreadyDone.className).not.toMatch(/ember-reward-/);

    (await f.next('PUT', '/api/habits/1/days/2026-11-01')).respond(200, doneHabit);
    const settled = await checkButton();
    fireEvent(settled, new Event('webkitAnimationEnd', { bubbles: true }));
    await waitFor(() => expect(settled.className).not.toMatch(/ember-reward-/));
  });

  it('marks today as a low day for the local day and shows the low-day notice', async () => {
    renderToday();
    await answerLoad(f, [habit]);

    await userEvent.click(await screen.findByRole('button', { name: 'Mark today as a low day' }));
    (await f.next('PUT', '/api/low-days/2026-11-01')).respond(204);

    expect(await screen.findByText('Today is a low day')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Mark today as a low day' })).toBeNull();
  });

  it('offers the low day below the habit list, not above it', async () => {
    renderToday();
    await answerLoad(f, [habit]);

    const mark = await screen.findByRole('button', { name: 'Mark today as a low day' });
    expect(screen.getByRole('list').compareDocumentPosition(mark) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('counts a habit at 1 of 2 as checked in today', async () => {
    const water = { ...habit, name: 'Water', timesPerDay: 2, today: { ...habit.today, state: 'partial' as const, count: 1 } };
    renderToday();
    await answerLoad(f, [water]);

    expect((await screen.findAllByText('1 checked in today')).length).toBeGreaterThan(0);
    expect(screen.queryByText('Nothing checked in yet today')).toBeNull();
  });

  it('scrolls to and rings the habit a reminder tap opened, then drops it from the URL', async () => {
    const scrolled = vi.fn<(this: Element, options?: ScrollIntoViewOptions) => void>(function (this: Element) {});
    Element.prototype.scrollIntoView = scrolled;
    const router = renderToday('/today?habit=2');
    await answerLoad(f, [habit, { ...habit, id: 2, name: 'Read' }]);

    await waitFor(() => expect(router.state.location.search).toBe(''));
    const row = screen.getByRole('heading', { name: 'Read' }).closest('li')!;
    expect(scrolled).toHaveBeenCalledOnce();
    expect(scrolled.mock.contexts[0]).toBe(row);
    expect(scrolled.mock.calls[0][0]).toMatchObject({ block: 'center' });
    expect(row.firstElementChild!.className).toMatch(/ring-primary/);
    await waitFor(() => expect(row.firstElementChild!.className).not.toMatch(/ring-primary/), { timeout: 2500 });
  });

  it('clears the low day with a DELETE and brings the mark button back', async () => {
    renderToday();
    (await f.next('GET', '/api/me')).respond(200, me);
    (await f.next('GET', '/api/habits')).respond(200, [habit]);
    (await f.next('GET', '/api/low-days?from=2026-11-01&to=2026-11-01')).respond(200, ['2026-11-01']);
    (await f.next('GET', '/api/stats/weekly')).respond(200, weekly);
    (await f.next('GET', '/api/routines')).respond(200, []);

    await userEvent.click(await screen.findByRole('button', { name: 'Clear low day' }));
    (await f.next('DELETE', '/api/low-days/2026-11-01')).respond(204);

    expect(await screen.findByRole('button', { name: 'Mark today as a low day' })).toBeInTheDocument();
    expect(screen.queryByText('Today is a low day')).toBeNull();
  });

  it('shows the synced list after a check-in that interrupted a sync refetch', async () => {
    renderToday();
    await answerLoad(f, [habit]);
    const button = await checkButton();

    act(() => deviceSync.synced.update((n) => n + 1));
    await vi.waitFor(() => expect(f.pending.some((c) => c.url === '/api/me')).toBe(true));
    await userEvent.click(button);
    (await f.next('GET', '/api/me')).respond(200, { ...me, localToday: '2026-11-01' });
    (await f.next('PUT', '/api/habits/1/days/2026-11-01')).respond(200, doneHabit);

    const read = { ...habit, id: 2, name: 'Read' };
    await vi.waitFor(
      () => {
        for (const call of [...f.pending]) {
          if (call.url === '/api/habits') call.respond(200, [doneHabit, read]);
          else if (call.url.startsWith('/api/low-days')) call.respond(200, []);
          else if (call.url === '/api/stats/weekly') call.respond(200, weekly);
          else if (call.url === '/api/routines') call.respond(200, []);
          else if (call.url === '/api/me') call.respond(200, { ...me, localToday: '2026-11-01' });
        }
        expect(screen.getByRole('heading', { name: 'Read' })).toBeInTheDocument();
      },
      { timeout: 2000, interval: 20 },
    );
  });
  it('adds one to a multi-count habit with an absolute count and shows the new progress', async () => {
    const water = { ...habit, name: 'Water', timesPerDay: 3, today: { ...habit.today, state: 'partial' as const, count: 1 } };
    renderToday();
    await answerLoad(f, [water]);

    await userEvent.click(await screen.findByRole('button', { name: 'Check in Water for today, 1 of 3' }));
    const put = await f.next('PUT', '/api/habits/1/days/2026-11-01');
    expect(put.body).toEqual({ count: 2 });
    expect(screen.getByRole('button', { name: 'Check in Water for today, 2 of 3' })).toHaveAttribute('aria-pressed', 'false');

    put.respond(200, { ...water, today: { ...water.today, count: 2 } });
    expect(await screen.findByText('Water: 2 of 3 for today.')).toBeInTheDocument();
    expect(screen.getByText('2 of 3 today')).toBeInTheDocument();
  });

  it('shows a rest day as rest, not as open, and returns to rest when its check-in is cleared', async () => {
    const restDone = {
      ...habit,
      frequency: 'weekdays' as const,
      days: ['mon' as const],
      today: { ...habit.today, state: 'done' as const, completed: true, count: 1 },
    };
    renderToday();
    await answerLoad(f, [restDone]);

    await userEvent.click(await checkButton());
    await f.next('DELETE', '/api/habits/1/days/2026-11-01');

    expect(screen.getByText(/Rest day, nothing planned/)).toBeInTheDocument();
    expect(await checkButton()).toHaveAttribute('aria-pressed', 'false');
  });

  it("groups habits under their routines in the routine's order, then the rest", async () => {
    const water = { ...habit, id: 3, name: 'Water' };
    const read = { ...habit, id: 2, name: 'Read' };
    renderToday();
    await answerLoad(f, [habit, read, water], {
      routines: [{ id: 5, name: 'Morning', reminderTime: null, reminderChannels: [], habitIds: [3, 1] }],
    });

    await screen.findByRole('heading', { name: 'Morning' });
    const headings = screen.getAllByRole('heading').map((h) => `${h.tagName} ${h.textContent}`);
    expect(headings.slice(1)).toEqual(['H2 Morning', 'H3 Water', 'H3 Walk', 'H2 Other habits', 'H3 Read', 'H2 How was today?']);
    expect(screen.getByRole('link', { name: 'Edit Morning routine' })).toHaveAttribute('href', '/routines/5/edit');
  });

  it('shows habits ungrouped when routines fail to load', async () => {
    renderToday();
    (await f.next('GET', '/api/me')).respond(200, me);
    (await f.next('GET', '/api/habits')).respond(200, [habit]);
    (await f.next('GET', '/api/low-days?from=2026-11-01&to=2026-11-01')).respond(200, []);
    (await f.next('GET', '/api/stats/weekly')).respond(200, weekly);
    (await f.next('GET', '/api/routines')).respond(500);

    expect(await screen.findByRole('heading', { level: 2, name: 'Walk' })).toBeInTheDocument();
  });

  it('without an account, shows the backup reminder when one is due and "Not now" snoozes it', async () => {
    localMode.mode.set('local');
    vi.mocked(backup.backupReminderDue).mockResolvedValue(true);
    const user = userEvent.setup();
    renderToday();
    await answerLoad(f, [habit]);

    const reminder = await screen.findByText(/It's been a while since your last backup/);
    expect(screen.getByRole('button', { name: 'Export backup' })).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Not now' }));

    expect(backup.snoozeBackupReminder).toHaveBeenCalledOnce();
    await waitFor(() => expect(reminder).not.toBeInTheDocument());
  });

  it('without an account, exporting from the reminder records the backup once it was saved', async () => {
    localMode.mode.set('local');
    vi.mocked(backup.backupReminderDue).mockResolvedValue(true);
    renderToday();
    await answerLoad(f, [habit]);

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Export backup' }));

    expect(await screen.findByText('Backup saved.')).toBeInTheDocument();
    expect(backup.markBackedUp).toHaveBeenCalledOnce();
    expect(screen.queryByText(/It's been a while since your last backup/)).toBeNull();
  });

  it('moves focus to "Clear low day" after marking, and back to the mark button after clearing', async () => {
    renderToday();
    await answerLoad(f, [habit]);

    await userEvent.click(await screen.findByRole('button', { name: 'Mark today as a low day' }));
    (await f.next('PUT', '/api/low-days/2026-11-01')).respond(204);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Clear low day' })).toHaveFocus());
    (await f.next('GET', '/api/habits')).respond(200, [habit]);

    await userEvent.click(screen.getByRole('button', { name: 'Clear low day' }));
    (await f.next('DELETE', '/api/low-days/2026-11-01')).respond(204);
    await waitFor(() => expect(screen.getByRole('button', { name: 'Mark today as a low day' })).toHaveFocus());
  });

  it('moves focus to the check button when the last check-in is taken off', async () => {
    const water = { ...habit, name: 'Water', timesPerDay: 3, today: { ...habit.today, state: 'partial' as const, count: 1 } };
    renderToday();
    await answerLoad(f, [water]);

    await userEvent.click(await screen.findByRole('button', { name: 'Take one check-in off Water' }));

    await waitFor(() => expect(screen.getByRole('button', { name: 'Check in Water for today, 0 of 3' })).toHaveFocus());
  });

  it('says so when every habit is done for today', async () => {
    renderToday();
    await answerLoad(f, [habit, { ...doneHabit, id: 2, name: 'Read' }]);
    await checkButton();
    expect(screen.queryByText(/That's everything for today/)).toBeNull();

    await userEvent.click(await checkButton());
    (await f.next('PUT', '/api/habits/1/days/2026-11-01')).respond(200, doneHabit);

    expect((await screen.findAllByText(/That's everything for today/)).length).toBeGreaterThan(0);
  });

  it('titles the weekly card "Last 7 days", keeps it in calm mode and gives the count without a percentage', async () => {
    renderToday();
    await answerLoad(f, [habit], {
      stats: { totalDone: 5, totalPossible: 7, headline: 'This week you completed 5/7 check-ins (71%).', footer: 'Keep it up.', habits: [] },
    });

    expect((await screen.findByRole('heading', { name: 'Last 7 days' })).closest('[data-calm-hide]')).toBeNull();
    expect(screen.getByText('5 check-ins in the last 7 days. Every one of those counts.')).toBeInTheDocument();
    expect(screen.queryByText(/71%/)).toBeNull();
    expect(screen.queryByText('Keep it up.')).toBeNull();
  });

  it('hides the weekly card while there is nothing to summarise or the oldest habit is under 7 days old', async () => {
    const stats = { totalDone: 0, totalPossible: 5, headline: "It's been a quiet week.", footer: '', habits: [] };
    renderToday();
    await answerLoad(f, [{ ...habit, startDate: '2026-10-28' }], { stats });

    await checkButton();
    expect(screen.queryByRole('heading', { name: 'Last 7 days' })).toBeNull();
    expect(screen.queryByText("It's been a quiet week.")).toBeNull();
  });

  const discordUnanswered: ReminderDestination = {
    id: 'discord',
    kind: 'discord',
    label: 'Discord DMs',
    enabled: false,
    current: false,
  };
  const browserOff: ReminderDestination = {
    id: 'browser-3',
    kind: 'browser',
    label: 'Firefox on Linux',
    enabled: false,
    current: true,
  };

  it('asks to turn on reminders when a habit has a reminder time and no reminder destination is on', async () => {
    renderToday();
    await answerLoad(f, [{ ...habit, reminderTime: '08:00' }]);
    (await f.next('GET', '/api/me/reminder-destinations')).respond(200, [discordUnanswered, browserOff]);

    expect(await screen.findByText("Your reminders can't reach you yet")).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Turn on reminders in Settings' })).toHaveAttribute('href', '/settings#discord');
  });

  it('counts Discord with paused DM reminders as not reaching the user', async () => {
    renderToday();
    (await f.next('GET', '/api/me')).respond(200, {
      ...me,
      localToday: '2026-11-01',
      discord: { ...me.discord, linked: true },
      dmReminders: { paused: true },
    });
    (await f.next('GET', '/api/habits')).respond(200, [{ ...habit, reminderTime: '08:00' }]);
    (await f.next('GET', '/api/low-days?from=2026-11-01&to=2026-11-01')).respond(200, []);
    (await f.next('GET', '/api/stats/weekly')).respond(200, weekly);
    (await f.next('GET', '/api/routines')).respond(200, []);
    (await f.next('GET', '/api/me/reminder-destinations')).respond(200, [{ ...discordUnanswered, enabled: true }]);

    expect(await screen.findByText("Your reminders can't reach you yet")).toBeInTheDocument();
  });

  it('shows no reminder warning once any reminder destination is on', async () => {
    renderToday();
    await answerLoad(f, [{ ...habit, reminderTime: '08:00' }]);
    (await f.next('GET', '/api/me/reminder-destinations')).respond(200, [discordUnanswered, { ...browserOff, enabled: true }]);

    await checkButton();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByText("Your reminders can't reach you yet")).toBeNull();
  });

  it('says yesterday is still open when nothing was checked in on a planned day', async () => {
    renderToday();
    await answerLoad(f, [habit]);

    (await f.next('GET', '/api/stats/days?from=2026-10-31&to=2026-10-31')).respond(200, [
      { date: '2026-10-31', scheduled: 1, done: 0, partial: 0, showedUp: false },
    ]);
    (await f.next('GET', '/api/low-days?from=2026-10-31&to=2026-10-31')).respond(200, []);

    expect(await screen.findByText(/Yesterday is still open/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Open History' })).toHaveAttribute('href', '/history');
  });

  it("doesn't say yesterday is open when it was a low day", async () => {
    renderToday();
    await answerLoad(f, [habit]);

    (await f.next('GET', '/api/stats/days?from=2026-10-31&to=2026-10-31')).respond(200, [
      { date: '2026-10-31', scheduled: 1, done: 0, partial: 0, showedUp: false },
    ]);
    (await f.next('GET', '/api/low-days?from=2026-10-31&to=2026-10-31')).respond(200, ['2026-10-31']);

    await checkButton();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByText(/Yesterday is still open/)).toBeNull();
  });

  it("doesn't ask about yesterday when only a weekly habit or a rest day was on it", async () => {
    renderToday();
    await answerLoad(f, [
      { ...habit, frequency: 'weekly', weeklyTarget: 3 },
      { ...habit, id: 2, name: 'Read', frequency: 'weekdays', days: ['mon', 'tue', 'wed', 'thu', 'fri'] },
    ]);

    await screen.findByRole('button', { name: 'Check in Read for today' });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(f.all.some((c) => c.url.startsWith('/api/stats/days'))).toBe(false);
  });

  it("doesn't ask about yesterday when the only daily habit was paused then", async () => {
    renderToday();
    await answerLoad(f, [{ ...habit, pausedFrom: '2026-10-30', pausedUntil: '2026-10-31' }]);

    await checkButton();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(f.all.some((c) => c.url.startsWith('/api/stats/days'))).toBe(false);
  });

  it('says everything is done while a weekly habit is still open today', async () => {
    renderToday();
    await answerLoad(f, [doneHabit, { ...habit, id: 2, name: 'Swim', frequency: 'weekly', weeklyTarget: 2 }]);

    expect((await screen.findAllByText(/That's everything for today/)).length).toBeGreaterThan(0);
  });
});

describe('TodayPage let-go habits', () => {
  let f: FetchMock;

  beforeEach(() => {
    f = mockFetch();
    auth.setMe(me);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
    localMode.mode.set('account');
  });

  it('lists a let-go habit in its own section without touching the build counts', async () => {
    renderToday();
    await answerLoad(f, [doneHabit, letGoHabit]);

    const section = (await screen.findByRole('heading', { name: 'Letting go' })).closest('section')!;
    expect(within(section).getByText('Coffee')).toBeInTheDocument();
    expect(within(section).getByText('4 days without')).toBeInTheDocument();
    expect(within(section).getByRole('button', { name: 'I slipped: Coffee' })).toBeInTheDocument();
    expect(section).not.toContainElement(screen.getByRole('button', { name: /Walk/ }));
    expect((await screen.findAllByText(/^1 checked in today/)).length).toBeGreaterThan(0);
    expect(screen.queryByText('2 checked in today')).toBeNull();
    expect(screen.getAllByText(/That's everything for today\./).length).toBeGreaterThan(0);
  });

  it('shows the let-go section for an account with only let-go habits, not the empty state', async () => {
    renderToday();
    await answerLoad(f, [letGoHabit]);

    expect(await screen.findByText('4 days without')).toBeInTheDocument();
    expect(screen.queryByText('Ready when you are.')).toBeNull();
  });

  it('saves a slip with the picked reason, names the longest stretch and undoes it', async () => {
    renderToday();
    await answerLoad(f, [letGoHabit]);
    await userEvent.click(await screen.findByRole('button', { name: 'I slipped: Coffee' }));

    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Tired' }));
    expect(within(dialog).getByRole('button', { name: 'Tired' })).toHaveAttribute('aria-pressed', 'true');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    const post = await f.next('POST', '/api/habits/9/slips');
    expect(post.body).toEqual({ date: '2026-11-01', reason: 'tired' });
    post.respond(200, {
      ...letGoHabit,
      today: { ...letGoHabit.today, state: 'slipped', slips: 1 },
      stats: { ...letGoHabit.stats, daysFree: 0 },
    });

    const line = await within(dialog).findByText('Noted. Your longest stretch so far is 12 days.');
    expect(line).toBeInTheDocument();
    expect(dialog.textContent ?? '').not.toMatch(/fail|broke|lost|streak/i);
    expect(await screen.findByText('Starting again, gently')).toBeInTheDocument();

    await userEvent.click(within(dialog).getByRole('button', { name: 'Undo' }));
    const del = await f.next('DELETE', '/api/habits/9/slips/2026-11-01');
    del.respond(200, letGoHabit);
    expect(await within(dialog).findByText('Taken back. Today has no slips noted.')).toBeInTheDocument();
    expect(await screen.findByText('4 days without')).toBeInTheDocument();
  });

  it('saves a slip without a reason as just the date', async () => {
    renderToday();
    await answerLoad(f, [letGoHabit]);
    await userEvent.click(await screen.findByRole('button', { name: 'I slipped: Coffee' }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save' }));

    expect((await f.next('POST', '/api/habits/9/slips')).body).toEqual({ date: '2026-11-01' });
  });

  it('shows the reason form again with no reason picked when the sheet is reopened after a save, and saves a second slip', async () => {
    renderToday();
    await answerLoad(f, [letGoHabit]);
    const slipped = { ...letGoHabit, today: { ...letGoHabit.today, state: 'slipped' as const, slips: 1 } };
    await userEvent.click(await screen.findByRole('button', { name: 'I slipped: Coffee' }));
    let dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Tired' }));
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    (await f.next('POST', '/api/habits/9/slips')).respond(200, slipped);
    await within(dialog).findByText(/^Noted\./);
    expect(within(dialog).getAllByRole('button', { name: 'Close' })).toHaveLength(1);
    await userEvent.click(within(dialog).getByRole('button', { name: 'Done' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    await userEvent.click(screen.getByRole('button', { name: 'I slipped: Coffee' }));
    dialog = await screen.findByRole('dialog');
    expect(within(dialog).queryByText(/^Noted\./)).toBeNull();
    expect(within(dialog).getByRole('button', { name: 'Tired' })).toHaveAttribute('aria-pressed', 'false');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect((await f.next('POST', '/api/habits/9/slips')).body).toEqual({ date: '2026-11-01' });
  });

  it('offers no Undo when the saved slip is not the only one that day', async () => {
    renderToday();
    await answerLoad(f, [letGoHabit]);
    await userEvent.click(await screen.findByRole('button', { name: 'I slipped: Coffee' }));
    const dialog = await screen.findByRole('dialog');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    (await f.next('POST', '/api/habits/9/slips')).respond(200, {
      ...letGoHabit,
      today: { ...letGoHabit.today, state: 'slipped', slips: 2 },
    });

    await within(dialog).findByText(/^Noted\./);
    expect(within(dialog).queryByRole('button', { name: 'Undo' })).toBeNull();
  });

  it('offers no slip button while the let-go habit is paused', async () => {
    renderToday();
    await answerLoad(f, [{ ...letGoHabit, pausedUntil: '2026-11-10', today: { ...letGoHabit.today, state: 'paused' } }]);

    expect(await screen.findByText('Coffee')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'I slipped: Coffee' })).toBeNull();
  });
});
