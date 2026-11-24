import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import i18n from '@/lib/i18n';
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

describe('DeleteCard', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  it('asks for LÖSCHEN on a German screen, refuses DELETE there and still sends DELETE to the API', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/privacy');
    await act(() => i18n.changeLanguage('de'));

    await user.type(screen.getByLabelText('Dein Passwort', { exact: true }), 'synthetic-secret-1');
    const confirm = screen.getByLabelText('Gib zur Bestätigung LÖSCHEN ein');
    await user.type(confirm, 'DELETE');
    await user.click(screen.getByRole('button', { name: 'Mein Konto löschen' }));
    expect(await screen.findByText('Gib zur Bestätigung LÖSCHEN in Großbuchstaben ein.')).toBeInTheDocument();
    expect(fetch.pending.some((call) => call.url.endsWith('/api/me/delete'))).toBe(false);

    await user.clear(confirm);
    await user.type(confirm, ' LÖSCHEN ');
    await user.click(screen.getByRole('button', { name: 'Mein Konto löschen' }));

    (await fetch.next('GET', '/api/me')).respond(200, me);
    const remove = await fetch.next('POST', '/api/me/delete');
    expect(remove.body).toEqual({ password: 'synthetic-secret-1', confirm: 'DELETE' });
  });

  it('links the page that says what goes and what stays', async () => {
    await openSettings('/settings/privacy');

    expect(screen.getByRole('link', { name: 'What gets deleted and what stays for a while' })).toHaveAttribute(
      'href',
      '/delete-account',
    );
  });

  it('deletes the account with the password and lands on /login?reason=deleted', async () => {
    const user = userEvent.setup();
    const { fetch, router } = await openSettings('/settings/privacy');

    await user.type(screen.getByLabelText('Your password', { exact: true }), 'synthetic-secret-1');
    await user.type(screen.getByLabelText('Type DELETE to confirm'), 'DELETE');
    await user.click(screen.getByRole('button', { name: 'Delete my account' }));

    (await fetch.next('GET', '/api/me')).respond(200, me);
    const remove = await fetch.next('POST', '/api/me/delete');
    expect(remove.body).toEqual({ password: 'synthetic-secret-1', confirm: 'DELETE' });
    remove.respond(204);
    (await fetch.next('GET', '/api/auth/antiforgery')).respond(204);

    await waitFor(() =>
      expect(router.state.location.pathname + router.state.location.search).toBe('/login?reason=deleted'),
    );
    expect(auth.me.get()).toBeNull();
  });

  it('deletes nothing when the browser has signed in to another account since, and says which one', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/privacy');
    await user.type(screen.getByLabelText('Your password', { exact: true }), 'synthetic-secret-1');
    await user.type(screen.getByLabelText('Type DELETE to confirm'), 'DELETE');
    await user.click(screen.getByRole('button', { name: 'Delete my account' }));
    const other = { ...me, id: '0199aaaa-0000-7000-8000-00000000000b', email: null, hasPassword: false };
    (await fetch.next('GET', '/api/me')).respond(200, other);

    expect(await screen.findByText(/now signed in as Discord, so nothing was deleted/)).toBeInTheDocument();
    expect(fetch.all.some((call) => call.url === '/api/me/delete')).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Delete my account' }));
    (await fetch.next('GET', '/api/me')).respond(200, other);
    expect((await fetch.next('POST', '/api/me/delete')).body).toEqual({ confirm: 'DELETE' });
  });
});
