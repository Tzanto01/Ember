import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { env } from '@/lib/env';
import { reminderNames } from '@/lib/native/reminder-names';
import { openSettings, resetSettings, stubSettingsMedia } from '@/test/settings-page';

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

describe('PhoneRemindersRow', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    exactAlarms.beta = false;
    resetSettings();
    notificationPermission.display = 'granted';
  });

  it('on the phone, says how to turn notifications back on when they are off for Ember', async () => {
    env.native = true;
    notificationPermission.display = 'denied';
    await openSettings('/settings/reminders');

    expect(
      await screen.findByText("Notifications are off for Ember on this phone. You can turn them on in your phone's settings."),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Allow on-time reminders' })).toBeNull();
  });

  it('in the beta app, offers on-time reminders while exact alarms are off, and hides the offer once allowed', async () => {
    env.native = true;
    exactAlarms.beta = true;
    exactAlarms.allowed = false;
    exactAlarms.allowExact.mockResolvedValue(true);
    const user = userEvent.setup();
    await openSettings('/settings/reminders');

    await user.click(await screen.findByRole('button', { name: 'Allow on-time reminders' }));

    expect(exactAlarms.allowExact).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Allow on-time reminders' })).toBeNull());
  });

  it('on the phone, the reminder names switch is off by default and turns the per-device setting on', async () => {
    env.native = true;
    const user = userEvent.setup();
    await openSettings('/settings/reminders');

    const names = screen.getByRole('switch', { name: 'Show habit names in reminders' });
    expect(names).not.toBeChecked();
    expect(names).toHaveAccessibleDescription(/lock screen.*This device/);
    await user.click(names);

    expect(reminderNames.get()).toBe(true);
    expect(localStorage.getItem('ember.reminderNames')).toBe('1');
    expect(names).toBeChecked();
  });
});
