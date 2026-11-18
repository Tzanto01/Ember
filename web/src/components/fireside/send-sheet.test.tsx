import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { FiresideFriend } from '@/lib/fireside';
import { mockFetch } from '@/lib/test-support';
import { renderPage } from '@/test-utils';
import { SendSheet } from './send-sheet';

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

function open() {
  const fetch = mockFetch();
  renderPage('/', () => <SendSheet friend={mara} onClose={() => undefined} />, '/');
  return fetch;
}

describe('SendSheet', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('offers the five fixed messages and sends the selected one', async () => {
    const user = userEvent.setup();
    const fetch = open();

    const dialog = await screen.findByRole('dialog', { name: 'Send warmth to Mara' });
    expect(within(dialog).getAllByRole('radio').map((r) => r.closest('label')?.textContent)).toEqual([
      'Thinking of you',
      'Proud of you',
      'Go easy on yourself today',
      "You're not alone",
      'Sending a warm blanket',
    ]);

    await user.click(within(dialog).getByRole('radio', { name: "You're not alone" }));
    await user.click(within(dialog).getByRole('button', { name: 'Send' }));
    const call = await fetch.next('POST', '/api/fireside/friends/7/warmth');
    expect(call.body).toEqual({ message: 'notAlone' });
    call.respond(204);

    expect(await screen.findByRole('heading', { name: 'Sent' })).toBeInTheDocument();
  });

  it('says gently that warmth was already sent today on a 409, and stays open to cancel', async () => {
    const user = userEvent.setup();
    const fetch = open();

    await user.click(await screen.findByRole('button', { name: 'Send' }));
    (await fetch.next('POST', '/api/fireside/friends/7/warmth')).respond(409, { type: 'conflict', detail: 'Over the limit.' });

    expect(await screen.findByText("You've already sent Mara warmth today.")).toBeInTheDocument();
    expect(screen.queryByText('Over the limit.')).toBeNull();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
  });
});
