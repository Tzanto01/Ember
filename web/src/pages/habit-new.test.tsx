import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { env } from '@/lib/env';
import type { HabitTemplate } from '@/lib/models';
import { auth, localMode, push } from '@/lib/services';
import { type FetchMock, habit, me, mockFetch } from '@/test/test-support';
import HabitNewPage from './habit-new';

vi.mock('@/lib/env', async (original) => ({ env: { ...(await original<typeof import('@/lib/env')>()).env } }));
vi.mock('@/lib/services', async (original) => {
  const { Store } = await import('@/lib/store');
  return { ...(await original<typeof import('@/lib/services')>()), localMode: { mode: new Store('account') } };
});

const template: HabitTemplate = {
  key: 'walk',
  name: 'Walk',
  description: 'A short walk outside.',
  suggestedReminderTime: '08:00',
  frequency: 'daily',
  weeklyTarget: null,
};

function renderPage() {
  const router = createMemoryRouter(
    [
      { path: '/habits/new', Component: HabitNewPage },
      { path: '/today', element: <h1>Today</h1> },
    ],
    { initialEntries: ['/habits/new'] },
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return router;
}

describe('HabitNewPage', () => {
  beforeEach(() => {
    vi.stubGlobal(
      'ResizeObserver',
      class {
        observe() {}
        unobserve() {}
        disconnect() {}
      },
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    localMode.mode.set('account');
    env.native = false;
    auth.me.set(null);
    push.available.set(false);
    push.subscribed.set(false);
  });

  describe('reminder times', () => {
    async function threeTimes(user: ReturnType<typeof userEvent.setup>, fetch: FetchMock) {
      (await fetch.next('GET', '/api/habit-templates')).respond(200, []);
      await user.type(await screen.findByLabelText('Name', { exact: true }), 'Water');
      await user.selectOptions(screen.getByLabelText('Times a day'), '3');
      fireEvent.change(screen.getByLabelText('Reminder time 1'), { target: { value: '20:00' } });
      await user.click(screen.getByRole('button', { name: 'Add another time' }));
      fireEvent.change(screen.getByLabelText('Reminder time 2'), { target: { value: '08:00' } });
      await user.click(screen.getByRole('button', { name: 'Add another time' }));
      fireEvent.change(screen.getByLabelText('Reminder time 3'), { target: { value: '13:00' } });
    }

    it('offers a time per check-in, focuses each new one and spells out the schedule', async () => {
      const user = userEvent.setup();
      const fetch = mockFetch();
      renderPage();
      (await fetch.next('GET', '/api/habit-templates')).respond(200, []);

      await user.type(await screen.findByLabelText('Name', { exact: true }), 'Water');
      expect(screen.queryByRole('button', { name: 'Add another time' })).toBeNull();
      await user.selectOptions(screen.getByLabelText('Times a day'), '3');
      const group = screen.getByRole('group', { name: 'Reminder times (optional)' });
      fireEvent.change(screen.getByLabelText('Reminder time 1'), { target: { value: '20:00' } });
      expect(group).toHaveAccessibleDescription(/Ember reminds you once, at 20:00/);
      await user.click(screen.getByRole('button', { name: 'Add another time' }));
      expect(screen.getByLabelText('Reminder time 2')).toHaveFocus();
      fireEvent.change(screen.getByLabelText('Reminder time 2'), { target: { value: '08:00' } });
      await user.click(screen.getByRole('button', { name: 'Add another time' }));
      fireEvent.change(screen.getByLabelText('Reminder time 3'), { target: { value: '13:00' } });

      expect(screen.queryByRole('button', { name: 'Add another time' })).toBeNull();
      expect(group).toHaveAccessibleDescription(
        /Ember reminds you at 08:00, 13:00 and 20:00 until you've checked in 3 times\./,
      );
    });

    it('removes a time, drops the latest when check-ins go down, and sends the times sorted', async () => {
      const user = userEvent.setup();
      const fetch = mockFetch();
      renderPage();
      await threeTimes(user, fetch);

      await user.click(screen.getByRole('button', { name: 'Remove 13:00' }));
      expect(screen.getByLabelText('Reminder time 2')).toHaveFocus();
      expect(screen.queryByLabelText('Reminder time 3')).toBeNull();
      await user.click(screen.getByRole('button', { name: 'Add another time' }));
      fireEvent.change(screen.getByLabelText('Reminder time 3'), { target: { value: '13:00' } });

      await user.selectOptions(screen.getByLabelText('Times a day'), '2');
      expect(screen.getAllByText('20:00 was taken off, since you now check in 2 times a day.')).toHaveLength(2);
      expect(screen.getByLabelText('Reminder time 1')).toHaveValue('08:00');
      expect(screen.getByLabelText('Reminder time 2')).toHaveValue('13:00');
      await user.click(screen.getByRole('button', { name: 'Create habit' }));

      expect((await fetch.next('POST', '/api/habits')).body).toEqual({
        name: 'Water',
        frequency: 'daily',
        timesPerDay: 2,
        reminderTimes: ['08:00', '13:00'],
      });
    });
  });

  describe('timer length', () => {
    it('is empty by default, asks for whole minutes in range, and is sent with the habit', async () => {
      const user = userEvent.setup();
      const fetch = mockFetch();
      renderPage();
      (await fetch.next('GET', '/api/habit-templates')).respond(200, []);
      await user.type(await screen.findByLabelText('Name', { exact: true }), 'Walk');
      const length = screen.getByLabelText(/^Timer length/);
      expect(length).toHaveValue('');

      for (const wrong of ['0', '241', '1.5']) {
        await user.clear(length);
        await user.type(length, wrong);
        await user.click(screen.getByRole('button', { name: 'Create habit' }));
        expect(length).toHaveFocus();
        expect(length).toHaveAccessibleDescription(/Use whole minutes from 1 to 240, or leave it empty\./);
      }
      expect(fetch.all.some((call) => call.method === 'POST')).toBe(false);

      await user.clear(length);
      await user.type(length, '30');
      await user.click(screen.getByRole('button', { name: 'Create habit' }));
      const post = await fetch.next('POST', '/api/habits');
      expect(post.body).toEqual({ name: 'Walk', frequency: 'daily', timesPerDay: 1, timerMinutes: 30 });
      post.respond(400, { type: 'https://ember/problems/validation', status: 400, errors: { timerMinutes: ['Too long.'] } });
      await waitFor(() => expect(length).toHaveAccessibleDescription(/Too long\./));
    });

    it('is dropped for a habit to let go of', async () => {
      const user = userEvent.setup();
      const fetch = mockFetch();
      renderPage();
      (await fetch.next('GET', '/api/habit-templates')).respond(200, []);
      await user.type(await screen.findByLabelText('Name', { exact: true }), 'Coffee');
      await user.type(screen.getByLabelText(/^Timer length/), '300');
      await user.click(screen.getByRole('radio', { name: 'Something to let go of' }));
      expect(screen.queryByLabelText(/^Timer length/)).toBeNull();
      await user.click(screen.getByRole('button', { name: 'Create habit' }));

      expect((await fetch.next('POST', '/api/habits')).body).not.toHaveProperty('timerMinutes');
    });
  });

  describe('how a habit reminds you', () => {
    beforeEach(() => {
      auth.setMe({ ...me, discord: { linked: true, id: '1' } });
      push.available.set(true);
      vi.spyOn(push, 'check').mockResolvedValue();
    });

    async function createWith(fetch: FetchMock, name: string, time: string) {
      await user().type(await screen.findByLabelText('Name', { exact: true }), name);
      fireEvent.change(screen.getByLabelText(/Reminder time/), { target: { value: time } });
      await user().click(screen.getByRole('button', { name: 'Create habit' }));
      return fetch.next('POST', '/api/habits');
    }
    let current: ReturnType<typeof userEvent.setup>;
    const user = () => current;
    beforeEach(() => {
      current = userEvent.setup();
    });

    it('reminds everywhere without a channel choice, asking this browser on the first reminder and not again once subscribed', async () => {
      const subscribe = vi.spyOn(push, 'subscribe').mockImplementation(async () => push.subscribed.set(true));
      const fetch = mockFetch();
      const router = renderPage();
      (await fetch.next('GET', '/api/habit-templates')).respond(200, []);

      expect(await screen.findByLabelText('Name', { exact: true })).toBeInTheDocument();
      expect(screen.queryByRole('checkbox')).toBeNull();
      const first = await createWith(fetch, 'Water', '08:00');
      expect(first.body).toEqual({ name: 'Water', frequency: 'daily', timesPerDay: 1, reminderTimes: ['08:00'] });
      expect(subscribe).toHaveBeenCalledTimes(1);
      first.respond(201, { ...habit, name: 'Water' });
      expect(await screen.findByRole('heading', { name: 'Today' })).toBeInTheDocument();

      await router.navigate('/habits/new');
      (await fetch.next('GET', '/api/habit-templates')).respond(200, []);
      (await createWith(fetch, 'Read', '21:00')).respond(201, { ...habit, id: 2, name: 'Read' });
      expect(await screen.findByRole('heading', { name: 'Today' })).toBeInTheDocument();
      expect(subscribe).toHaveBeenCalledTimes(1);
    });

    it('creates the habit when the browser blocks notifications, and says where to turn them on', async () => {
      vi.spyOn(push, 'subscribe').mockRejectedValue(new DOMException('Denied', 'NotAllowedError'));
      const fetch = mockFetch();
      const router = renderPage();
      (await fetch.next('GET', '/api/habit-templates')).respond(200, []);

      const create = await createWith(fetch, 'Water', '08:00');
      expect(create.body).toMatchObject({ reminderTimes: ['08:00'] });
      create.respond(201, { ...habit, name: 'Water' });

      expect(await screen.findByText(/Notifications are off for Ember in this browser/)).toBeInTheDocument();
      expect(screen.getByRole('link', { name: 'Turn them on in Settings' })).toHaveAttribute('href', '/settings#push');
      expect(router.state.location.pathname).toBe('/habits/new');
      expect(screen.queryByRole('button', { name: 'Create habit' })).toBeNull();
    });

    it('in the app without an account, sends no reminder times with the phone turned off, and says nothing will go out', async () => {
      env.native = true;
      localMode.mode.set('local');
      const fetch = mockFetch();
      renderPage();
      (await fetch.next('GET', '/api/habit-templates')).respond(200, []);

      await user().type(await screen.findByLabelText('Name', { exact: true }), 'Water');
      fireEvent.change(screen.getByLabelText(/Reminder time/), { target: { value: '08:00' } });
      await user().click(screen.getByRole('checkbox', { name: 'Remind me on this phone' }));
      expect(screen.getByText(/No reminders will go out for this habit/)).toBeInTheDocument();
      await user().click(screen.getByRole('button', { name: 'Create habit' }));

      expect((await fetch.next('POST', '/api/habits')).body).toEqual({
        name: 'Water',
        frequency: 'daily',
        timesPerDay: 1,
        reminderChannels: [],
      });
    });
  });

  describe('something to let go of', () => {
    it('starts from the picker with no schedule fields and sends a daily let-go habit without a reminder', async () => {
      const user = userEvent.setup();
      const fetch = mockFetch();
      renderPage();
      (await fetch.next('GET', '/api/habit-templates')).respond(200, [template]);

      await user.click(await screen.findByRole('button', { name: 'Something to let go of' }));
      expect(screen.queryByRole('radio', { name: 'A few times a week' })).toBeNull();
      expect(screen.queryByLabelText('Times a day')).toBeNull();
      expect(screen.queryByLabelText('Times per week')).toBeNull();
      await user.type(screen.getByLabelText('Name', { exact: true }), '  Coffee after 3pm ');
      await user.click(screen.getByRole('button', { name: 'Create habit' }));

      const create = await fetch.next('POST', '/api/habits');
      expect(create.body).toEqual({ name: 'Coffee after 3pm', kind: 'letGo', frequency: 'daily', timesPerDay: 1 });
    });

    it('drops a weekly schedule and extra times when the kind is switched to let go, keeping one check-in time', async () => {
      const user = userEvent.setup();
      const fetch = mockFetch();
      renderPage();
      (await fetch.next('GET', '/api/habit-templates')).respond(200, []);

      await user.type(await screen.findByLabelText('Name', { exact: true }), 'Scrolling');
      await user.click(screen.getByRole('radio', { name: 'A few times a week' }));
      await user.click(screen.getByRole('radio', { name: 'Something to let go of' }));
      expect(screen.queryByRole('radio', { name: 'A few times a week' })).toBeNull();
      expect(screen.queryByLabelText('Times per week')).toBeNull();
      expect(screen.queryByRole('button', { name: 'Add another time' })).toBeNull();
      fireEvent.change(screen.getByLabelText(/Quiet evening check-in/), { target: { value: '21:00' } });
      await user.click(screen.getByRole('button', { name: 'Create habit' }));

      const create = await fetch.next('POST', '/api/habits');
      expect(create.body).toEqual({
        name: 'Scrolling',
        kind: 'letGo',
        frequency: 'daily',
        timesPerDay: 1,
        reminderTimes: ['21:00'],
      });
    });

    it('waits for a save before asking for a name the user never typed, and says the check-in is off until a time is picked', async () => {
      const user = userEvent.setup();
      const fetch = mockFetch();
      renderPage();
      (await fetch.next('GET', '/api/habit-templates')).respond(200, [template]);

      await user.click(await screen.findByRole('button', { name: 'Something to let go of' }));
      expect(screen.getByLabelText('Name', { exact: true })).toHaveFocus();
      await user.click(screen.getByRole('radio', { name: 'Something to let go of' }));
      expect(screen.queryByText('Give your habit a short name.')).toBeNull();
      const checkIn = screen.getByLabelText(/Quiet evening check-in/);
      expect(checkIn).toHaveAccessibleDescription(/Off$/);

      fireEvent.change(checkIn, { target: { value: '21:00' } });
      expect(checkIn).not.toHaveAccessibleDescription(/Off$/);
      await user.click(screen.getByRole('button', { name: 'Create habit' }));
      expect(screen.getByText('Give your habit a short name.')).toBeInTheDocument();
    });

    it('offers social-media-free evenings as a let-go habit with its quiet check-in at 21:00', async () => {
      const user = userEvent.setup();
      const fetch = mockFetch();
      renderPage();
      (await fetch.next('GET', '/api/habit-templates')).respond(200, [template]);

      await user.click(await screen.findByRole('button', { name: /Social-media-free evenings/ }));
      expect(screen.getByRole('radio', { name: 'Something to let go of' })).toBeChecked();
      expect(screen.getByLabelText(/Quiet evening check-in/)).toHaveValue('21:00');
      await user.click(screen.getByRole('button', { name: 'Create habit' }));

      expect((await fetch.next('POST', '/api/habits')).body).toEqual({
        name: '🌙 Social-media-free evenings',
        kind: 'letGo',
        frequency: 'daily',
        timesPerDay: 1,
        reminderTimes: ['21:00'],
      });
    });

    it('does not send a kind for a build habit', async () => {
      const user = userEvent.setup();
      const fetch = mockFetch();
      renderPage();
      (await fetch.next('GET', '/api/habit-templates')).respond(200, []);

      await user.type(await screen.findByLabelText('Name', { exact: true }), 'Walk');
      await user.click(screen.getByRole('radio', { name: 'Something to let go of' }));
      await user.click(screen.getByRole('radio', { name: 'Something to build up' }));
      await user.click(screen.getByRole('button', { name: 'Create habit' }));

      expect((await fetch.next('POST', '/api/habits')).body).not.toHaveProperty('kind');
    });
  });

  it('creates a habit from scratch with the trimmed name and weekly target, then lands on /today', async () => {
    const user = userEvent.setup();
    const fetch = mockFetch();
    const router = renderPage();
    (await fetch.next('GET', '/api/habit-templates')).respond(200, [template]);

    await user.click(await screen.findByRole('button', { name: 'Start from scratch' }));
    const name = screen.getByLabelText('Name', { exact: true });
    expect(name).toHaveFocus();
    await user.type(name, '  Stretch  ');
    await user.click(screen.getByRole('radio', { name: 'A few times a week' }));
    await user.selectOptions(screen.getByLabelText('Times per week'), '4');
    fireEvent.change(screen.getByLabelText(/Reminder time/), { target: { value: '07:30' } });
    expect(screen.getByLabelText(/Reminder time/)).toHaveAccessibleDescription(
      "Ember reminds you at 7:30 AM on days you haven't checked in yet, until you've done it 4 times this week. Then it's quiet until Monday.",
    );
    await user.click(screen.getByRole('button', { name: 'Create habit' }));

    const create = await fetch.next('POST', '/api/habits');
    expect(create.body).toEqual({ name: 'Stretch', frequency: 'weekly', weeklyTarget: 4, reminderTimes: ['07:30'] });
    create.respond(201, { ...habit, name: 'Stretch' });

    expect(await screen.findByRole('heading', { name: 'Today' })).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/today');
  });

  it('skips to the form when suggestions fail to load, and asks for a name before sending anything', async () => {
    const user = userEvent.setup();
    const fetch = mockFetch();
    renderPage();
    (await fetch.next('GET', '/api/habit-templates')).respond(500);

    const name = await screen.findByLabelText('Name', { exact: true });
    expect(screen.queryByRole('button', { name: 'Back to suggestions' })).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Create habit' }));

    expect(screen.getByText('Give your habit a short name.')).toBeInTheDocument();
    expect(name).toHaveFocus();
    expect(fetch.all.some((call) => call.method === 'POST')).toBe(false);
  });
  it('shows how many characters the name has once it nears the limit', async () => {
    const fetch = mockFetch();
    renderPage();
    (await fetch.next('GET', '/api/habit-templates')).respond(500);
    const name = await screen.findByLabelText('Name', { exact: true });

    fireEvent.change(name, { target: { value: 'a'.repeat(79) } });
    expect(screen.queryByText(/of 100 characters/)).toBeNull();
    fireEvent.change(name, { target: { value: 'a'.repeat(80) } });
    expect(screen.getByText('80 of 100 characters')).toBeInTheDocument();
    expect(name).toHaveAccessibleDescription(expect.stringContaining('80 of 100 characters'));
  });

  it('creates a habit on set weekdays several times a day', async () => {
    const user = userEvent.setup();
    const fetch = mockFetch();
    renderPage();
    (await fetch.next('GET', '/api/habit-templates')).respond(200, []);

    await user.type(await screen.findByLabelText('Name', { exact: true }), 'Water');
    await user.click(screen.getByRole('radio', { name: 'On set days' }));
    for (const day of ['Monday', 'Tuesday', 'Wednesday', 'Thursday']) {
      await user.click(screen.getByRole('checkbox', { name: day }));
    }
    await user.selectOptions(screen.getByLabelText('Times a day'), '3');
    await user.click(screen.getByRole('button', { name: 'Create habit' }));

    expect((await fetch.next('POST', '/api/habits')).body).toEqual({
      name: 'Water',
      frequency: 'weekdays',
      days: ['mon', 'tue', 'wed', 'thu'],
      timesPerDay: 3,
    });
  });

  it('asks for at least one day before sending anything', async () => {
    const user = userEvent.setup();
    const fetch = mockFetch();
    renderPage();
    (await fetch.next('GET', '/api/habit-templates')).respond(200, []);

    await user.type(await screen.findByLabelText('Name', { exact: true }), 'Water');
    await user.click(screen.getByRole('radio', { name: 'On set days' }));
    for (const day of ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']) {
      expect(screen.getByRole('checkbox', { name: day })).not.toBeChecked();
    }
    await user.click(screen.getByRole('button', { name: 'Create habit' }));

    expect(screen.getByText('Pick at least one day.')).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Monday' })).toHaveFocus();
    expect(fetch.all.some((call) => call.method === 'POST')).toBe(false);
  });

  it('saves all seven days as every day and shows server field messages', async () => {
    const user = userEvent.setup();
    const fetch = mockFetch();
    renderPage();
    (await fetch.next('GET', '/api/habit-templates')).respond(200, []);

    await user.type(await screen.findByLabelText('Name', { exact: true }), 'Water');
    await user.click(screen.getByRole('radio', { name: 'On set days' }));
    for (const day of ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']) {
      await user.click(screen.getByRole('checkbox', { name: day }));
    }
    await user.click(screen.getByRole('button', { name: 'Create habit' }));

    const create = await fetch.next('POST', '/api/habits');
    expect(create.body).toEqual({ name: 'Water', frequency: 'daily', timesPerDay: 1 });
    create.respond(400, { type: 'validation', errors: { timesPerDay: ['Choose 1 to 10 times a day.'] } });
    expect(await screen.findByText('Choose 1 to 10 times a day.')).toBeInTheDocument();
  });

  it('sends one habit when the form is submitted twice in the same moment', async () => {
    const user = userEvent.setup();
    const fetch = mockFetch();
    renderPage();
    (await fetch.next('GET', '/api/habit-templates')).respond(200, [template]);

    await user.click(await screen.findByRole('button', { name: 'Start from scratch' }));
    await user.type(screen.getByLabelText('Name', { exact: true }), 'Stretch');
    const form = screen.getByRole('button', { name: 'Create habit' }).closest('form')!;
    act(() => {
      form.requestSubmit();
      form.requestSubmit();
    });

    await fetch.next('POST', '/api/habits');
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(fetch.all.filter((c) => c.method === 'POST')).toHaveLength(1);
  });

  it('says how reminders can reach an account that has no channel yet, linking Discord where push is not offered', async () => {
    const user = userEvent.setup();
    const fetch = mockFetch();
    renderPage();
    (await fetch.next('GET', '/api/habit-templates')).respond(200, [template]);
    (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, []);

    await user.click(await screen.findByRole('button', { name: /Walk/ }));

    expect(await screen.findByText(/Reminders reach you through notifications on this device, or Discord if you use it/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Turn one on in Settings.' })).toHaveAttribute('href', '/settings#discord');
  });

  it('offers no reminder time without an account on the web', async () => {
    localMode.mode.set('local');
    const user = userEvent.setup();
    const fetch = mockFetch();
    renderPage();
    (await fetch.next('GET', '/api/habit-templates')).respond(200, [template]);

    const card = await screen.findByRole('button', { name: /Walk/ });
    expect(card).not.toHaveTextContent('reminder at');
    await user.click(card);
    expect(screen.queryByLabelText(/Reminder time/)).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Create habit' }));

    expect((await fetch.next('POST', '/api/habits')).body).toEqual({ name: 'Walk', frequency: 'daily', timesPerDay: 1 });
  });
});
