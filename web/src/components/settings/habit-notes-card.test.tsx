import { screen } from '@testing-library/react';
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

describe('HabitNotesCard', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  it('offers a note after check-in only once turned on, saving it to the account', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/habits');
    const toggle = screen.getByRole('switch', { name: 'Offer a note after check-in' });
    expect(toggle).not.toBeChecked();
    expect(toggle).toHaveAccessibleDescription(/Off, Ember never asks/);

    await user.click(toggle);

    const sent = await fetch.next('PATCH', '/api/me');
    expect(sent.body).toEqual({ offerHabitNote: true });
    sent.respond(200, { ...me, offerHabitNote: true });
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect(toggle).toBeChecked();
    expect(auth.me.get()?.offerHabitNote).toBe(true);
  });
});
