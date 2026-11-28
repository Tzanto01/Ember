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
describe('UnitsRow', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  it('shows metric until imperial is chosen, saving that to the account', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/language');
    const select = screen.getByRole('combobox', { name: 'Units' });
    expect(select).toHaveValue('metric');
    expect(select).toHaveAccessibleDescription('For water and distances.');

    await user.selectOptions(select, 'Imperial (fl oz, mi)');

    const patch = await fetch.next('PATCH', '/api/me');
    expect(patch.body).toEqual({ units: 'imperial' });
    patch.respond(200, { ...me, units: 'imperial' });
    await waitFor(() => expect(auth.me.get()?.units).toBe('imperial'));
    expect(select).toHaveValue('imperial');
    expect(screen.getByText('Saved.')).toBeInTheDocument();
  });
});
