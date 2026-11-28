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
describe('HabitPicturesRow', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  it('suggests pictures until turned off, saving that to the account', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/habits');
    const toggle = screen.getByRole('switch', { name: 'Suggest pictures for habits' });
    expect(toggle).toBeChecked();
    expect(toggle).toHaveAccessibleDescription(/Pictures you already chose stay/);

    await user.click(toggle);

    const sent = await fetch.next('PATCH', '/api/me');
    expect(sent.body).toEqual({ suggestPictures: false });
    sent.respond(200, { ...me, suggestPictures: false });
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect(toggle).not.toBeChecked();
    expect(auth.me.get()?.suggestPictures).toBe(false);
  });
});
