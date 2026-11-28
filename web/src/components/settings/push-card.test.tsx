import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { env } from '@/lib/env';
import { reminderNames } from '@/lib/native/reminder-names';
import type { ReminderDestination } from '@/lib/models';
import { auth, deviceSync, push } from '@/lib/services';
import SettingsPage from '@/pages/settings';
import { openSettings, resetSettings, settle, stubSettingsMedia } from '@/test/settings-page';
import { me, mockFetch } from '@/test/test-support';
import { renderPage } from '@/test/test-utils';

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

const notificationPermission = vi.hoisted(() => ({ display: 'granted' }));
vi.mock('@capacitor/local-notifications', () => ({
  LocalNotifications: { checkPermissions: async () => ({ ...notificationPermission }) },
}));

describe('PushCard', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  describe('where Ember reminds you', () => {
    const discord: ReminderDestination = { id: 'discord', kind: 'discord', label: 'Discord DMs', enabled: true, current: false };
    const laptop: ReminderDestination = {
      id: 'browser-3',
      kind: 'browser',
      label: 'Firefox on Linux',
      enabled: true,
      current: true,
    };
    const phone: ReminderDestination = { id: 'phone-5', kind: 'phone', label: 'Pixel 8', enabled: false, current: false };

    afterEach(() => {
      vi.restoreAllMocks();
      enablePhone.mockReset();
    });

    it('lists every destination with its switch, marks this browser, and turns one off', async () => {
      const user = userEvent.setup();
      const { fetch } = await openSettings('/settings/reminders');
      (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, [discord, laptop, phone]);

      const here = await screen.findByRole('switch', { name: 'Firefox on Linux, This browser' });
      expect(here).toBeChecked();
      expect(screen.getByRole('switch', { name: 'Discord DMs' })).toBeChecked();
      expect(screen.getByRole('switch', { name: 'Pixel 8' })).not.toBeChecked();

      await user.click(here);
      const patch = await fetch.next('PATCH', '/api/me/reminder-destinations/browser-3');
      expect(patch.body).toEqual({ enabled: false });
      expect(here).not.toBeChecked();
      patch.respond(200, { ...laptop, enabled: false });

      expect(await screen.findByText('Saved.')).toBeInTheDocument();
      expect(here).not.toBeChecked();
      expect(screen.queryByRole('button', { name: 'Turn on reminders in this browser' })).toBeNull();
    });

    it('in a browser with reminders on, the names switch is off by default and saves names for this browser', async () => {
      const setShowNames = vi.spyOn(push, 'setShowNames').mockImplementation(async (on) => push.showNames.set(on));
      const user = userEvent.setup();
      const { fetch } = await openSettings('/settings/reminders');
      (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, [discord, laptop]);

      const names = await screen.findByRole('switch', { name: 'Show habit names in reminders' });
      expect(names).not.toBeChecked();
      expect(names).toHaveAccessibleDescription(/lock screen.*This device/);
      await user.click(names);

      expect(setShowNames).toHaveBeenCalledWith(true);
      expect(await screen.findByText('Saved.')).toBeInTheDocument();
      expect(names).toBeChecked();
      expect(reminderNames.get()).toBe(false);
      push.showNames.set(false);
    });

    it('offers no names switch for browser reminders while this browser is not a destination', async () => {
      const { fetch } = await openSettings('/settings/reminders');
      (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, [discord, { ...laptop, current: false }]);

      await screen.findByRole('switch', { name: 'Firefox on Linux' });
      expect(screen.queryByRole('switch', { name: 'Show habit names in reminders' })).toBeNull();
    });

    it('puts the switch back and says so gently when the change fails', async () => {
      const user = userEvent.setup();
      const { fetch } = await openSettings('/settings/reminders');
      (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, [discord]);

      await user.click(await screen.findByRole('switch', { name: 'Discord DMs' }));
      (await fetch.next('PATCH', '/api/me/reminder-destinations/discord')).respond(500, { type: 'internal' });

      expect(await screen.findByRole('switch', { name: 'Discord DMs' })).toBeChecked();
      expect(
        await within(screen.getByRole('region', { name: 'Where Ember reminds you' })).findByRole('alert'),
      ).toHaveTextContent("That didn't save, so Discord DMs is still on. Try again in a moment.");
    });

    it('saves two destinations at once and confirms each row', async () => {
      const user = userEvent.setup();
      const { fetch } = await openSettings('/settings/reminders');
      (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, [discord, laptop, phone]);

      await user.click(await screen.findByRole('switch', { name: 'Discord DMs' }));
      await user.click(screen.getByRole('switch', { name: 'Pixel 8' }));
      const first = await fetch.next('PATCH', '/api/me/reminder-destinations/discord');
      const second = await fetch.next('PATCH', '/api/me/reminder-destinations/phone-5');
      expect(second.body).toEqual({ enabled: true });

      second.respond(200, { ...phone, enabled: true });
      first.respond(200, { ...discord, enabled: false });

      await waitFor(() => expect(screen.getAllByText('Saved.')).toHaveLength(2));
      expect(screen.getByRole('switch', { name: 'Discord DMs' })).not.toBeChecked();
      expect(screen.getByRole('switch', { name: 'Pixel 8' })).toBeChecked();
    });

    it('turns reminders on in this browser when it is not listed yet, then shows its row', async () => {
      push.available.set(true);
      vi.spyOn(push, 'check').mockResolvedValue();
      const subscribe = vi.spyOn(push, 'subscribe').mockResolvedValue();
      const user = userEvent.setup();
      const { fetch } = await openSettings('/settings/reminders');
      (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, [discord]);

      await user.click(await screen.findByRole('button', { name: 'Turn on reminders in this browser' }));
      expect(subscribe).toHaveBeenCalledTimes(1);
      (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, [discord, laptop]);

      expect(await screen.findByRole('switch', { name: 'Firefox on Linux, This browser' })).toBeChecked();
      expect(screen.queryByRole('button', { name: 'Turn on reminders in this browser' })).toBeNull();
    });

    it('in the app, turns this phone on and rebuilds its reminders, or says notifications are off when refused', async () => {
      env.native = true;
      const rebuild = vi.spyOn(deviceSync, 'rebuildReminders').mockImplementation(() => undefined);
      enablePhone.mockResolvedValueOnce(null);
      const user = userEvent.setup();
      const { fetch } = await openSettings('/settings/reminders');
      (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, [discord]);

      await user.click(await screen.findByRole('button', { name: 'Turn on reminders on this phone' }));
      expect(
        await screen.findByText("Notifications are off for Ember on this phone. You can turn them on in your phone's settings."),
      ).toBeInTheDocument();
      rebuild.mockClear();

      const mine = { ...phone, enabled: true, current: true };
      enablePhone.mockResolvedValueOnce(mine);
      await user.click(screen.getByRole('button', { name: 'Turn on reminders on this phone' }));
      (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, [discord, mine]);

      expect(await screen.findByRole('switch', { name: 'Pixel 8, This phone' })).toBeChecked();
      expect(rebuild).toHaveBeenCalled();
    });

    it('in the app, switching this phone off rebuilds its reminders', async () => {
      env.native = true;
      const rebuild = vi.spyOn(deviceSync, 'rebuildReminders').mockImplementation(() => undefined);
      const mine = { ...phone, enabled: true, current: true };
      const user = userEvent.setup();
      const { fetch } = await openSettings('/settings/reminders');
      (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, [mine]);

      await user.click(await screen.findByRole('switch', { name: 'Pixel 8, This phone' }));
      rebuild.mockClear();
      (await fetch.next('PATCH', '/api/me/reminder-destinations/phone-5')).respond(200, { ...mine, enabled: false });

      await waitFor(() => expect(rebuild).toHaveBeenCalled());
      expect(screen.getByRole('switch', { name: 'Pixel 8, This phone' })).not.toBeChecked();
    });

    it('in the app, switching Discord off rebuilds this phone\'s reminders too', async () => {
      env.native = true;
      const rebuild = vi.spyOn(deviceSync, 'rebuildReminders').mockImplementation(() => undefined);
      const user = userEvent.setup();
      const { fetch } = await openSettings('/settings/reminders');
      (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, [discord, { ...phone, current: true }]);

      await user.click(await screen.findByRole('switch', { name: 'Discord DMs' }));
      rebuild.mockClear();
      (await fetch.next('PATCH', '/api/me/reminder-destinations/discord')).respond(200, { ...discord, enabled: false });

      await waitFor(() => expect(rebuild).toHaveBeenCalled());
    });
  });

  it('offers no browser reminders when the server answers 501 for the push key', async () => {
    const registration = { pushManager: { getSubscription: async () => null } };
    vi.stubGlobal('PushManager', class {});
    Object.defineProperty(navigator, 'serviceWorker', {
      configurable: true,
      value: { getRegistration: async () => registration, ready: Promise.resolve(registration) },
    });
    const { fetch } = await openSettings('/settings/reminders');

    (await fetch.next('GET', '/api/push/vapid-public-key')).respond(501);
    (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, []);
    await settle();

    expect(await screen.findByText(/Nothing can remind you yet/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Turn on reminders in this browser' })).toBeNull();
    expect(screen.getByRole('combobox', { name: 'Second reminder' })).toBeInTheDocument();
  });

  it('says paused Discord DM reminders inside this card and resumes them', async () => {
    const user = userEvent.setup();
    const paused = { ...me, dmReminders: { paused: true } };
    auth.setMe(paused);
    const fetch = mockFetch();
    renderPage('/settings/:group?', SettingsPage, '/settings/reminders');
    (await fetch.next('GET', '/api/me')).respond(200, paused);
    (await fetch.next('GET', '/api/me/reminder-destinations')).respond(200, []);

    const card = within(await screen.findByRole('region', { name: 'Where Ember reminds you' }));
    expect(card.getByText(/Ember paused your Discord DM reminders/)).toBeInTheDocument();
    await user.click(card.getByRole('button', { name: 'Resume DM reminders' }));
    (await fetch.next('POST', '/api/me/dm-reminders/resume')).respond(204);
    (await fetch.next('GET', '/api/me')).respond(200, me);

    expect(await card.findByText('DM reminders are back on.')).toBeInTheDocument();
    expect(card.queryByText(/Ember paused your Discord DM reminders/)).toBeNull();
    expect(card.queryByRole('button', { name: 'Resume DM reminders' })).toBeNull();
  });
});
