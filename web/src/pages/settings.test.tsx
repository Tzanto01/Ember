import { act, screen, waitFor, within } from '@testing-library/react';
import { Capacitor } from '@capacitor/core';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { env } from '@/lib/env';
import { renderPage } from '@/test/test-utils';
import { auth, localMode, push } from '@/lib/services';
import { me, mockFetch } from '@/test/test-support';
import { openPatches, openSettings, resetSettings, setWide, settingsPath, settle, stubSettingsMedia } from '@/test/settings-page';
import SettingsPage from './settings';

vi.mock('@/lib/env', async (original) => ({ env: { ...(await original<typeof import('@/lib/env')>()).env } }));
const exactAlarms = vi.hoisted(() => ({ beta: false, allowed: false, allowExact: vi.fn() }));
const enablePhone = vi.hoisted(() => vi.fn());
vi.mock('@/lib/native/updater', async (original) => ({
  ...(await original<typeof import('@/lib/native/updater')>()),
  updaterAvailable: () => exactAlarms.beta,
}));
vi.mock('@/lib/services', async (original) => {
  const { Store } = await import('@/lib/store');
  return {
    ...(await original<typeof import('@/lib/services')>()),
    localMode: { mode: new Store('account'), evicted: new Store(false) },
    reminders: {
      exactAllowed: async () => exactAlarms.allowed,
      allowExact: (...args: unknown[]) => exactAlarms.allowExact(...args),
      enablePhone: () => enablePhone(),
    },
  };
});

