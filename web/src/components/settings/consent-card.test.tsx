import { screen, waitFor, within } from '@testing-library/react';
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

describe('ConsentCard', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  it('withdraws consent only after the confirmation, reloads me and lands on /consent', async () => {
    const user = userEvent.setup();
    const { fetch, router } = await openSettings('/settings/privacy');
    const card = within(screen.getByRole('region', { name: 'Privacy' }));

    await user.click(card.getByRole('button', { name: 'Withdraw consent' }));
    expect(card.getByText(/Ember then saves nothing new, your reminders stop/)).toHaveFocus();
    await user.click(card.getByRole('button', { name: 'Keep it' }));
    expect(card.getByRole('button', { name: 'Withdraw consent' })).toHaveFocus();
    await settle();
    expect(fetch.all.some((call) => call.url === '/api/me/consent/withdraw')).toBe(false);

    await user.click(card.getByRole('button', { name: 'Withdraw consent' }));
    await user.click(card.getByRole('button', { name: 'Withdraw' }));
    (await fetch.next('POST', '/api/me/consent/withdraw')).respond(204);
    (await fetch.next('GET', '/api/me')).respond(200, {
      ...me,
      consent: { ...me.consent, given: false, withdrawnAt: '2026-11-01T09:00:00Z', deletesAt: '2026-12-01T09:00:00Z' },
    });

    await waitFor(() => expect(router.state.location.pathname).toBe('/consent'));
  });

  it('while consent is withdrawn, shows the deletion date and a way to give it again', async () => {
    const withdrawn = {
      ...me,
      timezone: 'Europe/Berlin',
      consent: { ...me.consent, given: false, withdrawnAt: '2026-11-01T09:00:00Z', deletesAt: '2026-12-01T09:00:00Z' },
    };
    auth.setMe(withdrawn);
    const fetch = mockFetch();
    renderPage('/settings/:group?', SettingsPage, '/settings/privacy');
    (await fetch.next('GET', '/api/me')).respond(200, withdrawn);
    const card = within(await screen.findByRole('region', { name: 'Privacy' }));

    expect(card.getByText('You withdrew your consent. Ember deletes your data on December 1, 2026.')).toBeInTheDocument();
    expect(card.getByRole('link', { name: 'Give consent' })).toHaveAttribute('href', '/consent');
    expect(card.queryByRole('button', { name: 'Withdraw consent' })).toBeNull();
  });
});
