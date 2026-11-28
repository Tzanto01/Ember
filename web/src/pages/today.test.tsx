import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AnyHabit, Habit, Me, ReminderDestination, Routine, WeeklyStats } from '@/lib/models';
import { findGuideTarget } from '@/lib/guide/highlight';
import { auth, backup, deviceSync, localMode } from '@/lib/services';
import { type FetchMock, habit, letGoHabit, me, mockFetch } from '@/test/test-support';
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

function renderToday(
  entry = '/today',
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } }),
) {
  const router = createMemoryRouter([{ path: '/today', Component: TodayPage }], {
    initialEntries: [entry],
  });
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
    account = {},
  }: { localToday?: string; stats?: WeeklyStats; routines?: Routine[]; account?: Partial<Me> } = {},
) {
  (await f.next('GET', '/api/me')).respond(200, { ...me, localToday, ...account });
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
    const localToday = new Intl.DateTimeFormat('en-CA', { timeZone: me.timezone }).format(
      new Date(),
    );
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

    expect(await screen.findByRole('alert')).toHaveTextContent(
      "Your check-in for Walk didn't save",
    );
    expect(await checkButton()).toHaveAttribute('aria-pressed', 'false');
    expect(screen.queryByText('Walk: checked in for today.')).toBeNull();
  });

  it('keeps a newer state when a stale check-in failure arrives after it', async () => {
    renderToday();
    await answerLoad(f, [habit]);
    await userEvent.click(await checkButton());
    const put = await f.next('PUT', '/api/habits/1/days/2026-11-01');

    act(() => deviceSync.synced.update((n) => n + 1));
    await answerLoad(f, [doneHabit], {
      stats: { ...weekly, totalPossible: 7, headline: 'Synced week.' },
    });
    expect(await screen.findByText('Synced week.')).toBeInTheDocument();

    put.respond(500, { title: 'Server error' });

    expect(await screen.findByRole('alert')).toHaveTextContent(
      "Your check-in for Walk didn't save",
    );
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
    expect(
      screen.getByRole('list').compareDocumentPosition(mark) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  it('counts a habit at 1 of 2 as checked in today', async () => {
    const water = {
      ...habit,
      name: 'Water',
      timesPerDay: 2,
      today: { ...habit.today, state: 'partial' as const, count: 1 },
    };
    renderToday();
    await answerLoad(f, [water]);

    expect((await screen.findAllByText('1 checked in today')).length).toBeGreaterThan(0);
    expect(screen.queryByText('Nothing checked in yet today')).toBeNull();
  });

  it('scrolls to and rings the habit a reminder tap opened, then drops it from the URL', async () => {
    const scrolled = vi.fn<(this: Element, options?: ScrollIntoViewOptions) => void>(function (
      this: Element,
    ) {});
    Element.prototype.scrollIntoView = scrolled;
    const router = renderToday('/today?habit=2');
    await answerLoad(f, [habit, { ...habit, id: 2, name: 'Read' }]);

    await waitFor(() => expect(router.state.location.search).toBe(''));
    const row = screen.getByRole('heading', { name: 'Read' }).closest('li')!;
    expect(scrolled).toHaveBeenCalledOnce();
    expect(scrolled.mock.contexts[0]).toBe(row);
    expect(scrolled.mock.calls[0][0]).toMatchObject({ block: 'center' });
    expect(row.firstElementChild!.className).toMatch(/ring-primary/);
    await waitFor(() => expect(row.firstElementChild!.className).not.toMatch(/ring-primary/), {
      timeout: 2500,
    });
  });

  it('clears the low day with a DELETE and brings the mark button back', async () => {
    renderToday();
    (await f.next('GET', '/api/me')).respond(200, me);
    (await f.next('GET', '/api/habits')).respond(200, [habit]);
    (await f.next('GET', '/api/low-days?from=2026-11-01&to=2026-11-01')).respond(200, [
      '2026-11-01',
    ]);
    (await f.next('GET', '/api/stats/weekly')).respond(200, weekly);
    (await f.next('GET', '/api/routines')).respond(200, []);

    await userEvent.click(await screen.findByRole('button', { name: 'Clear low day' }));
    (await f.next('DELETE', '/api/low-days/2026-11-01')).respond(204);

    expect(
      await screen.findByRole('button', { name: 'Mark today as a low day' }),
    ).toBeInTheDocument();
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
    const water = {
      ...habit,
      name: 'Water',
      timesPerDay: 3,
      today: { ...habit.today, state: 'partial' as const, count: 1 },
    };
    renderToday();
    await answerLoad(f, [water]);

    await userEvent.click(
      await screen.findByRole('button', { name: 'Check in Water for today, 1/3 done' }),
    );
    const put = await f.next('PUT', '/api/habits/1/days/2026-11-01');
    expect(put.body).toEqual({ count: 2 });
    expect(
      screen.getByRole('button', { name: 'Check in Water for today, 2/3 done' }),
    ).toHaveAttribute('aria-pressed', 'false');

    put.respond(200, { ...water, today: { ...water.today, count: 2 } });
    expect(await screen.findByText('Water: 2 of 3 for today.')).toBeInTheDocument();
    expect(screen.getByText('2 of 3 today')).toBeInTheDocument();
  });

  it("checks in once from a tap on the habit's picture, through the same absolute count as the check button", async () => {
    const water: Habit = {
      ...habit,
      name: 'Water',
      timesPerDay: 8,
      picture: 'bottle',
      portionAmount: 250,
      portionUnit: 'ml',
      today: { ...habit.today, state: 'partial', count: 5 },
    };
    renderToday();
    await answerLoad(f, [water]);

    await userEvent.click(
      await screen.findByRole('button', { name: 'Water, 5 of 8 glasses. Add 250 ml.' }),
    );
    const put = await f.next('PUT', '/api/habits/1/days/2026-11-01');
    expect(put.body).toEqual({ count: 6 });
    expect(screen.getByText('6 of 8, 1.5 L')).toBeInTheDocument();
    put.respond(200, { ...water, today: { ...water.today, count: 6 } });

    expect(
      await screen.findByRole('button', { name: 'Check in Water for today, 6/8 done' }),
    ).toBeInTheDocument();
  });

  it('undoes a pill box back to the taken slot that was tapped, and sends nothing for a tap on a full bottle', async () => {
    const tablets: Habit = {
      ...habit,
      name: 'Tablets',
      timesPerDay: 3,
      picture: 'pills',
      today: { ...habit.today, state: 'partial', count: 2 },
    };
    const full: Habit = {
      ...habit,
      id: 2,
      name: 'Water',
      timesPerDay: 2,
      picture: 'bottle',
      portionAmount: 250,
      portionUnit: 'ml',
      today: { ...habit.today, state: 'done', completed: true, count: 2 },
    };
    renderToday();
    await answerLoad(f, [tablets, full]);

    await userEvent.click(
      await screen.findByRole('button', { name: 'Water, 2 of 2 glasses. All done for today.' }),
    );
    await userEvent.click(screen.getByRole('button', { name: 'Tablets, 1st' }));

    const put = await f.next('PUT', '/api/habits/1/days/2026-11-01');
    expect(put.body).toEqual({ count: 0 });
    expect(f.all.filter((call) => call.method === 'PUT')).toHaveLength(1);
  });

  it('says a checked-in rest day is still a rest day, and returns to the plain rest line when its check-in is cleared', async () => {
    const restDone = {
      ...habit,
      frequency: 'weekdays' as const,
      days: ['mon' as const],
      today: { ...habit.today, state: 'done' as const, completed: true, count: 1 },
    };
    renderToday();
    await answerLoad(f, [restDone]);

    await userEvent.click(await screen.findByRole('button', { name: 'Not for today · 1' }));
    expect(screen.getByText(/Rest day, and you checked in anyway/)).toBeInTheDocument();
    expect(screen.queryByText('Done today')).toBeNull();
    await userEvent.click(await checkButton());
    await f.next('DELETE', '/api/habits/1/days/2026-11-01');

    expect(screen.getByText(/Rest day, nothing planned/)).toBeInTheDocument();
    expect(await checkButton()).toHaveAttribute('aria-pressed', 'false');
  });

  it('shows each routine in For today as one closed row that opens to its habits in routine order', async () => {
    const water = { ...habit, id: 3, name: 'Water' };
    const read = { ...habit, id: 2, name: 'Read' };
    renderToday();
    await answerLoad(f, [habit, read, water], {
      routines: [
        { id: 5, name: 'Morning', reminderTime: null, reminderChannels: [], habitIds: [3, 1] },
      ],
    });

    await screen.findByRole('heading', { name: 'Morning' });
    const headings = () =>
      screen
        .getAllByRole('heading')
        .map((h) => `${h.tagName} ${h.textContent}`)
        .slice(1);
    expect(headings()).toEqual(['H2 For today', 'H3 Morning', 'H3 Read', 'H2 How was today?']);
    await userEvent.click(screen.getByRole('button', { name: 'Morning' }));
    expect(headings()).toEqual([
      'H2 For today',
      'H3 Morning',
      'H4 Water',
      'H4 Walk',
      'H3 Read',
      'H2 How was today?',
    ]);
    expect(screen.getByRole('link', { name: 'Edit Morning routine' })).toHaveAttribute(
      'href',
      '/routines/5/edit',
    );
  });

  it('shows habits ungrouped when routines fail to load', async () => {
    renderToday();
    (await f.next('GET', '/api/me')).respond(200, me);
    (await f.next('GET', '/api/habits')).respond(200, [habit]);
    (await f.next('GET', '/api/low-days?from=2026-11-01&to=2026-11-01')).respond(200, []);
    (await f.next('GET', '/api/stats/weekly')).respond(200, weekly);
    (await f.next('GET', '/api/routines')).respond(500);

    expect(await screen.findByRole('heading', { level: 3, name: 'Walk' })).toBeInTheDocument();
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
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Clear low day' })).toHaveFocus(),
    );
    (await f.next('GET', '/api/habits')).respond(200, [habit]);

    await userEvent.click(screen.getByRole('button', { name: 'Clear low day' }));
    (await f.next('DELETE', '/api/low-days/2026-11-01')).respond(204);
    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Mark today as a low day' })).toHaveFocus(),
    );
  });

  it('moves focus to the check button when the last check-in is taken off', async () => {
    const water = {
      ...habit,
      name: 'Water',
      timesPerDay: 3,
      today: { ...habit.today, state: 'partial' as const, count: 1 },
    };
    renderToday();
    await answerLoad(f, [water]);

    await userEvent.click(
      await screen.findByRole('button', { name: 'Take one check-in off Water' }),
    );

    await waitFor(() =>
      expect(
        screen.getByRole('button', { name: 'Check in Water for today, 0/3 done' }),
      ).toHaveFocus(),
    );
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
      stats: {
        totalDone: 5,
        totalPossible: 7,
        headline: 'This week you completed 5/7 check-ins (71%).',
        footer: 'Keep it up.',
        habits: [],
      },
    });

    expect(
      (await screen.findByRole('heading', { name: 'Last 7 days' })).closest('[data-calm-hide]'),
    ).toBeNull();
    expect(
      screen.getByText('5 check-ins in the last 7 days. Every one of those counts.'),
    ).toBeInTheDocument();
    expect(screen.queryByText(/71%/)).toBeNull();
    expect(screen.queryByText('Keep it up.')).toBeNull();
  });

  it('hides the weekly card while there is nothing to summarise or the oldest habit is under 7 days old', async () => {
    const stats = {
      totalDone: 0,
      totalPossible: 5,
      headline: "It's been a quiet week.",
      footer: '',
      habits: [],
    };
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
    (await f.next('GET', '/api/me/reminder-destinations')).respond(200, [
      discordUnanswered,
      browserOff,
    ]);

    expect(await screen.findByText("Your reminders can't reach you yet")).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Turn on reminders in Settings' })).toHaveAttribute(
      'href',
      '/settings#discord',
    );
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
    (await f.next('GET', '/api/me/reminder-destinations')).respond(200, [
      { ...discordUnanswered, enabled: true },
    ]);

    expect(await screen.findByText("Your reminders can't reach you yet")).toBeInTheDocument();
  });

  it('shows no reminder warning once any reminder destination is on', async () => {
    renderToday();
    await answerLoad(f, [{ ...habit, reminderTime: '08:00' }]);
    (await f.next('GET', '/api/me/reminder-destinations')).respond(200, [
      discordUnanswered,
      { ...browserOff, enabled: true },
    ]);

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
    (await f.next('GET', '/api/low-days?from=2026-10-31&to=2026-10-31')).respond(200, [
      '2026-10-31',
    ]);

    await checkButton();
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByText(/Yesterday is still open/)).toBeNull();
  });

  it("doesn't ask about yesterday when only a weekly habit or a rest day was on it", async () => {
    renderToday();
    await answerLoad(f, [
      { ...habit, frequency: 'weekly', weeklyTarget: 3 },
      {
        ...habit,
        id: 2,
        name: 'Read',
        frequency: 'weekdays',
        days: ['mon', 'tue', 'wed', 'thu', 'fri'],
      },
    ]);

    await screen.findByRole('button', { name: 'Check in Walk for today' });
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
    await answerLoad(f, [
      doneHabit,
      { ...habit, id: 2, name: 'Swim', frequency: 'weekly', weeklyTarget: 2 },
    ]);

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

    const section = (await screen.findByRole('heading', { name: 'Letting go' })).closest(
      'section',
    )!;
    expect(within(section).getByText('Coffee')).toBeInTheDocument();
    expect(within(section).getByText('4 days without')).toBeInTheDocument();
    expect(within(section).getByRole('button', { name: 'I slipped: Coffee' })).toBeInTheDocument();
    expect(section).not.toContainElement(
      screen.getByRole('button', { name: 'Check in Walk for today' }),
    );
    expect((await screen.findAllByText(/^1 checked in today/)).length).toBeGreaterThan(0);
    expect(screen.queryByText('2 checked in today')).toBeNull();
    expect(screen.getAllByText(/That's everything for today\./).length).toBeGreaterThan(0);
  });

  it('opens the Try instead ideas from the let-go section only when asked', async () => {
    renderToday();
    await answerLoad(f, [doneHabit, letGoHabit]);

    const toggle = await screen.findByRole('button', { name: 'Try instead' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('list', { name: 'Try instead' })).toBeNull();
    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(
      within(screen.getByRole('list', { name: 'Try instead' })).getByText('Meet a friend'),
    ).toBeInTheDocument();
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
    expect(within(dialog).getByRole('button', { name: 'Tired' })).toHaveAttribute(
      'aria-pressed',
      'true',
    );
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
    expect(
      await within(dialog).findByText('Taken back. Today has no slips noted.'),
    ).toBeInTheDocument();
    expect(await screen.findByText('4 days without')).toBeInTheDocument();
  });

  it('saves a slip without a reason as just the date', async () => {
    renderToday();
    await answerLoad(f, [letGoHabit]);
    await userEvent.click(await screen.findByRole('button', { name: 'I slipped: Coffee' }));
    await userEvent.click(
      within(await screen.findByRole('dialog')).getByRole('button', { name: 'Save' }),
    );

    expect((await f.next('POST', '/api/habits/9/slips')).body).toEqual({ date: '2026-11-01' });
  });

  it('shows the reason form again with no reason picked when the sheet is reopened after a save, and saves a second slip', async () => {
    renderToday();
    await answerLoad(f, [letGoHabit]);
    const slipped = {
      ...letGoHabit,
      today: { ...letGoHabit.today, state: 'slipped' as const, slips: 1 },
    };
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
    expect(within(dialog).getByRole('button', { name: 'Tired' })).toHaveAttribute(
      'aria-pressed',
      'false',
    );
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
    await answerLoad(f, [
      { ...letGoHabit, pausedUntil: '2026-11-10', today: { ...letGoHabit.today, state: 'paused' } },
    ]);

    expect(await screen.findByText('Coffee')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'I slipped: Coffee' })).toBeNull();
  });
});