describe('SettingsPage', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    exactAlarms.beta = false;
    vi.restoreAllMocks();
    resetSettings();
  });

  it('Account & sign-in starts with who is signed in, then two-step and Discord, and sends a change-password link', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/account');

    const cards = document.querySelectorAll('section');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Account & sign-in');
    expect([...cards].map((card) => card.getAttribute('id'))).toEqual(['account', 'two-factor', 'discord']);
    expect(cards[0]).toHaveAccessibleName('Account');
    const account = within(cards[0] as HTMLElement);
    expect(account.getByText(/Signed in as/)).toHaveTextContent('Signed in as a@example.test');
    expect(account.queryByRole('link', { name: 'Privacy' })).toBeNull();

    await user.click(account.getByRole('button', { name: 'Change password' }));
    const sent = await fetch.next('POST', '/api/auth/forgotPassword');
    expect(sent.body).toEqual({ email: 'a@example.test' });
    sent.respond(200);
    expect(await account.findByText(/We sent a link to a@example.test/)).toBeInTheDocument();
  });

  it("in the beta Android app, Feedback & about holds the tech report and App updates, then What's new and About", async () => {
    exactAlarms.beta = true;
    vi.spyOn(Capacitor, 'getPlatform').mockReturnValue('android');
    env.native = true;
    await openSettings('/settings/support');

    const cards = document.querySelectorAll('section');
    expect([...cards].map((card) => card.getAttribute('id'))).toEqual(['feedback', 'diagnostics', 'app-updates']);
    expect(within(cards[1] as HTMLElement).getByRole('button', { name: 'Look at the report' })).toBeInTheDocument();
    expect(within(cards[2] as HTMLElement).getByRole('button', { name: 'Check for updates' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: "What's new" })).toHaveAttribute('href', '/whats-new');
    expect(screen.getByRole('link', { name: 'About Ember' })).toHaveAttribute('href', '/about');
  });

  it('opens the group of an older /settings#push link, scrolls to that card and focuses its heading', async () => {
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;
    const frames = new Map<number, FrameRequestCallback>();
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => frames.set(frames.size + 1, callback).size);
    vi.stubGlobal('cancelAnimationFrame', (id: number) => frames.delete(id));
    push.available.set(true);
    auth.setMe(me);
    const fetch = mockFetch();
    const router = renderPage('/settings/:group?', SettingsPage, '/settings#push');
    const heading = await screen.findByRole('heading', { name: 'Where Ember reminds you' });
    (await fetch.next('GET', '/api/me')).respond(200, { ...me });
    await settle();

    expect(settingsPath(router)).toBe('/settings/reminders#push');
    expect(router.state.historyAction).toBe('REPLACE');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Reminders & sounds');
    expect(screen.queryByRole('combobox', { name: 'Grace days per week' })).toBeNull();
    frames.forEach((callback) => callback(0));
    expect(heading).toHaveFocus();
    expect(scrollIntoView.mock.contexts[0]).toBe(document.getElementById('push'));
  });

  it.each([
    ['/settings/habits#habit-notes', 'habit-notes', () => screen.getByText('Offer a note after check-in')],
    ['/settings/support#about', 'about', () => screen.getByRole('link', { name: 'About Ember' })],
  ])('opens %s, scrolls to that row and focuses it', async (url, id, target) => {
    const scrollIntoView = vi.fn();
    Element.prototype.scrollIntoView = scrollIntoView;
    const frames = new Map<number, FrameRequestCallback>();
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => frames.set(frames.size + 1, callback).size);
    vi.stubGlobal('cancelAnimationFrame', (id: number) => frames.delete(id));
    await openSettings(url);
    await settle();

    frames.forEach((callback) => callback(0));
    expect(target()).toHaveFocus();
    expect(scrollIntoView.mock.contexts[0]).toBe(document.getElementById(id));
  });

  it.each([
    ['/settings/habits#timezone', '/settings/language#timezone', 'Timezone'],
    ['/settings/privacy#journal', '/settings/journal#journal', 'Private journal'],
    ['/settings/habits#moods', '/settings/journal#moods', 'Moods'],
  ])('opens the group that now holds the card of an older link %s', async (url, target, card) => {
    const { router } = await openSettings(url);

    await waitFor(() => expect(settingsPath(router)).toBe(target));
    expect(router.state.historyAction).toBe('REPLACE');
    expect(screen.getByRole('region', { name: card })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Grace days' })).toBeNull();
  });

  it.each(['/settings#dm-reminders', '/settings/reminders#dm-reminders'])(
    'sends an older %s link to the push card that took its place and focuses its heading',
    async (url) => {
      Element.prototype.scrollIntoView = vi.fn();
      const frames = new Map<number, FrameRequestCallback>();
      vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => frames.set(frames.size + 1, callback).size);
      vi.stubGlobal('cancelAnimationFrame', (id: number) => frames.delete(id));
      const { router } = await openSettings(url);

      await waitFor(() => expect(settingsPath(router)).toBe('/settings/reminders#push'));
      expect(router.state.historyAction).toBe('REPLACE');
      await settle();
      frames.forEach((callback) => callback(0));
      expect(screen.getByRole('heading', { name: 'Where Ember reminds you' })).toHaveFocus();
    },
  );

  it('sends the older Send feedback address to Feedback & about, keeping its query and hash', async () => {
    const { router } = await openSettings('/settings/feedback?from=today#top');

    await waitFor(() => expect(settingsPath(router)).toBe('/settings/support?from=today#top'));
    expect(router.state.historyAction).toBe('REPLACE');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Feedback & about');
  });

  it('on a phone, lists the groups, opens one, and goes back to the list through history', async () => {
    const user = userEvent.setup();
    const { router } = await openSettings('/settings');

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Settings');
    expect(document.querySelectorAll('section')).toHaveLength(0);
    expect(screen.queryByRole('link', { name: 'Back to Settings' })).toBeNull();
    const list = within(screen.getByRole('navigation', { name: 'Settings groups' }));
    expect(list.getAllByRole('heading', { level: 2 }).map((heading) => heading.textContent)).toEqual([
      'You',
      'Ember',
      'Support',
    ]);
    expect(within(list.getByRole('list', { name: 'You' })).getAllByRole('link').map((link) => link.textContent)).toEqual([
      expect.stringContaining('Account & sign-in'),
      expect.stringContaining('Language & time'),
      expect.stringContaining('Privacy & data'),
    ]);
    const reminders = list.getByRole('link', { name: 'Reminders & sounds' });
    expect(reminders).toHaveAccessibleDescription('Where and when Ember nudges you');

    await user.click(reminders);
    expect(router.state.location.pathname).toBe('/settings/reminders');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Reminders & sounds');
    expect(screen.getByRole('combobox', { name: 'Second reminder' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Grace days' })).toBeNull();
    expect(screen.queryByRole('navigation', { name: 'Settings' })).toBeNull();

    await user.click(screen.getByRole('link', { name: 'Back to Settings' }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/settings'));
    expect(router.state.historyAction).toBe('POP');
    expect(screen.getByRole('navigation', { name: 'Settings groups' })).toBeInTheDocument();
  });

  it('on a phone, a group opened directly goes back to the list without leaving Settings', async () => {
    const user = userEvent.setup();
    const { router } = await openSettings('/settings/habits');

    await user.click(screen.getByRole('link', { name: 'Back to Settings' }));
    expect(router.state.location.pathname).toBe('/settings');
    expect(router.state.historyAction).toBe('PUSH');
  });

  it('from md, docks the groups in a side rail, opens the first one and marks the current group', async () => {
    setWide(true);
    const user = userEvent.setup();
    const { router } = await openSettings('/settings');

    await waitFor(() => expect(router.state.location.pathname).toBe('/settings/account'));
    const rail = within(screen.getByRole('navigation', { name: 'Settings' }));
    expect(rail.getByRole('heading', { level: 2, name: 'Settings' })).toBeInTheDocument();
    expect(rail.getAllByRole('heading', { level: 3 }).map((heading) => heading.textContent)).toEqual([
      'You',
      'Ember',
      'Support',
    ]);
    expect(within(rail.getByRole('list', { name: 'Support' })).getAllByRole('link').map((link) => link.textContent)).toEqual(
      ['Feedback & about'],
    );
    expect(rail.getAllByRole('link').at(-1)).toHaveTextContent('Help right now');
    expect(rail.getByRole('link', { name: 'Account & sign-in' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('region', { name: 'Account' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Back to Settings' })).toBeNull();

    await user.click(rail.getByRole('link', { name: 'Privacy & data' }));
    expect(router.state.location.pathname).toBe('/settings/privacy');
    expect(rail.getByRole('link', { name: 'Privacy & data' })).toHaveAttribute('aria-current', 'page');
    expect(rail.getByRole('link', { name: 'Account & sign-in' })).not.toHaveAttribute('aria-current');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Privacy & data');
    expect([...document.querySelectorAll('section')].map((card) => card.getAttribute('id'))).toEqual([
      'consent',
      'export',
      'delete',
    ]);
  });

  const signedIn = [
    ['/settings/account', 'Password, two-step sign-in, Discord'],
    ['/settings/language', 'Language, your time zone'],
    ['/settings/privacy', 'Consent, download, delete'],
    ['/settings/reminders', 'Where and when Ember nudges you'],
    ['/settings/habits', 'Streaks, grace days, notes, routines'],
    ['/settings/journal', 'Private journal, moods, what helps you'],
    ['/settings/appearance', 'Light or dark, theme, calm mode'],
    ['/settings/people', 'Share your fire with people you choose'],
    ['/settings/support', "Send feedback, what's new, app version"],
    ['/settings/help', 'Crisis lines, any time of day'],
  ];

  it.each([
    ['without an account on the web', 'local', false, [
      ['/settings/account', 'Backups and an account of your own'],
      ['/settings/language', 'Language, your time zone'],
      ['/settings/privacy', 'Delete everything on this device'],
      ['/settings/reminders', 'The sound when a timer ends'],
      ['/settings/habits', 'Streaks, grace days, notes, routines'],
      ['/settings/journal', 'Moods and what helps you'],
      ['/settings/appearance', 'Light or dark, theme, calm mode'],
      ['/settings/support', "Send feedback, what's new, app version"],
      ['/settings/help', 'Crisis lines, any time of day'],
    ]],
    ['without an account in the Android app', 'local', true, [
      ['/settings/account', 'Backups and an account of your own'],
      ['/settings/language', 'Language, your time zone'],
      ['/settings/privacy', 'Delete everything on this device'],
      ['/settings/reminders', 'Reminders on this phone, timer sound'],
      ['/settings/habits', 'Streaks, grace days, notes, routines'],
      ['/settings/journal', 'Moods and what helps you'],
      ['/settings/appearance', 'Light or dark, theme, calm mode'],
      ['/settings/support', "Send feedback, what's new, app version"],
      ['/settings/help', 'Crisis lines, any time of day'],
    ]],
    ['signed in on the web', 'account', false, signedIn],
    ['signed in in the beta Android app', 'account', true, signedIn],
  ] as const)('lists the groups %s with their summaries, Help right now last and apart', async (_, mode, native, expected) => {
    localMode.mode.set(mode);
    if (native) {
      exactAlarms.beta = true;
      vi.spyOn(Capacitor, 'getPlatform').mockReturnValue('android');
      env.native = true;
    }
    await openSettings('/settings');

    const nav = screen.getByRole('navigation', { name: 'Settings groups' });
    const links = within(nav).getAllByRole('link');
    expect(links.map((link) => [link.getAttribute('href'), document.getElementById(link.getAttribute('aria-describedby') ?? '')?.textContent])).toEqual(expected);
    const help = links.at(-1) as HTMLElement;
    expect(help.closest('ul')).not.toHaveAccessibleName();
  });

  const habitRows = ['streaks', 'grace', 'habit-notes', 'routines'];
  const signedInCards = {
    account: ['account', 'two-factor', 'discord'],
    language: ['language', 'timezone'],
    privacy: ['consent', 'export', 'delete'],
    reminders: ['push', 'second-reminder', 'timer'],
    habits: habitRows,
    journal: ['journal', 'moods', 'helps', 'moments'],
    appearance: ['appearance', 'calm', 'this-device'],
    people: ['fireside'],
    support: ['feedback', 'whats-new', 'about'],
    help: ['help'],
  };
  const localCards = {
    account: ['device'],
    language: ['language', 'timezone'],
    privacy: ['delete'],
    reminders: ['timer'],
    habits: habitRows,
    journal: ['moods', 'helps'],
    appearance: ['appearance', 'calm', 'this-device'],
    support: ['feedback', 'whats-new', 'about'],
    help: ['help'],
  };

  it.each([
    ['without an account on the web', 'local', false, false, localCards],
    ['without an account in the app', 'local', true, false, {
      ...localCards,
      reminders: ['phone-reminders', 'second-reminder', 'timer'],
      appearance: ['appearance', 'calm', 'this-device', 'gentle'],
    }],
    ['signed in on the web', 'account', false, false, signedInCards],
    ['signed in in the beta Android app', 'account', true, true, {
      ...signedInCards,
      reminders: ['push', 'phone-reminders', 'second-reminder', 'timer'],
      appearance: ['appearance', 'calm', 'this-device', 'gentle'],
      support: ['feedback', 'diagnostics', 'app-updates', 'whats-new', 'about'],
    }],
  ] as const)('%s, every group holds its cards and rows in order', async (_, mode, native, beta, expected) => {
    localMode.mode.set(mode);
    env.native = native;
    if (beta) {
      exactAlarms.beta = true;
      vi.spyOn(Capacitor, 'getPlatform').mockReturnValue('android');
    }
    const { router } = await openSettings('/settings');
    const slugs = within(screen.getByRole('navigation', { name: 'Settings groups' }))
      .getAllByRole('link')
      .map((link) => link.getAttribute('href')?.replace('/settings/', ''));
    expect(slugs).toEqual(Object.keys(expected));

    const shown: Record<string, string[]> = {};
    for (const slug of Object.keys(expected)) {
      await act(() => router.navigate(`/settings/${slug}`));
      shown[slug] = [...document.querySelectorAll('[id]')]
        .map((element) => element.id)
        .filter((id) => document.getElementById(`${id}-heading`));
    }
    expect(shown).toEqual(expected);
  });

  it('without an account, sends the Fireside address back to the list', async () => {
    localMode.mode.set('local');
    const { router } = await openSettings('/settings/people');

    await waitFor(() => expect(router.state.location.pathname).toBe('/settings'));
    expect(screen.getByRole('navigation', { name: 'Settings groups' })).toBeInTheDocument();
  });

  it('sends quick successive grace changes one PATCH at a time, in order', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/habits');
    const select = screen.getByLabelText('Grace days per week');

    await user.selectOptions(select, '2 days a week');
    await user.selectOptions(select, '3 days a week');

    const first = await fetch.next('PATCH', '/api/me');
    expect(first.body).toEqual({ graceDaysPerWeek: 2 });
    await settle();
    expect(openPatches(fetch)).toHaveLength(0);
    first.respond(200, { ...me, graceDaysPerWeek: 2 });

    const second = await fetch.next('PATCH', '/api/me');
    expect(second.body).toEqual({ graceDaysPerWeek: 3 });
    second.respond(200, { ...me, graceDaysPerWeek: 3 });

    await waitFor(() => expect(select).toHaveDisplayValue('3 days a week'));
    expect(screen.getByText('Saved.')).toBeInTheDocument();
  });

  it('Help right now shows the crisis lines and sends nothing', async () => {
    const { fetch, router } = await openSettings('/settings/help');

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Help right now');
    expect(settingsPath(router)).toBe('/settings/help');
    const lines = within(screen.getByRole('region', { name: 'Crisis lines' }));
    expect(lines.getByRole('list', { name: 'Crisis lines' })).toBeInTheDocument();
    expect(lines.getAllByRole('link').some((link) => link.getAttribute('href')?.startsWith('tel:'))).toBe(true);
    expect(lines.getByText("Ember isn't a crisis service and doesn't read what you write.")).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'About Ember' })).toBeNull();
    await settle();
    expect(fetch.pending).toHaveLength(0);
  });

  it('keeps Help right now to the crisis lines in the Android app', async () => {
    vi.spyOn(Capacitor, 'getPlatform').mockReturnValue('android');
    env.native = true;
    await openSettings('/settings/help');

    expect([...document.querySelectorAll('section')].map((section) => section.id)).toEqual(['help']);
  });

  it('leaves Send a tech report out on the web', async () => {
    vi.spyOn(Capacitor, 'getPlatform').mockReturnValue('android');
    await openSettings('/settings/support');

    expect([...document.querySelectorAll('section')].map((section) => section.id)).toEqual(['feedback']);
  });

  it('waits for the grace PATCH before sending the theme one, across groups', async () => {
    setWide(true);
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/habits');

    await user.selectOptions(screen.getByLabelText('Grace days per week'), '2 days a week');
    await user.click(screen.getByRole('link', { name: 'Look & feel' }));
    await user.click(screen.getByRole('radio', { name: 'Fieldnotes' }));

    const first = await fetch.next('PATCH', '/api/me');
    expect(first.body).toEqual({ graceDaysPerWeek: 2 });
    await settle();
    expect(openPatches(fetch)).toHaveLength(0);
    first.respond(200, { ...me, graceDaysPerWeek: 2 });

    const second = await fetch.next('PATCH', '/api/me');
    expect(second.body).toEqual({ theme: 'fieldnotes' });
    second.respond(200, { ...me, graceDaysPerWeek: 2, theme: 'fieldnotes' });

    await waitFor(() => expect(auth.me.get()?.theme).toBe('fieldnotes'));
    await user.click(screen.getByRole('link', { name: 'Habits & streaks' }));
    expect(screen.getByLabelText('Grace days per week')).toHaveDisplayValue('2 days a week');
    expect(screen.queryByText(/leaderboard/i)).not.toBeInTheDocument();
  });

  it('shows the Discord linked notice in its group, reloads me and drops the query from the address', async () => {
    auth.setMe(me);
    const fetch = mockFetch();
    const router = renderPage('/settings/:group?', SettingsPage, '/settings?discord=linked');

    const notice = await screen.findByText('Discord is linked. You can sign in with it from now on.');
    expect(notice.closest('[role="status"]')).not.toBeNull();
    (await fetch.next('GET', '/api/auth/antiforgery')).respond(204);
    (await fetch.next('GET', '/api/me')).respond(200, { ...me, discord: { linked: true, id: '42' } });

    expect(await screen.findByRole('button', { name: 'Unlink Discord' })).toBeInTheDocument();
    expect(settingsPath(router)).toBe('/settings/account');
    expect(notice).toBeInTheDocument();
  });

  it('without an account, shows the device card first and no sign-out, Discord, two-step or private journal', async () => {
    localMode.mode.set('local');
    const { router } = await openSettings('/settings/account');

    const cards = document.querySelectorAll('section');
    expect(cards).toHaveLength(1);
    expect(cards[0]).toHaveAccessibleName('On this device');
    expect(within(cards[0] as HTMLElement).getByText(/Browsers can delete site data/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Export backup' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create an account and keep my data' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign out' })).toBeNull();

    await act(() => router.navigate('/settings/privacy'));
    expect(screen.getByRole('button', { name: 'Delete everything on this device' })).toBeInTheDocument();
    for (const name of ['Discord', 'Two-step sign-in', 'Private journal', 'Privacy', 'Your data', 'Delete your account']) {
      expect(screen.queryByRole('heading', { name })).toBeNull();
    }
    expect(screen.queryByRole('button', { name: 'Delete my account' })).toBeNull();

    await act(() => router.navigate('/settings/language'));
    expect(screen.getByRole('heading', { name: 'Timezone' })).toBeInTheDocument();
  });

  it('sends feedback from Feedback & about with an account', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/support');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Feedback & about');
    await user.type(screen.getByRole('textbox', { name: /Title/ }), 'Hi');
    await user.type(screen.getByRole('textbox', { name: /Description/ }), 'Hello');
    await user.click(screen.getByRole('button', { name: 'Send' }));
    (await fetch.next('POST', '/api/feedback')).respond(204);
    await screen.findByText('Thank you, that really helps.');
  });

  it('shows the feedback form without an account too', async () => {
    localMode.mode.set('local');
    await openSettings('/settings/support');

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Feedback & about');
    expect(screen.getByRole('textbox', { name: /Description/ })).toBeInTheDocument();
  });

  it('links to routines', async () => {
    const user = userEvent.setup();
    const { router } = await openSettings('/settings/habits');

    await user.click(screen.getByRole('link', { name: 'Routines' }));
    expect(router.state.location.pathname).toBe('/routines');
  });
  it('claims this browser for the account on entry, before listing reminders', async () => {
    const subscription = { toJSON: () => ({ endpoint: 'https://push.example/1', keys: { p256dh: 'p256', auth: 'secret' } }) };
    const registration = { pushManager: { getSubscription: async () => subscription } };
    vi.stubGlobal('PushManager', class {});
    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { getRegistration: async () => registration, ready: Promise.resolve(registration) },
    });
    const { fetch } = await openSettings('/settings/reminders');

    (await fetch.next('GET', '/api/push/vapid-public-key')).respond(200, { key: 'BAAA' });
    const claim = await fetch.next('POST', '/api/me/push-subscriptions');
    expect(claim.body).toMatchObject({ endpoint: 'https://push.example/1' });
    await settle();
    expect(fetch.pending.some((call) => call.url === '/api/me/reminder-destinations')).toBe(false);
    claim.respond(204);
    (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, []);
  });

  it('claims no browser when entered without an account, not even once the device data starts moving into one', async () => {
    const getRegistration = vi.fn(async () => ({ pushManager: { getSubscription: async () => null } }));
    vi.stubGlobal('PushManager', class {});
    Object.defineProperty(navigator, 'serviceWorker', { configurable: true, value: { getRegistration } });
    localMode.mode.set('local');
    await openSettings('/settings/account');

    act(() => localMode.mode.set('upgrading'));
    await settle();
    expect(getRegistration).not.toHaveBeenCalled();
  });
});
