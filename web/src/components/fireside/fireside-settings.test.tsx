import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Fireside } from '@/lib/fireside';
import { auth, localMode } from '@/lib/services';
import { me, mockFetch } from '@/test/test-support';
import SettingsPage from '@/pages/settings';
import { renderPage } from '@/test/test-utils';

const off: Fireside = { enabled: false, name: null, notify: false, askingUntil: null, code: null, friends: [], warmth: [], consentVersion: '2026-11', consentGiven: true };
const on: Fireside = { ...off, enabled: true, name: 'Mara' };

async function openPeople(state: Fireside) {
  auth.setMe(me);
  const fetch = mockFetch();
  renderPage('/settings/:group?', SettingsPage, '/settings/people');
  (await fetch.next('GET', '/api/me')).respond(200, me);
  (await fetch.next('GET', '/api/fireside')).respond(200, state);
  const toggle = await screen.findByRole('switch', { name: 'Use Fireside' });
  return { fetch, toggle, puts: () => fetch.all.filter((c) => c.method === 'PUT') };
}

describe('Settings › People › Fireside', { timeout: 15_000 }, () => {
  beforeEach(() => {
    window.matchMedia = () =>
      ({ matches: false, addEventListener: () => undefined, removeEventListener: () => undefined }) as unknown as MediaQueryList;
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
    localMode.mode.set('account');
  });

  it('is not offered in no-account mode and makes no Fireside request', async () => {
    localMode.mode.set('local');
    auth.setMe(me);
    const fetch = mockFetch();
    renderPage('/settings/:group?', SettingsPage, '/settings/people');

    await screen.findByRole('heading', { level: 1, name: 'Settings' });
    expect(screen.queryByRole('switch', { name: 'Use Fireside' })).toBeNull();
    expect(fetch.all.filter((c) => c.url.startsWith('/api/fireside'))).toEqual([]);
  });

  it('turns on only with a name of 1 to 32 characters after trimming, and sends it trimmed', async () => {
    const user = userEvent.setup();
    const { toggle, puts, fetch } = await openPeople(off);
    const name = screen.getByRole('textbox', { name: /Your name at the fireside/ });

    await user.type(name, '   ');
    await user.click(toggle);
    expect(puts()).toHaveLength(0);
    expect(name).toBeInvalid();

    await user.clear(name);
    await user.type(name, 'x'.repeat(33));
    await user.click(toggle);
    expect(puts()).toHaveLength(0);
    expect(name).toBeInvalid();

    await user.clear(name);
    await user.type(name, '  Mara  ');
    await user.click(toggle);
    const put = await fetch.next('PUT', '/api/fireside');
    expect(put.body).toEqual({ enabled: true, name: 'Mara' });
    put.respond(200, on);
    await waitFor(() => expect(toggle).toBeChecked());
  });

  it('asks before turning off and sends enabled:false only after confirming', async () => {
    const user = userEvent.setup();
    const { toggle, puts, fetch } = await openPeople(on);

    await user.click(toggle);
    expect(await screen.findByText('You leave every fireside, and your Fireside consent ends.')).toBeInTheDocument();
    expect(puts()).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: 'Keep it on' }));
    expect(screen.queryByText('You leave every fireside, and your Fireside consent ends.')).toBeNull();
    expect(puts()).toHaveLength(0);
    expect(toggle).toBeChecked();

    await user.click(toggle);
    await user.click(await screen.findByRole('button', { name: 'Turn Fireside off' }));
    const put = await fetch.next('PUT', '/api/fireside');
    expect(put.body).toEqual({ enabled: false });
    put.respond(200, off);
    await waitFor(() => expect(toggle).not.toBeChecked());
  });

  it('without a current Fireside consent, asks first: cancel sends nothing, agree sends the consent version', async () => {
    const user = userEvent.setup();
    const { toggle, puts, fetch } = await openPeople({ ...off, consentGiven: false });
    await user.type(screen.getByRole('textbox', { name: /Your name at the fireside/ }), 'Mara');

    await user.click(toggle);
    expect(screen.getByRole('heading', { name: 'Before you turn on Fireside' })).toHaveFocus();
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('heading', { name: 'Before you turn on Fireside' })).toBeNull();
    expect(toggle).not.toBeChecked();
    expect(puts()).toHaveLength(0);

    await user.click(toggle);
    await user.click(screen.getByRole('button', { name: 'Agree and turn on' }));
    const put = await fetch.next('PUT', '/api/fireside');
    expect(put.body).toEqual({ enabled: true, name: 'Mara', consentVersion: '2026-11' });
    put.respond(200, on);
    await waitFor(() => expect(toggle).toBeChecked());
    expect(screen.queryByRole('heading', { name: 'Before you turn on Fireside' })).toBeNull();
  });
});
