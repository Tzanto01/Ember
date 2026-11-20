import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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

const loadPage = vi.hoisted(() => vi.fn());
vi.mock('@/lib/navigation', async (original) => ({ ...(await original<typeof import('@/lib/navigation')>()), loadPage }));

describe('DiscordCard', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  it('links Discord only after the password: a wrong one is an inline error, the right one goes to Discord', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/account');
    const card = within(screen.getByRole('region', { name: 'Discord' }));

    await user.click(card.getByRole('button', { name: 'Link Discord' }));
    const field = card.getByLabelText('Your password', { exact: true });
    expect(field).toHaveFocus();

    await user.type(field, 'wrong-synthetic-1');
    await user.click(card.getByRole('button', { name: 'Continue to Discord' }));
    const wrong = await fetch.next('POST', '/api/auth/discord/link/start');
    expect(wrong.body).toEqual({ password: 'wrong-synthetic-1' });
    wrong.respond(400, { status: 400, errors: { password: ['The password is not correct.'] } });

    await waitFor(() => expect(field).toHaveAttribute('aria-invalid', 'true'));
    expect(field).toHaveAccessibleDescription(expect.stringContaining("That password doesn't match. Try again."));
    expect(field).toHaveFocus();
    expect(loadPage).not.toHaveBeenCalled();

    await user.clear(field);
    await user.type(field, 'synthetic-secret-1');
    await user.click(card.getByRole('button', { name: 'Continue to Discord' }));
    (await fetch.next('POST', '/api/auth/discord/link/start')).respond(204);
    await waitFor(() => expect(loadPage).toHaveBeenCalledWith('/api/auth/discord/link'));
  });
});
