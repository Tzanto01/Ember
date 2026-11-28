import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderPage } from '@/test/test-utils';
import { auth } from '@/lib/services';
import { me, mockFetch } from '@/test/test-support';
import { openSettings, resetSettings, stubSettingsMedia } from '@/test/settings-page';
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

describe('StreaksRow', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  it('turns streaks off through PATCH /api/me, hiding the grace days at once and keeping them hidden once saved', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/habits');
    const toggle = screen.getByRole('switch', { name: 'Show streaks' });
    expect(toggle).toBeChecked();
    expect(screen.getByRole('combobox', { name: 'Grace days per week' })).toBeInTheDocument();

    await user.click(toggle);

    const sent = await fetch.next('PATCH', '/api/me');
    expect(sent.body).toEqual({ showStreaks: false });
    expect(toggle).not.toBeChecked();
    expect(screen.queryByRole('combobox', { name: 'Grace days per week' })).toBeNull();
    sent.respond(200, { ...me, showStreaks: false });

    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect(toggle).not.toBeChecked();
    expect(screen.queryByLabelText('Grace days per week')).toBeNull();
  });

  it('puts the switch back when turning streaks on fails', async () => {
    const off = { ...me, showStreaks: false };
    const user = userEvent.setup();
    auth.setMe(off);
    const fetch = mockFetch();
    renderPage('/settings/:group?', SettingsPage, '/settings/habits');
    (await fetch.next('GET', '/api/me')).respond(200, off);
    const toggle = await screen.findByRole('switch', { name: 'Show streaks' });
    expect(screen.queryByRole('combobox', { name: 'Grace days per week' })).toBeNull();

    await user.click(toggle);
    const sent = await fetch.next('PATCH', '/api/me');
    expect(sent.body).toEqual({ showStreaks: true });
    sent.respond(500, { type: 'internal', title: 'Server error', status: 500 });

    await waitFor(() => expect(toggle).not.toBeChecked());
    expect(screen.queryByRole('combobox', { name: 'Grace days per week' })).toBeNull();
  });
});
