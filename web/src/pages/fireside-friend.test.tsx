import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Fireside, FiresideFriend } from '@/lib/fireside';
import { auth } from '@/lib/services';
import { me, mockFetch } from '@/test/test-support';
import { renderPage } from '@/test/test-utils';
import FiresideFriendPage from './fireside-friend';

const mara: FiresideFriend = {
  id: 7,
  name: 'Mara',
  asking: false,
  checkedInToday: null,
  muted: false,
  shareFlame: false,
  renamedFrom: null,
  joinedViaMyCode: false,
  canSendWarmth: true,
};

const data: Fireside = { enabled: true, name: 'Nia', notify: false, askingUntil: null, code: null, friends: [mara], warmth: [], consentVersion: '2026-11', consentGiven: true };

async function open() {
  auth.setMe(me);
  const fetch = mockFetch();
  const router = renderPage('/fireside/:id', FiresideFriendPage, '/fireside/7');
  (await fetch.next('GET', '/api/fireside')).respond(200, data);
  await screen.findByRole('heading', { level: 1, name: 'Mara' });
  return { fetch, router };
}

describe('FiresideFriendPage', { timeout: 15_000 }, () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
  });

  it('PATCHes shareFlame from the flame switch and muted from the mute switch', async () => {
    const user = userEvent.setup();
    const { fetch } = await open();

    await user.click(screen.getByRole('switch', { name: 'A small flame on days you check in' }));
    const flame = await fetch.next('PATCH', '/api/fireside/friends/7');
    expect(flame.body).toEqual({ shareFlame: true });
    flame.respond(200, { ...mara, shareFlame: true });
    await waitFor(() =>
      expect(screen.getByRole('switch', { name: 'A small flame on days you check in' })).toBeChecked(),
    );

    await user.click(screen.getByRole('switch', { name: 'Mute Mara' }));
    const mute = await fetch.next('PATCH', '/api/fireside/friends/7');
    expect(mute.body).toEqual({ muted: true });
    mute.respond(200, { ...mara, shareFlame: true, muted: true });
    await waitFor(() => expect(screen.getByRole('switch', { name: 'Mute Mara' })).toBeChecked());
  });

  it('removes only after confirming, keeps the code (revokeCode=false) and returns to /fireside', async () => {
    const user = userEvent.setup();
    const { fetch, router } = await open();

    await user.click(screen.getByRole('button', { name: 'Remove from your fireside' }));
    expect(await screen.findByText('Remove Mara from your fireside?')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Keep Mara' }));
    expect(fetch.all.filter((c) => c.method === 'DELETE')).toEqual([]);

    await user.click(screen.getByRole('button', { name: 'Remove from your fireside' }));
    await user.click(await screen.findByRole('button', { name: 'Remove' }));
    (await fetch.next('DELETE', '/api/fireside/friends/7?revokeCode=false')).respond(204);

    await waitFor(() => expect(router.state.location.pathname).toBe('/fireside'));
  });

  it('says the person is not at the fireside for an unknown friend id', async () => {
    auth.setMe(me);
    const fetch = mockFetch();
    renderPage('/fireside/:id', FiresideFriendPage, '/fireside/99');
    (await fetch.next('GET', '/api/fireside')).respond(200, data);

    expect(await screen.findByText("This person isn't at your fireside anymore.")).toBeInTheDocument();
  });
});
