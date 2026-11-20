import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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
    resetSettings();
  });

  it('Account & security starts with who is signed in, then two-step and Discord, and sends a change-password link', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/account');

    const cards = document.querySelectorAll('section');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Account & security');
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

  it('in the beta app, Account & security ends with App updates and its check button', async () => {
    exactAlarms.beta = true;
    await openSettings('/settings/account');

    const cards = document.querySelectorAll('section');
    expect([...cards].map((card) => card.getAttribute('id'))).toEqual(['account', 'two-factor', 'discord', 'app-updates']);
    expect(within(cards[3] as HTMLElement).getByRole('button', { name: 'Check for updates' })).toBeInTheDocument();
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
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Reminders');
    expect(screen.queryByRole('heading', { name: 'Grace days' })).toBeNull();
    frames.forEach((callback) => callback(0));
    expect(heading).toHaveFocus();
    expect(scrollIntoView.mock.contexts[0]).toBe(document.getElementById('push'));
  });

  it('opens the group holding the card in the hash, even from another group', async () => {
    const { router } = await openSettings('/settings/habits#journal');

    await waitFor(() => expect(settingsPath(router)).toBe('/settings/privacy#journal'));
    expect(screen.getByRole('region', { name: 'Private journal' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Grace days' })).toBeNull();
  });

  it('on a phone, lists the groups, opens one, and goes back to the list through history', async () => {
    const user = userEvent.setup();
    const { router } = await openSettings('/settings');

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Settings');
    expect(document.querySelectorAll('section')).toHaveLength(0);
    expect(screen.queryByRole('link', { name: 'Back to Settings' })).toBeNull();
    const list = within(screen.getByRole('navigation', { name: 'Settings groups' }));
    expect(list.getAllByRole('link').map((link) => link.textContent)).toEqual([
      expect.stringContaining('Account & security'),
      expect.stringContaining('Reminders'),
      expect.stringContaining('Habits & days'),
      expect.stringContaining('People'),
      expect.stringContaining('Appearance'),
      expect.stringContaining('Privacy & data'),
      expect.stringContaining('Send feedback'),
      expect.stringContaining('Help right now'),
      expect.stringContaining("What's new"),
    ]);
    const reminders = list.getByRole('link', { name: 'Reminders' });
    expect(reminders).toHaveAccessibleDescription('Where Ember reminds you · Second reminder');

    await user.click(reminders);
    expect(router.state.location.pathname).toBe('/settings/reminders');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Reminders');
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
    expect(rail.getAllByRole('link').map((link) => link.textContent)).toEqual([
      'Account & security',
      'Reminders',
      'Habits & days',
      'People',
      'Appearance',
      'Privacy & data',
      'Send feedback',
      'Help right now',
      "What's new",
    ]);
    expect(rail.getByRole('link', { name: 'Account & security' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('region', { name: 'Account' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Back to Settings' })).toBeNull();

    await user.click(rail.getByRole('link', { name: 'Privacy & data' }));
    expect(router.state.location.pathname).toBe('/settings/privacy');
    expect(rail.getByRole('link', { name: 'Privacy & data' })).toHaveAttribute('aria-current', 'page');
    expect(rail.getByRole('link', { name: 'Account & security' })).not.toHaveAttribute('aria-current');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Privacy & data');
    expect([...document.querySelectorAll('section')].map((card) => card.getAttribute('id'))).toEqual([
      'journal',
      'moments',
      'helps',
      'consent',
      'export',
      'delete',
    ]);
  });

  it('without an account, leaves out the Reminders group and sends its address back to the list', async () => {
    localMode.mode.set('local');
    const { router } = await openSettings('/settings');

    const list = within(screen.getByRole('navigation', { name: 'Settings groups' }));
    expect(list.getAllByRole('link').map((link) => link.getAttribute('href'))).toEqual([
      '/settings/account',
      '/settings/habits',
      '/settings/appearance',
      '/settings/privacy',
      '/settings/feedback',
      '/settings/help',
      '/whats-new',
    ]);
    expect(list.getByRole('link', { name: 'Account & security' })).toHaveAccessibleDescription('On this device');

    await act(() => router.navigate('/settings/reminders'));
    await waitFor(() => expect(router.state.location.pathname).toBe('/settings'));
    expect(screen.queryByRole('combobox', { name: 'Second reminder' })).toBeNull();
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
    const { fetch } = await openSettings('/settings/help');

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Help right now');
    const lines = within(screen.getByRole('region', { name: 'Crisis lines' }));
    expect(lines.getByRole('list', { name: 'Crisis lines' })).toBeInTheDocument();
    expect(lines.getAllByRole('link').some((link) => link.getAttribute('href')?.startsWith('tel:'))).toBe(true);
    expect(lines.getByText("Ember isn't a crisis service and doesn't read what you write.")).toBeInTheDocument();
    await settle();
    expect(fetch.pending).toHaveLength(0);
  });

  it('waits for the grace PATCH before sending the theme one, across groups', async () => {
    setWide(true);
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/habits');

    await user.selectOptions(screen.getByLabelText('Grace days per week'), '2 days a week');
    await user.click(screen.getByRole('link', { name: 'Appearance' }));
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
    await user.click(screen.getByRole('link', { name: 'Habits & days' }));
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

    await act(() => router.navigate('/settings/habits'));
    expect(screen.getByRole('heading', { name: 'Timezone' })).toBeInTheDocument();
  });

  it('sends feedback from its own Settings group with an account', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/feedback');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Send feedback');
    await user.type(screen.getByRole('textbox', { name: /Title/ }), 'Hi');
    await user.type(screen.getByRole('textbox', { name: /Description/ }), 'Hello');
    await user.click(screen.getByRole('button', { name: 'Send' }));
    (await fetch.next('POST', '/api/feedback')).respond(204);
    await screen.findByText('Thank you, that really helps.');
  });

  it('shows the feedback form without an account too', async () => {
    localMode.mode.set('local');
    await openSettings('/settings/feedback');

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Send feedback');
    expect(screen.getByRole('textbox', { name: /Description/ })).toBeInTheDocument();
  });

  it('links to routines', async () => {
    const user = userEvent.setup();
    const { router } = await openSettings('/settings/habits');

    await user.click(screen.getByRole('link', { name: 'Your routines' }));
    expect(router.state.location.pathname).toBe('/routines');
  });
});
