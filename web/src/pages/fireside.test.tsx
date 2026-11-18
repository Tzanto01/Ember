import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Fireside, FiresideFriend } from '@/lib/fireside';
import { auth } from '@/lib/services';
import { me, mockFetch } from '@/lib/test-support';
import { renderPage } from '@/test-utils';
import FiresidePage from './fireside';

const friend = (over: Partial<FiresideFriend> = {}): FiresideFriend => ({
  id: 7,
  name: 'Mara',
  asking: false,
  checkedInToday: null,
  muted: false,
  shareFlame: false,
  renamedFrom: null,
  joinedViaMyCode: false,
  canSendWarmth: true,
  ...over,
});

const fireside = (over: Partial<Fireside> = {}): Fireside => ({
  enabled: true,
  name: 'Nia',
  notify: false,
  askingUntil: null,
  code: null,
  friends: [],
  warmth: [],
  ...over,
});

async function open(data: Fireside) {
  auth.setMe(me);
  const fetch = mockFetch();
  const router = renderPage('/fireside', FiresidePage, '/fireside');
  (await fetch.next('GET', '/api/fireside')).respond(200, data);
  return { fetch, router };
}

const rows = () => screen.getAllByTestId('fireside-friend');

describe('FiresidePage', { timeout: 15_000 }, () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    auth.clearSession();
  });

  it('sends the user to Settings › People while Fireside is off', async () => {
    const { router } = await open(fireside({ enabled: false, name: null }));
    await waitFor(() => expect(router.state.location.pathname).toBe('/settings/people'));
  });

  describe('code', () => {
    beforeEach(() => {
      vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
      vi.setSystemTime(new Date('2026-11-18T12:00:00Z'));
    });

    it('shows the code as XXXX XXXX with its remaining minutes and takes it away at expiry', async () => {
      await open(fireside({ code: { code: 'k7m2q9xa', expiresAt: '2026-11-18T12:12:00Z' } }));

      expect(await screen.findByTestId('fireside-code')).toHaveTextContent('K7M2 Q9XA');
      expect(screen.getByText(/12 minutes left/)).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'New code' })).toBeInTheDocument();

      act(() => vi.advanceTimersByTime(13 * 60_000));

      expect(screen.queryByTestId('fireside-code')).toBeNull();
      expect(screen.getByRole('button', { name: 'Make a code' })).toBeInTheDocument();
    });
  });

  describe('joining', () => {
    it('sends the typed code as 8 upper-case characters without spaces, then lists the new friend', async () => {
      const user = userEvent.setup();
      const { fetch } = await open(fireside());

      await user.type(await screen.findByLabelText('Got a code from someone?'), 'k7m2 q9xa');
      await user.click(screen.getByRole('button', { name: 'Join their fireside' }));
      const join = await fetch.next('POST', '/api/fireside/join');
      expect(join.body).toEqual({ code: 'K7M2Q9XA' });
      join.respond(200, friend({ name: 'Lio' }));

      expect(await screen.findByText("You're at Lio's fireside")).toBeInTheDocument();
      expect(within(rows()[0]!).getByText('Lio')).toBeInTheDocument();
    });

    it('does not send a code that is not 8 characters', async () => {
      const user = userEvent.setup();
      const { fetch } = await open(fireside());

      await user.type(await screen.findByLabelText('Got a code from someone?'), 'k7m2 q9');
      await user.click(screen.getByRole('button', { name: 'Join their fireside' }));

      expect(await screen.findByText('A code has 8 letters and numbers.')).toBeInTheDocument();
      expect(fetch.all.filter((c) => c.method === 'POST')).toEqual([]);
    });

    it.each([
      [400, { type: 'validation', errors: { code: ["That code didn't work."] } }, undefined, "That code didn't work."],
      [409, { type: 'conflict', detail: 'This did not work out.' }, undefined, 'This did not work out.'],
      [429, { type: 'too_many_requests' }, { 'Retry-After': '3600' }, 'Too many tries. Try again in a while.'],
    ])('answers a %i from the server with its calm message', async (status, problem, headers, text) => {
      const user = userEvent.setup();
      const { fetch } = await open(fireside());

      await user.type(await screen.findByLabelText('Got a code from someone?'), 'k7m2q9xa');
      await user.click(screen.getByRole('button', { name: 'Join their fireside' }));
      (await fetch.next('POST', '/api/fireside/join')).respond(status, problem, headers);

      expect(await screen.findByText(text)).toBeInTheDocument();
    });
  });

  describe('friends', () => {
    it('shows Send warmth only while a warmth can be sent, otherwise "Sent today"', async () => {
      await open(fireside({ friends: [friend({ id: 1, name: 'Mara' }), friend({ id: 2, name: 'Lio', canSendWarmth: false })] }));

      await screen.findByText('Lio');
      expect(within(rows()[0]!).getByRole('button', { name: 'Send warmth to Mara' })).toBeInTheDocument();
      expect(within(rows()[1]!).queryByRole('button')).toBeNull();
      expect(within(rows()[1]!).getByText('Sent today')).toBeInTheDocument();
    });

    it('marks a friend as checked in only when checkedInToday is true', async () => {
      await open(
        fireside({
          friends: [
            friend({ id: 1, name: 'Mara', checkedInToday: true }),
            friend({ id: 2, name: 'Lio', checkedInToday: false }),
            friend({ id: 3, name: 'Kai', checkedInToday: null }),
          ],
        }),
      );

      await screen.findByText('Kai');
      expect(within(rows()[0]!).getByText('checked in today')).toBeInTheDocument();
      expect(within(rows()[1]!).queryByText('checked in today')).toBeNull();
      expect(within(rows()[2]!).queryByText('checked in today')).toBeNull();
    });

    it('opens the send sheet for the chosen friend', async () => {
      const user = userEvent.setup();
      await open(fireside({ friends: [friend({ id: 1, name: 'Mara' }), friend({ id: 2, name: 'Lio' })] }));

      await user.click(await screen.findByRole('button', { name: 'Send warmth to Lio' }));
      expect(await screen.findByRole('dialog', { name: 'Send warmth to Lio' })).toBeInTheDocument();
    });
  });

  describe('notices', () => {
    it('clears the rename notice with Okay by acknowledging the rename', async () => {
      const user = userEvent.setup();
      const { fetch } = await open(fireside({ friends: [friend({ name: 'LenaABC', renamedFrom: 'Lena' })] }));

      expect(await screen.findByText('Lena has changed their name to LenaABC')).toBeInTheDocument();
      await user.click(screen.getByRole('button', { name: 'Okay' }));
      const call = await fetch.next('POST', '/api/fireside/friends/7/acknowledge');
      expect(call.body).toEqual({ rename: true });
      call.respond(200, friend({ name: 'LenaABC' }));

      await waitFor(() => expect(screen.queryByText(/has changed their name/)).toBeNull());
    });

    it('acknowledges a joined notice with Okay and keeps the friend', async () => {
      const user = userEvent.setup();
      const { fetch } = await open(fireside({ friends: [friend({ joinedViaMyCode: true })] }));

      expect(await screen.findByText('Mara joined your fireside')).toBeInTheDocument();
      await user.click(screen.getByRole('button', { name: 'Okay' }));
      const call = await fetch.next('POST', '/api/fireside/friends/7/acknowledge');
      expect(call.body).toEqual({ joined: true });
      call.respond(200, friend());

      await waitFor(() => expect(screen.queryByText('Mara joined your fireside')).toBeNull());
      expect(rows()).toHaveLength(1);
    });

    it('removes a friend and the code with "Not someone I know" (revokeCode=true)', async () => {
      const user = userEvent.setup();
      const { fetch } = await open(
        fireside({
          friends: [friend({ joinedViaMyCode: true })],
          code: { code: 'K7M2Q9XA', expiresAt: new Date(Date.now() + 20 * 60_000).toISOString() },
        }),
      );

      await user.click(await screen.findByRole('button', { name: 'Not someone I know' }));
      (await fetch.next('DELETE', '/api/fireside/friends/7?revokeCode=true')).respond(204);

      await waitFor(() => expect(screen.queryByTestId('fireside-friend')).toBeNull());
      expect(screen.queryByTestId('fireside-code')).toBeNull();
    });
  });

  it('turns notifications on with the switch', async () => {
    const user = userEvent.setup();
    const { fetch } = await open(fireside());

    await user.click(await screen.findByRole('switch', { name: 'Notify me about warmth and new friends' }));
    const put = await fetch.next('PUT', '/api/fireside');
    expect(put.body).toEqual({ enabled: true, notify: true });
    put.respond(200, fireside({ notify: true }));

    await waitFor(() =>
      expect(screen.getByRole('switch', { name: 'Notify me about warmth and new friends' })).toBeChecked(),
    );
  });
});
