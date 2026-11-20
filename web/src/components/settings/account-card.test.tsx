import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderPage } from '@/test/test-utils';
import { auth } from '@/lib/services';
import { me, mockFetch } from '@/test/test-support';
import { openSettings, resetSettings, settle, stubSettingsMedia } from '@/test/settings-page';
import SettingsPage from '@/pages/settings';

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

describe('AccountCard', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  it('for a Discord-only account, says so and offers no password change', async () => {
    const discordOnly = { ...me, email: null, hasPassword: false, discord: { linked: true, id: '42' } };
    auth.setMe(discordOnly);
    const fetch = mockFetch();
    renderPage('/settings/:group?', SettingsPage, '/settings/account');
    (await fetch.next('GET', '/api/me')).respond(200, discordOnly);

    expect(await screen.findByText('Signed in with Discord')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Change password' })).toBeNull();
  });

  it('signs out everywhere only after the confirmation, and ends signed out', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/account');

    await user.click(screen.getByRole('button', { name: 'Sign out everywhere' }));
    expect(screen.getByText('This signs you out on every device, including this one.')).toHaveFocus();
    await user.click(screen.getByRole('button', { name: 'Yes, sign out everywhere' }));
    (await fetch.next('POST', '/api/auth/logout-everywhere')).respond(204);
    (await fetch.next('GET', '/api/auth/antiforgery')).respond(204);

    await waitFor(() => expect(auth.me.get()).toBeNull());
    expect(fetch.all.some((call) => call.url === '/api/auth/logout')).toBe(false);
  });

  it('cancelling sign out everywhere sends nothing and keeps the session', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/account');

    await user.click(screen.getByRole('button', { name: 'Sign out everywhere' }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByText('This signs you out on every device, including this one.')).toBeNull();
    expect(screen.getByRole('button', { name: 'Sign out everywhere' })).toHaveFocus();
    await settle();
    expect(fetch.all.some((call) => call.url.startsWith('/api/auth/logout'))).toBe(false);
    expect(auth.me.get()).not.toBeNull();
  });

  it('says it could not sign out and stays when the server cannot end the session', async () => {
    const user = userEvent.setup();
    const { fetch, router } = await openSettings('/settings/account');

    await user.click(screen.getByRole('button', { name: 'Sign out' }));
    (await fetch.next('POST', '/api/auth/logout')).respond(503);

    expect(
      await screen.findByText("Ember couldn't sign you out. Check your connection and try again."),
    ).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/settings/account');
    expect(auth.me.get()).not.toBeNull();
  });
});
