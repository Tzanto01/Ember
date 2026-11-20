import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Fireside, FiresideFriend } from '@/lib/fireside';
import { auth } from '@/lib/services';
import { me, mockFetch } from '@/test/test-support';
import { renderPage } from '@/test/test-utils';
import { FiresideToday } from './fireside-today';

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
  friends: [friend()],
  warmth: [],
  consentVersion: '2026-11',
  consentGiven: true,
  ...over,
});

const warmth = (id: number, friendId: number, name: string) => ({
  id,
  friendId,
  name,
  message: 'proud' as const,
  sentAt: '2026-11-18T08:00:00Z',
});

async function open(data: Fireside) {
  auth.setMe(me);
  const fetch = mockFetch();
  renderPage('/', FiresideToday, '/');
  (await fetch.next('GET', '/api/fireside')).respond(200, data);
  return fetch;
}

const asking = () => screen.findByRole('switch', { name: 'Could use some warmth today' });

describe('FiresideToday', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
  });

  it('shows warmth from a friend and removes the card on Okay while telling the server', async () => {
    const user = userEvent.setup();
    const fetch = await open(fireside({ warmth: [warmth(5, 7, 'Mara')] }));

    const card = await screen.findByTestId('fireside-warmth');
    expect(within(card).getByRole('heading', { name: 'Mara sent you warmth' })).toBeInTheDocument();
    expect(within(card).getByText('Proud of you')).toBeInTheDocument();

    await user.click(within(card).getByRole('button', { name: 'Okay, from Mara' }));
    (await fetch.next('POST', '/api/fireside/warmth/5/dismiss')).respond(204);
    await waitFor(() => expect(screen.queryByTestId('fireside-warmth')).toBeNull());
  });

  it('opens the send sheet for that friend with "Send some back", only while one can be sent', async () => {
    const user = userEvent.setup();
    const fetch = await open(
      fireside({
        friends: [friend({ id: 7, name: 'Mara' }), friend({ id: 8, name: 'Lio', canSendWarmth: false })],
        warmth: [warmth(5, 7, 'Mara'), warmth(6, 8, 'Lio')],
      }),
    );

    const [maraCard, lioCard] = await screen.findAllByTestId('fireside-warmth');
    expect(within(lioCard!).queryByRole('button', { name: 'Send some back' })).toBeNull();

    await user.click(within(maraCard!).getByRole('button', { name: 'Send some back' }));
    await user.click(await screen.findByRole('button', { name: 'Send' }));
    const call = await fetch.next('POST', '/api/fireside/friends/7/warmth');
    expect(call.body).toEqual({ message: 'thinking' });
  });

  it('asks for warmth with PUT /api/fireside/ask and stops with DELETE', async () => {
    const user = userEvent.setup();
    const fetch = await open(fireside());
    const toggle = await asking();
    expect(toggle).not.toBeChecked();

    await user.click(toggle);
    (await fetch.next('PUT', '/api/fireside/ask')).respond(200, { askingUntil: new Date(Date.now() + 3_600_000).toISOString() });
    await waitFor(() => expect(toggle).toBeChecked());

    await user.click(toggle);
    (await fetch.next('DELETE', '/api/fireside/ask')).respond(204);
    await waitFor(() => expect(toggle).not.toBeChecked());
  });

  it.each([
    ['Fireside is off', fireside({ enabled: false, name: null, friends: [] })],
    ['there are no friends', fireside({ friends: [] })],
  ])('has no ask control when %s', async (_, data) => {
    await open(data);
    await waitFor(() => expect(screen.queryByRole('switch')).toBeNull());
    expect(screen.queryByTestId('fireside-ask-today')).toBeNull();
  });
});
