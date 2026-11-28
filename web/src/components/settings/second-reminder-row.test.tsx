import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { auth } from '@/lib/services';
import { me } from '@/test/test-support';
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

describe('SecondReminderRow', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  it('saves the second reminder choice to the account', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/reminders');
    const select = screen.getByRole('combobox', { name: 'Second reminder' });
    expect(select).toHaveDisplayValue('Off');
    expect(select).toHaveAccessibleDescription("If you haven't checked in yet, Ember nudges you once more.");

    await user.selectOptions(select, '1 hour');

    const patch = await fetch.next('PATCH', '/api/me');
    expect(patch.body).toEqual({ secondReminderMinutes: 60 });
    patch.respond(200, { ...me, secondReminderMinutes: 60 });
    await waitFor(() => expect(auth.me.get()?.secondReminderMinutes).toBe(60));
    expect(select).toHaveDisplayValue('1 hour');
    expect(screen.getByText('Saved.')).toBeInTheDocument();
  });
});