describe('TodayPage made it today', () => {
  let f: FetchMock;
  const marked = { ...letGoHabit, today: { ...letGoHabit.today, madeIt: true } };

  beforeEach(() => {
    f = mockFetch();
    auth.setMe(me);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    auth.clearSession();
    localMode.mode.set('account');
  });

  it('marks a free day made it with a warm line that fades after six seconds, and unmarks on a second tap', async () => {
    renderToday();
    await answerLoad(f, [letGoHabit]);
    const button = await screen.findByRole('button', { name: 'Made it today: Coffee' });
    expect(button).toHaveAttribute('aria-pressed', 'false');

    vi.useFakeTimers({ shouldAdvanceTime: true });
    fireEvent.click(button);
    (await f.next('PUT', '/api/habits/9/made-it/2026-11-01')).respond(200, marked);

    expect(await screen.findByText('Nice. Let that feel good.')).toBeVisible();
    expect(button).toHaveAttribute('aria-pressed', 'true');

    act(() => {
      vi.advanceTimersByTime(6000);
    });
    expect(screen.queryByText('Nice. Let that feel good.')).toBeNull();
    expect(button).toHaveAttribute('aria-pressed', 'true');

    fireEvent.click(button);
    (await f.next('DELETE', '/api/habits/9/made-it/2026-11-01')).respond(200, letGoHabit);
    await waitFor(() => expect(button).toHaveAttribute('aria-pressed', 'false'));
    expect(screen.queryByText('Nice. Let that feel good.')).toBeNull();
  });

  it('keeps the warm line for the full stretch before six seconds are up', async () => {
    renderToday();
    await answerLoad(f, [letGoHabit]);
    const button = await screen.findByRole('button', { name: 'Made it today: Coffee' });

    vi.useFakeTimers({ shouldAdvanceTime: true });
    fireEvent.click(button);
    (await f.next('PUT', '/api/habits/9/made-it/2026-11-01')).respond(200, marked);
    await screen.findByText('Nice. Let that feel good.');
    act(() => {
      vi.advanceTimersByTime(5000);
    });

    expect(screen.getByText('Nice. Let that feel good.')).toBeVisible();
  });

  it.each([
    ['slipped', { state: 'slipped' as const, slips: 1 }, {}],
    ['paused', { state: 'paused' as const }, { pausedUntil: '2026-11-10' }],
    ['not started', { state: 'beforestart' as const }, { startDate: '2026-11-05' }],
  ])('offers no made it toggle on a %s day', async (_name, today, habitChange) => {
    renderToday();
    await answerLoad(f, [
      { ...letGoHabit, ...habitChange, today: { ...letGoHabit.today, ...today } },
    ]);

    expect(await screen.findByText('Coffee')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Made it today: Coffee' })).toBeNull();
  });

  it('shows a quiet notice and leaves the toggle unpressed when marking fails', async () => {
    renderToday();
    await answerLoad(f, [letGoHabit]);
    const button = await screen.findByRole('button', { name: 'Made it today: Coffee' });

    await userEvent.click(button);
    (await f.next('PUT', '/api/habits/9/made-it/2026-11-01')).respond(500, {
      title: 'Server error',
      status: 500,
    });

    expect(await screen.findByText("That didn't save just now")).toBeInTheDocument();
    expect(button).toHaveAttribute('aria-pressed', 'false');
    expect(screen.queryByText('Nice. Let that feel good.')).toBeNull();
  });
});

describe('TodayPage sections and order', () => {
  let f: FetchMock;
  const at = (
    id: number,
    name: string,
    time: string | null,
    change: Partial<Habit> = {},
  ): Habit => ({
    ...habit,
    id,
    name,
    reminderTime: time,
    reminderTimes: time ? [time] : [],
    ...change,
  });
  const restDay = (id: number, name: string): Habit =>
    at(id, name, null, { frequency: 'weekdays', days: ['mon'] });
  const section = (name: string | RegExp) =>
    screen.getByRole('heading', { level: 2, name }).closest('section')!;
  const rowNames = (name: string | RegExp) =>
    within(section(name))
      .getAllByRole('heading', { level: 3 })
      .map((h) => h.textContent);
  const choose = async (menu: string, item: string) => {
    await userEvent.click(screen.getByRole('button', { name: menu }));
    await userEvent.click(await screen.findByRole('menuitem', { name: item }));
  };

  beforeEach(() => {
    f = mockFetch();
    auth.setMe(me);
    localStorage.removeItem('ember.notTodayOpen');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
    localStorage.removeItem('ember.notTodayOpen');
  });

  it('lists only what is for today, and keeps the rest in a collapsed "Not for today" that this device remembers open', async () => {
    const swim = at(3, 'Swim', null, {
      frequency: 'weekly',
      weeklyTarget: 2,
      stats: { ...habit.stats, week: { done: 2, target: 2 } },
    });
    renderToday();
    await answerLoad(f, [habit, restDay(2, 'Gym'), swim]);

    const toggle = await screen.findByRole('button', { name: 'Not for today · 2' });
    expect(rowNames('For today')).toEqual(['Walk']);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('heading', { name: 'Gym' })).toBeNull();

    await userEvent.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(rowNames(/Not for today/)).toEqual(['Gym', 'Swim']);
    expect(within(section(/Not for today/)).getByText('Done for this week')).toBeInTheDocument();
    expect(localStorage.getItem('ember.notTodayOpen')).toBe('1');

    cleanup();
    renderToday();
    await answerLoad(f, [habit, restDay(2, 'Gym'), swim]);
    expect(await screen.findByRole('button', { name: 'Not for today · 2' })).toHaveAttribute(
      'aria-expanded',
      'true',
    );
  });

  it('keeps a weekly habit under For today through the check-in that meets its target', async () => {
    const swim = at(3, 'Swim', null, {
      frequency: 'weekly',
      weeklyTarget: 2,
      stats: { ...habit.stats, week: { done: 1, target: 2 } },
    });
    renderToday();
    await answerLoad(f, [swim]);

    await userEvent.click(await screen.findByRole('button', { name: 'Check in Swim for today' }));
    expect(rowNames('For today')).toEqual(['Swim']);
    (await f.next('PUT', '/api/habits/3/days/2026-11-01')).respond(200, {
      ...swim,
      today: { ...swim.today, state: 'done', completed: true, count: 1 },
      stats: { ...swim.stats, week: { done: 2, target: 2 } },
    });

    expect(await screen.findByText('Swim: checked in for today.')).toBeInTheDocument();
    expect(rowNames('For today')).toEqual(['Swim']);
    expect(screen.queryByRole('button', { name: /Not for today/ })).toBeNull();
  });

  it('saves the full order with "Move down", let-go keys included, and then offers "Sort by time again"', async () => {
    renderToday();
    await answerLoad(f, [at(1, 'Meds', '08:00'), at(2, 'Read', '21:00'), letGoHabit]);
    await screen.findByRole('heading', { name: 'Meds' });
    expect(screen.queryByRole('button', { name: 'List options' })).toBeNull();

    await choose('Options for Meds', 'Move down');

    const put = await f.next('PUT', '/api/me/today-order');
    expect(put.body).toEqual({ order: ['h:2', 'h:1', 'h:9'] });
    expect(rowNames('For today')).toEqual(['Read', 'Meds']);
    put.respond(200, { order: ['h:2', 'h:1', 'h:9'] });
    expect(await screen.findByText('Meds moved down.')).toBeInTheDocument();
    expect(auth.me.get()?.todayOrder).toEqual(['h:2', 'h:1', 'h:9']);
    expect(screen.getByRole('button', { name: 'List options' })).toBeInTheDocument();
  });

  it('goes back to the time order with "Sort by time again"', async () => {
    auth.setMe({ ...me, todayOrder: ['h:2', 'h:1'] });
    renderToday();
    await answerLoad(f, [at(1, 'Meds', '08:00'), at(2, 'Read', '21:00')], {
      account: { todayOrder: ['h:2', 'h:1'] },
    });
    await screen.findByRole('heading', { name: 'Meds' });
    expect(rowNames('For today')).toEqual(['Read', 'Meds']);

    await choose('List options', 'Sort by time again');

    const put = await f.next('PUT', '/api/me/today-order');
    expect(put.body).toEqual({ order: null });
    put.respond(200, { order: null });
    expect(rowNames('For today')).toEqual(['Meds', 'Read']);
    await waitFor(() => expect(screen.queryByRole('button', { name: 'List options' })).toBeNull());
  });

  it('puts the old order back and says so when the new one did not save', async () => {
    renderToday();
    await answerLoad(f, [at(1, 'Meds', '08:00'), at(2, 'Read', '21:00')]);
    await screen.findByRole('heading', { name: 'Meds' });

    await choose('Options for Read', 'Move up');
    expect(rowNames('For today')).toEqual(['Read', 'Meds']);
    (await f.next('PUT', '/api/me/today-order')).respond(500, { type: 'internal' });

    expect(await screen.findByText("Your new order didn't save")).toBeInTheDocument();
    expect(rowNames('For today')).toEqual(['Meds', 'Read']);
    expect(auth.me.get()?.todayOrder).toBeNull();
  });

  it("moves a routine member within its routine and sends the routine's full habit list, the hidden member in its slot", async () => {
    const routine: Routine = {
      id: 5,
      name: 'Morning',
      reminderTime: null,
      reminderChannels: [],
      habitIds: [3, 1, 2],
    };
    renderToday();
    await answerLoad(f, [at(1, 'Walk', null), at(2, 'Water', null), restDay(3, 'Gym')], {
      routines: [routine],
    });
    await userEvent.click(await screen.findByRole('button', { name: 'Morning' }));

    await choose('Options for Water', 'Move up');

    const patch = await f.next('PATCH', '/api/routines/5');
    expect(patch.body).toEqual({ habitIds: [3, 2, 1] });
    const block = screen.getByRole('heading', { level: 3, name: 'Morning' }).closest('section')!;
    expect(
      within(block)
        .getAllByRole('heading', { level: 4 })
        .map((h) => h.textContent),
    ).toEqual(['Water', 'Walk']);
    patch.respond(200, { ...routine, habitIds: [3, 2, 1] });
    expect(await screen.findByText('Water moved up.')).toBeInTheDocument();
  });

  it('moves a routine as one block with "Move down"', async () => {
    const routine: Routine = {
      id: 5,
      name: 'Morning',
      reminderTime: '07:00',
      reminderChannels: [],
      habitIds: [1, 2],
    };
    renderToday();
    await answerLoad(f, [at(1, 'Walk', null), at(2, 'Water', null), at(3, 'Read', '21:00')], {
      routines: [routine],
    });
    await screen.findByRole('heading', { name: 'Morning' });

    await choose('Options for Morning routine', 'Move down');

    expect((await f.next('PUT', '/api/me/today-order')).body).toEqual({ order: ['h:3', 'r:5'] });
    expect(rowNames('For today')).toEqual(['Read', 'Morning']);
  });

  it('lifts a row after a long press, drags it to the end and saves that order', async () => {
    renderToday();
    await answerLoad(f, [at(1, 'Meds', '08:00'), at(2, 'Read', '21:00')]);
    const row = (await screen.findByRole('heading', { name: 'Meds' })).closest('li')!;

    fireEvent.pointerDown(row, { button: 0, pointerId: 1, clientX: 10, clientY: 10 });
    await waitFor(
      () =>
        expect(
          screen.getAllByText('Moving “Meds”. Let go to drop it here.').length,
        ).toBeGreaterThan(0),
      {
        timeout: 1500,
      },
    );
    fireEvent.pointerMove(row, { pointerId: 1, clientX: 10, clientY: 400 });
    expect(rowNames('For today')).toEqual(['Read', 'Meds']);
    fireEvent.pointerUp(row, { pointerId: 1, clientX: 10, clientY: 400 });

    expect((await f.next('PUT', '/api/me/today-order')).body).toEqual({ order: ['h:2', 'h:1'] });
    expect(screen.queryByText('Moving “Meds”. Let go to drop it here.')).toBeNull();
  });

  it('does not lift a row the pointer moved away from before the hold ended', async () => {
    renderToday();
    await answerLoad(f, [at(1, 'Meds', '08:00'), at(2, 'Read', '21:00')]);
    const row = (await screen.findByRole('heading', { name: 'Meds' })).closest('li')!;

    fireEvent.pointerDown(row, { button: 0, pointerId: 1, clientX: 10, clientY: 10 });
    fireEvent.pointerMove(row, { pointerId: 1, clientX: 10, clientY: 60 });
    await new Promise((resolve) => setTimeout(resolve, 650));
    fireEvent.pointerUp(row, { pointerId: 1, clientX: 10, clientY: 60 });

    expect(screen.queryByText(/Let go to drop it here/)).toBeNull();
    expect(f.all.some((c) => c.url === '/api/me/today-order')).toBe(false);
  });
});

