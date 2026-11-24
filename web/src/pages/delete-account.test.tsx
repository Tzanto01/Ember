import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { auth, localMode } from '@/lib/services';
import { type FetchMock, me, mockFetch } from '@/test/test-support';
import { inGerman, renderPage } from '@/test/test-utils';
import DeleteAccountPage from './delete-account';

vi.mock('@/lib/services', async (original) => {
  const { Store } = await import('@/lib/store');
  return {
    ...(await original<typeof import('@/lib/services')>()),
    localMode: {
      mode: new Store('account'),
      evicted: new Store(false),
      wipe: vi.fn(async () => {}),
    },
  };
});

const open = () => renderPage('/delete-account', DeleteAccountPage, '/delete-account');
const remove = () => screen.queryByRole('button', { name: 'Delete my account' });

describe('DeleteAccountPage', () => {
  let fetch: FetchMock;

  beforeEach(() => {
    fetch = mockFetch();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
    localMode.mode.set('account');
  });

  it('signed out: names Ember, leads to sign-in and back here, and offers no deletion without signing in', () => {
    open();

    expect(
      screen.getByRole('heading', { level: 1, name: 'Delete your Ember account' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Sign in to delete your account' })).toHaveAttribute(
      'href',
      '/login?returnUrl=%2Fdelete-account',
    );
    expect(screen.getByRole('link', { name: 'Reset your password' })).toHaveAttribute(
      'href',
      '/forgot-password',
    );
    expect(screen.getByRole('link', { name: 'contact@nicowoellnitz.de' })).toHaveAttribute(
      'href',
      'mailto:contact@nicowoellnitz.de',
    );
    expect(screen.getByText(/send the bot \/privacy delete/)).toBeInTheDocument();
    expect(
      screen.getByText(/“Privacy & data”, “Delete everything on this device”/),
    ).toBeInTheDocument();
    expect(screen.getByText(/up to about 6 weeks/)).toBeInTheDocument();
    expect(remove()).toBeNull();
    expect(screen.queryByRole('textbox')).toBeNull();
    expect(fetch.all).toEqual([]);
  });

  it('signed out in German: the same way in German', async () => {
    await inGerman();
    open();

    expect(
      screen.getByRole('heading', { level: 1, name: 'Dein Ember-Konto löschen' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Anmelden und Konto löschen' })).toBeInTheDocument();
    expect(
      screen.getByText('Gib dein Passwort und LÖSCHEN ein. Ohne Passwort reicht LÖSCHEN.'),
    ).toBeInTheDocument();
  });

  it('signed in: a wrong password deletes nothing, the right one does and ends on the calm confirmation', async () => {
    const user = userEvent.setup();
    auth.setMe(me);
    const wipe = vi.spyOn(auth, 'wipeDevice');
    const router = open();
    expect(screen.getByText(`You're signed in as ${me.email}.`)).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Sign in to delete your account' })).toBeNull();

    const card = within(screen.getByRole('region', { name: 'Delete your account' }));
    await user.type(card.getByLabelText('Your password', { exact: true }), 'wrong-synthetic-1');
    await user.type(card.getByLabelText('Type DELETE to confirm'), 'DELETE');
    await user.click(remove()!);
    (await fetch.next('POST', '/api/me/delete')).respond(400, {
      status: 400,
      errors: { password: ['The password is not correct.'] },
    });
    expect(await card.findByText(/That password doesn't match/)).toBeInTheDocument();
    expect(wipe).not.toHaveBeenCalled();
    expect(auth.me.get()).toEqual(me);

    const password = card.getByLabelText('Your password', { exact: true });
    await user.clear(password);
    await user.type(password, 'synthetic-secret-1');
    await user.click(remove()!);
    const sent = await fetch.next('POST', '/api/me/delete');
    expect(sent.body).toEqual({ password: 'synthetic-secret-1', confirm: 'DELETE' });
    sent.respond(204);

    const antiforgery = await fetch.next('GET', '/api/auth/antiforgery');
    expect(wipe).toHaveBeenCalledOnce();
    expect(auth.me.get()).toBeNull();
    expect(screen.queryByRole('link', { name: 'Sign in to delete your account' })).toBeNull();
    antiforgery.respond(204);
    await waitFor(() =>
      expect(router.state.location.pathname + router.state.location.search).toBe(
        '/login?reason=deleted',
      ),
    );
  });

  it('a Discord-only account deletes with the word alone', async () => {
    const user = userEvent.setup();
    auth.setMe({ ...me, email: null, hasPassword: false });
    open();

    expect(screen.getByText("You're signed in with Discord.")).toBeInTheDocument();
    expect(screen.queryByLabelText('Your password', { exact: true })).toBeNull();
    await user.type(screen.getByLabelText('Type DELETE to confirm'), 'delete');
    await user.click(remove()!);
    expect(fetch.all).toEqual([]);

    await user.clear(screen.getByLabelText('Type DELETE to confirm'));
    await user.type(screen.getByLabelText('Type DELETE to confirm'), 'DELETE');
    await user.click(remove()!);
    expect((await fetch.next('POST', '/api/me/delete')).body).toEqual({ confirm: 'DELETE' });
  });

  it('in no-account mode deletes the device data and asks no server', async () => {
    const user = userEvent.setup();
    localMode.mode.set('local');
    auth.setMe(me);
    open();

    expect(remove()).toBeNull();
    expect(screen.queryByText('What stays for a while')).toBeNull();
    await user.type(screen.getByLabelText('Type DELETE to confirm'), 'DELETE');
    await user.click(screen.getByRole('button', { name: /^Delete everything/ }));

    await waitFor(() => expect(localMode.wipe).toHaveBeenCalledOnce());
    expect(fetch.all).toEqual([]);
  });

  it('turns into the deletion card when the session loads late', async () => {
    open();
    expect(
      screen.getByRole('link', { name: 'Sign in to delete your account' }),
    ).toBeInTheDocument();

    auth.setMe(me);

    await waitFor(() => expect(remove()).toBeInTheDocument());
    expect(screen.queryByRole('link', { name: 'Sign in to delete your account' })).toBeNull();
  });
});
