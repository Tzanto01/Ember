import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { type Fireside, type FiresideFriend, firesideKey } from '@/lib/fireside';
import { auth } from '@/lib/services';
import { me, mockFetch } from '@/lib/test-support';
import { FiresideAsk } from './fireside-ask';

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

const loaded: Fireside = {
  enabled: true,
  name: 'Nia',
  notify: false,
  askingUntil: null,
  code: null,
  friends: [mara],
  warmth: [],
  consentVersion: '2026-11',
  consentGiven: true,
};

function renderAsk(cached: Fireside | null) {
  auth.setMe(me);
  const fetch = mockFetch();
  const client = new QueryClient();
  if (cached) client.setQueryData(firesideKey, cached);
  render(
    <QueryClientProvider client={client}>
      <FiresideAsk />
      <button type="button">Finish</button>
    </QueryClientProvider>,
  );
  return fetch;
}

const title = 'Let your fireside know you could use some warmth?';

describe('FiresideAsk (end of a moment)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
  });

  it('is offered from the cached Fireside without any request, and PUTs the ask on "Let them know"', async () => {
    const user = userEvent.setup();
    const fetch = renderAsk(loaded);

    expect(screen.getByRole('heading', { name: title })).toBeInTheDocument();
    expect(fetch.all).toEqual([]);

    await user.click(screen.getByRole('button', { name: 'Let them know' }));
    (await fetch.next('PUT', '/api/fireside/ask')).respond(200, { askingUntil: new Date(Date.now() + 3_600_000).toISOString() });
    expect(await screen.findByText('Your fireside can see it until midnight.')).toBeInTheDocument();
  });

  it.each([
    ['Fireside data is not cached', null],
    ['Fireside is off', { ...loaded, enabled: false }],
    ['there are no friends', { ...loaded, friends: [] }],
    ['the user is already asking', { ...loaded, askingUntil: new Date(Date.now() + 3_600_000).toISOString() }],
  ])('is not offered when %s', (_, cached) => {
    const fetch = renderAsk(cached);

    expect(screen.queryByRole('heading', { name: title })).toBeNull();
    expect(fetch.all).toEqual([]);
  });

  it('only says quietly that it failed, and leaves the way on open', async () => {
    const user = userEvent.setup();
    const fetch = renderAsk(loaded);

    await user.click(screen.getByRole('button', { name: 'Let them know' }));
    (await fetch.next('PUT', '/api/fireside/ask')).respond(500);

    expect(await screen.findByText(/That didn't go through just now/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Finish' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Not this time' })).toBeEnabled();
  });
});