describe('TodayPage habit notes', () => {
  let f: FetchMock;
  const notesUrl = '/api/habit-notes?from=2026-09-02&to=2026-11-01&habitId=1';

  beforeEach(() => {
    f = mockFetch();
    auth.setMe(me);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
  });

  it('writes a note from the row menu, shows earlier ones, and saves it as plain text', async () => {
    renderToday();
    await answerLoad(f, [habit]);
    await checkButton();

    await userEvent.click(screen.getByRole('button', { name: 'Options for Walk' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Note' }));
    (await f.next('GET', notesUrl)).respond(200, [
      { habitId: 1, date: '2026-10-30', text: 'Felt easy.' },
    ]);

    const dialog = await screen.findByRole('dialog', { name: 'Walk' });
    expect(await within(dialog).findByText(/Felt easy\./)).toBeInTheDocument();
    const box = within(dialog).getByRole('textbox', { name: 'A note for today, just for you' });
    await userEvent.type(box, 'Heavy, went anyway.');
    expect(within(dialog).getByText('19 / 280')).toBeInTheDocument();
    expect(box).toHaveAttribute('maxlength', '280');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save note' }));

    const put = await f.next('PUT', '/api/habits/1/notes/2026-11-01');
    expect(put.body).toEqual({ text: 'Heavy, went anyway.' });
    put.respond(200, { habitId: 1, date: '2026-11-01', text: 'Heavy, went anyway.' });
    expect(await screen.findByText('Note on Walk saved.')).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('keeps the text and says so gently when the note did not save', async () => {
    renderToday();
    await answerLoad(f, [habit]);
    await checkButton();
    await userEvent.click(screen.getByRole('button', { name: 'Options for Walk' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Note' }));
    (await f.next('GET', notesUrl)).respond(200, []);
    const dialog = await screen.findByRole('dialog', { name: 'Walk' });
    await userEvent.type(within(dialog).getByRole('textbox'), 'Still counts.');

    await userEvent.click(within(dialog).getByRole('button', { name: 'Save note' }));
    (await f.next('PUT', '/api/habits/1/notes/2026-11-01')).fail();

    expect(await within(dialog).findByText(/Your words are still here/)).toBeInTheDocument();
    expect(within(dialog).getByRole('textbox')).toHaveValue('Still counts.');
  });

  it('offers a note right after a check-in only when the account asked for it', async () => {
    auth.setMe({ ...me, offerHabitNote: true });
    renderToday();
    await answerLoad(f, [habit], { account: { offerHabitNote: true } });

    await userEvent.click(await checkButton());
    (await f.next('PUT', '/api/habits/1/days/2026-11-01')).respond(200, doneHabit);

    const dialog = await screen.findByRole('dialog', { name: 'Walk' });
    expect(
      within(dialog).getByText('Nice. Want to note how it went? You can skip this.'),
    ).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: 'Not now' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('never offers a note after a check-in while the setting is off', async () => {
    renderToday();
    await answerLoad(f, [habit]);

    await userEvent.click(await checkButton());
    (await f.next('PUT', '/api/habits/1/days/2026-11-01')).respond(200, doneHabit);

    expect(await screen.findByText('Walk: checked in for today.')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});

describe('TodayPage review fixes', () => {
  let f: FetchMock;
  const at = (
    id: number,
    name: string,
    time: string | null,
    change: Partial<Habit> = {},
  ): Habit => ({
    ...habit,
    id,
    name,
    reminderTime: time,
    reminderTimes: time ? [time] : [],
    ...change,
  });
  const restDay = (id: number, name: string): Habit =>
    at(id, name, null, { frequency: 'weekdays', days: ['mon'] });
  const forToday = () =>
    within(screen.getByRole('heading', { level: 2, name: 'For today' }).closest('section')!)
      .getAllByRole('heading', { level: 3 })
      .map((h) => h.textContent);
  const choose = async (menu: string, item: string) => {
    await userEvent.click(screen.getByRole('button', { name: menu }));
    await userEvent.click(await screen.findByRole('menuitem', { name: item }));
  };
  const lift = async (name: string) => {
    const row = (await screen.findByRole('heading', { name })).closest('li')!;
    fireEvent.pointerDown(row, { button: 0, pointerId: 1, clientX: 10, clientY: 10 });
    await waitFor(
      () =>
        expect(
          screen.getAllByText(`Moving “${name}”. Let go to drop it here.`).length,
        ).toBeGreaterThan(0),
      {
        timeout: 1500,
      },
    );
    return row;
  };

  beforeEach(() => {
    f = mockFetch();
    auth.setMe(me);
    localStorage.removeItem('ember.notTodayOpen');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
    localStorage.removeItem('ember.notTodayOpen');
  });

  it('checks in a paused habit under Not for today without a warning', async () => {
    const paused = at(2, 'Read', null, {
      pausedToday: true,
      pausedUntil: '2026-11-08',
      today: { ...habit.today, state: 'paused' },
    });
    renderToday();
    await answerLoad(f, [habit, paused]);

    await userEvent.click(await screen.findByRole('button', { name: 'Not for today · 1' }));
    await userEvent.click(screen.getByRole('button', { name: 'Check in Read for today' }));

    expect((await f.next('PUT', '/api/habits/2/days/2026-11-01')).body).toEqual({
      completed: true,
    });
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('opens Not for today for a reminder link to a habit in it', async () => {
    renderToday('/today?habit=2');
    await answerLoad(f, [habit, restDay(2, 'Gym')]);

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Not for today · 1' })).toHaveAttribute(
        'aria-expanded',
        'true',
      ),
    );
    expect(screen.getByRole('heading', { name: 'Gym' })).toBeVisible();
  });

  it('keeps dragging after a reload dropped a row, and saves only rows that are still there', async () => {
    renderToday();
    await answerLoad(f, [at(1, 'Meds', '08:00'), at(2, 'Read', '21:00'), at(3, 'Tea', null)]);
    const row = await lift('Meds');
    fireEvent.pointerMove(row, { pointerId: 1, clientX: 10, clientY: 400 });

    act(() => deviceSync.synced.update((n) => n + 1));
    await answerLoad(f, [at(1, 'Meds', '08:00'), at(3, 'Tea', null)]);
    await waitFor(() => expect(screen.queryByRole('heading', { name: 'Read' })).toBeNull());
    expect(forToday()).toEqual(['Tea', 'Meds']);
    fireEvent.pointerUp(row, { pointerId: 1, clientX: 10, clientY: 400 });

    expect((await f.next('PUT', '/api/me/today-order')).body).toEqual({ order: ['h:3', 'h:1'] });
  });

  it('blocks touch scrolling only while a row is lifted', async () => {
    renderToday();
    await answerLoad(f, [at(1, 'Meds', '08:00'), at(2, 'Read', '21:00')]);
    await screen.findByRole('heading', { name: 'Meds' });
    const touch = () => {
      const event = new Event('touchmove', { cancelable: true, bubbles: true });
      document.dispatchEvent(event);
      return event.defaultPrevented;
    };
    expect(touch()).toBe(false);

    const row = await lift('Meds');
    expect(touch()).toBe(true);
    fireEvent.pointerCancel(row, { pointerId: 1 });
    expect(touch()).toBe(false);
  });

  it('after Escape, swallows the click of the still-held press even when it ends late, and nothing moves', async () => {
    renderToday();
    await answerLoad(f, [at(1, 'Meds', '08:00'), at(2, 'Read', '21:00')]);
    const row = await lift('Meds');

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(await screen.findByText('Nothing moved.')).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 500));
    fireEvent.pointerUp(row, { pointerId: 1 });
    fireEvent.click(screen.getByRole('button', { name: 'Check in Meds for today' }));

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(f.all.some((c) => c.method === 'PUT')).toBe(false);
  });

  it('puts back the order the server last confirmed, not an unconfirmed one, when saves fail', async () => {
    renderToday();
    await answerLoad(f, [at(1, 'Meds', '08:00'), at(2, 'Read', '21:00'), at(3, 'Tea', null)]);
    await screen.findByRole('heading', { name: 'Meds' });

    await choose('Options for Meds', 'Move down');
    await choose('Options for Meds', 'Move down');
    expect(forToday()).toEqual(['Read', 'Tea', 'Meds']);
    (await f.next('PUT', '/api/me/today-order')).respond(500, { type: 'internal' });
    (await f.next('PUT', '/api/me/today-order')).respond(500, { type: 'internal' });

    expect(await screen.findByText("Your new order didn't save")).toBeInTheDocument();
    expect(forToday()).toEqual(['Meds', 'Read', 'Tea']);
    expect(auth.me.get()?.todayOrder).toBeNull();
  });

  it('sends routine moves one at a time and hands the saved routine to the Routines screens', async () => {
    const routine: Routine = {
      id: 5,
      name: 'Morning',
      reminderTime: null,
      reminderChannels: [],
      habitIds: [1, 2, 3],
    };
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    client.setQueryData(['routines'], [routine]);
    renderToday('/today', client);
    await answerLoad(f, [at(1, 'Walk', null), at(2, 'Water', null), at(3, 'Stretch', null)], {
      routines: [routine],
    });
    await userEvent.click(await screen.findByRole('button', { name: 'Morning' }));

    await choose('Options for Stretch', 'Move up');
    await choose('Options for Stretch', 'Move up');
    const first = await f.next('PATCH', '/api/routines/5');
    expect(first.body).toEqual({ habitIds: [1, 3, 2] });
    expect(f.pending.filter((c) => c.method === 'PATCH')).toHaveLength(0);
    first.respond(200, { ...routine, habitIds: [1, 3, 2] });
    const second = await f.next('PATCH', '/api/routines/5');
    expect(second.body).toEqual({ habitIds: [3, 1, 2] });
    second.respond(200, { ...routine, habitIds: [3, 1, 2] });

    await waitFor(() =>
      expect(client.getQueryData<Routine[]>(['routines'])?.[0]?.habitIds).toEqual([3, 1, 2]),
    );
  });

  it('moves focus to For today after "Sort by time again" takes the list menu away', async () => {
    renderToday();
    await answerLoad(f, [at(1, 'Meds', '08:00'), at(2, 'Read', '21:00')], {
      account: { todayOrder: ['h:2', 'h:1'] },
    });
    await screen.findByRole('heading', { name: 'Meds' });

    await choose('List options', 'Sort by time again');

    await waitFor(() =>
      expect(screen.getByRole('heading', { level: 2, name: 'For today' })).toHaveFocus(),
    );
  });

  it("offers Save only once the day's note has loaded and the text changed, so a slow load never deletes it", async () => {
    renderToday();
    await answerLoad(f, [habit]);
    await checkButton();
    await choose('Options for Walk', 'Note');
    const dialog = await screen.findByRole('dialog', { name: 'Walk' });
    const save = within(dialog).getByRole('button', { name: 'Save note' });

    expect(save).toHaveAttribute('aria-disabled', 'true');
    await userEvent.click(save);
    (await f.next('GET', '/api/habit-notes?from=2026-09-02&to=2026-11-01&habitId=1')).respond(200, [
      { habitId: 1, date: '2026-11-01', text: 'Kept.' },
    ]);
    await waitFor(() => expect(within(dialog).getByRole('textbox')).toHaveValue('Kept.'));
    expect(save).toHaveAttribute('aria-disabled', 'true');
    expect(f.all.some((c) => c.url.startsWith('/api/habits/1/notes'))).toBe(false);

    await userEvent.type(within(dialog).getByRole('textbox'), ' Still.');
    expect(save).not.toHaveAttribute('aria-disabled');
  });
});

describe('TodayPage routine row', () => {
  let f: FetchMock;
  const member = (id: number, name: string, change: Partial<Habit> = {}): Habit => ({
    ...habit,
    id,
    name,
    ...change,
  });
  const day = (state: Habit['today']['state'], change: Partial<Habit['today']> = {}) => ({
    today: { ...habit.today, state, ...change },
  });
  const morning = (habitIds: number[]): Routine => ({
    id: 5,
    name: 'Morning',
    reminderTime: null,
    reminderChannels: [],
    habitIds,
  });
  const toggle = () => screen.findByRole('button', { name: 'Morning' });

  beforeEach(() => {
    f = mockFetch();
    auth.setMe(me);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    auth.clearSession();
  });

  it('counts skipped members as settled, partial ones as open, and leaves paused ones out of the progress', async () => {
    renderToday();
    await answerLoad(
      f,
      [
        member(1, 'Walk', day('done', { completed: true, count: 1 })),
        member(2, 'Water', day('notdone', { completed: false, skipped: true })),
        member(3, 'Read'),
        member(4, 'Stretch', { timesPerDay: 2, ...day('partial', { count: 1 }) }),
        member(6, 'Meds', { pausedToday: true, ...day('paused') }),
      ],
      { routines: [morning([1, 2, 3, 4, 6])] },
    );

    expect(await toggle()).toHaveAccessibleDescription('2 of 4 done');
    expect(screen.getByText('2/4')).toBeInTheDocument();
  });

  it('says Done today once every member is done or skipped, and shows the low-day line on a low day', async () => {
    renderToday();
    await answerLoad(
      f,
      [
        member(1, 'Walk', day('done', { completed: true, count: 1 })),
        member(2, 'Water', day('notdone', { completed: false, skipped: true })),
      ],
      { routines: [morning([1, 2])] },
    );
    expect(await toggle()).toHaveAccessibleDescription('Done today');
    cleanup();

    renderToday();
    (await f.next('GET', '/api/me')).respond(200, me);
    (await f.next('GET', '/api/habits')).respond(200, [
      member(1, 'Walk', day('lowday')),
      member(2, 'Water', day('lowday')),
    ]);
    (await f.next('GET', '/api/low-days?from=2026-11-01&to=2026-11-01')).respond(200, [
      '2026-11-01',
    ]);
    (await f.next('GET', '/api/stats/weekly')).respond(200, weekly);
    (await f.next('GET', '/api/routines')).respond(200, [morning([1, 2])]);
    expect(await toggle()).toHaveAccessibleDescription(
      'Low day, resting. Checking in is still welcome.',
    );
  });

  it('opens and closes with a tap or the keyboard, and the members it controls show only while open', async () => {
    renderToday();
    await answerLoad(f, [member(1, 'Walk'), member(2, 'Water')], { routines: [morning([1, 2])] });
    const button = await toggle();
    const members = document.getElementById(button.getAttribute('aria-controls')!)!;

    expect(button).toHaveAttribute('aria-expanded', 'false');
    expect(members).not.toBeVisible();
    await userEvent.click(button);
    expect(button).toHaveAttribute('aria-expanded', 'true');
    expect(within(members).getByRole('heading', { level: 4, name: 'Walk' })).toBeVisible();
    button.focus();
    await userEvent.keyboard('{Enter}');
    expect(button).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('heading', { level: 4 })).toBeNull();
  });

  it('remembers an open routine on this device after a reload', async () => {
    renderToday();
    await answerLoad(f, [member(1, 'Walk')], { routines: [morning([1])] });
    await userEvent.click(await toggle());
    cleanup();

    renderToday();
    await answerLoad(f, [member(1, 'Walk')], { routines: [morning([1])] });
    expect(await toggle()).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('heading', { level: 4, name: 'Walk' })).toBeVisible();
  });

  it('starts closed and still opens when this device refuses storage', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });
    renderToday();
    await answerLoad(f, [member(1, 'Walk')], { routines: [morning([1])] });

    const button = await toggle();
    expect(button).toHaveAttribute('aria-expanded', 'false');
    await userEvent.click(button);
    expect(button).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('heading', { level: 4, name: 'Walk' })).toBeVisible();
  });

  it('lifts the closed routine row with a long press and drops it as one block, without the ending tap opening it', async () => {
    renderToday();
    await answerLoad(
      f,
      [
        member(1, 'Walk'),
        member(2, 'Water'),
        member(3, 'Read', { reminderTime: '21:00', reminderTimes: ['21:00'] }),
      ],
      {
        routines: [{ ...morning([1, 2]), reminderTime: '07:00' }],
      },
    );
    const row = (await toggle()).closest('li')!;

    fireEvent.pointerDown(row, { button: 0, pointerId: 1, clientX: 10, clientY: 10 });
    await waitFor(
      () =>
        expect(
          screen.getAllByText('Moving “Morning routine”. Let go to drop it here.').length,
        ).toBeGreaterThan(0),
      {
        timeout: 1500,
      },
    );
    fireEvent.pointerMove(row, { pointerId: 1, clientX: 10, clientY: 400 });
    fireEvent.pointerUp(row, { pointerId: 1, clientX: 10, clientY: 400 });
    fireEvent.click(await toggle());

    expect((await f.next('PUT', '/api/me/today-order')).body).toEqual({ order: ['h:3', 'r:5'] });
    expect(await toggle()).toHaveAttribute('aria-expanded', 'false');
  });

  it('updates the progress when a member is checked in inside the open routine', async () => {
    renderToday();
    await answerLoad(f, [member(1, 'Walk'), member(2, 'Water')], { routines: [morning([1, 2])] });
    const button = await toggle();
    expect(button).toHaveAccessibleDescription('0 of 2 done');
    await userEvent.click(button);

    await userEvent.click(screen.getByRole('button', { name: 'Check in Walk for today' }));
    const put = await f.next('PUT', '/api/habits/1/days/2026-11-01');
    expect(put.body).toEqual({ completed: true });
    put.respond(200, { ...doneHabit, id: 1, name: 'Walk' });

    await waitFor(() => expect(button).toHaveAccessibleDescription('1 of 2 done'));
  });

  it('opens the routine a reminder link points into, then scrolls to and rings the habit', async () => {
    const scrolled = vi.fn<(this: Element, options?: ScrollIntoViewOptions) => void>(function (
      this: Element,
    ) {});
    Element.prototype.scrollIntoView = scrolled;
    renderToday('/today?habit=2');
    await answerLoad(f, [member(1, 'Walk'), member(2, 'Water')], { routines: [morning([1, 2])] });

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Morning' })).toHaveAttribute(
        'aria-expanded',
        'true',
      ),
    );
    const row = screen.getByRole('heading', { level: 4, name: 'Water' }).closest('li')!;
    expect(row).toBeVisible();
    await waitFor(() => expect(scrolled.mock.contexts).toEqual([row]));
    expect(row.firstElementChild!.className).toMatch(/ring-primary/);
  });

  it("points the guide's check-in at a shown check button, never one inside a closed routine", async () => {
    renderToday();
    await answerLoad(f, [member(1, 'Walk'), member(2, 'Read')], { routines: [morning([1])] });
    await screen.findByRole('button', { name: 'Morning' });

    expect(findGuideTarget('check-in')).toBe(
      screen.getByRole('button', { name: 'Check in Read for today' }),
    );
  });
});
