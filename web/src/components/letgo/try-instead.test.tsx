import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeJournal } from '@/test/fake-journal';
import { JournalContext } from '@/components/journal/journal-context';
import { env } from '@/lib/env';
import type { JournalState } from '@/lib/journal/types';
import { helps } from '@/lib/services';
import { letGoHabit, mockFetch } from '@/test/test-support';
import { SlipSheet } from './slip-sheet';
import { TryInstead } from './try-instead';

vi.mock('@/lib/env', async (original) => ({
  env: { ...(await original<typeof import('@/lib/env')>()).env },
}));

const native = vi.hoisted(() => ({
  canOpenScreenTime: undefined as (() => Promise<{ available: boolean }>) | undefined,
  openScreenTime: vi.fn(async () => undefined),
}));

vi.mock('@/lib/native/widget', async (original) => ({
  ...(await original<typeof import('@/lib/native/widget')>()),
  capacitorWidget: async () => ({
    EmberWidget: {
      canOpenScreenTime: native.canOpenScreenTime,
      openScreenTime: native.openScreenTime,
    },
  }),
}));

const builtIn = [
  'Call or message someone',
  'Step outside for a few minutes',
  'Meet a friend',
  'Read a few pages',
  'Put the phone in another room',
];

const own = [
  { id: 'a', text: 'Cold water on my wrists', addedAt: '2026-11-01T08:00:00Z' },
  { id: 'b', text: 'Text Sam', addedAt: '2026-11-01T08:00:00Z' },
];

function renderTry(state: JournalState) {
  const journal = fakeJournal({ state });
  render(
    <JournalContext value={journal}>
      <TryInstead />
    </JournalContext>,
  );
  return journal;
}

const ideas = () =>
  within(screen.getByRole('list', { name: 'Try instead' }))
    .getAllByRole('listitem')
    .map((li) => li.textContent);

describe('TryInstead', () => {
  afterEach(() => {
    helps.items.set(null);
    env.native = false;
    native.canOpenScreenTime = undefined;
    native.openScreenTime.mockClear();
  });

  it('shows the built-in list after a slip is saved in the sheet', async () => {
    const fetch = mockFetch();
    render(
      <JournalContext value={fakeJournal({ state: 'off' })}>
        <SlipSheet habit={letGoHabit} open onOpenChange={() => undefined} onChange={() => undefined} />
      </JournalContext>,
    );
    expect(screen.queryByRole('list', { name: 'Try instead' })).toBeNull();

    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    (await fetch.next('POST', '/api/habits/9/slips')).respond(200, {
      ...letGoHabit,
      today: { ...letGoHabit.today, state: 'slipped', slips: 1 },
    });

    expect(await screen.findByRole('list', { name: 'Try instead' })).toBeInTheDocument();
    expect(ideas()).toEqual(builtIn);
  });

  it("shows the user's own What helps me items while the journal is unlocked", () => {
    helps.items.set(own);
    renderTry('unlocked');
    expect(ideas()).toEqual(['Cold water on my wrists', 'Text Sam']);
  });

  it.each(['locked', 'off'] as const)('shows the built-in list while the journal is %s', (state) => {
    helps.items.set(own);
    renderTry(state);
    expect(ideas()).toEqual(builtIn);
  });

  it('shows the built-in list while the journal is turning off', () => {
    helps.items.set(own);
    const journal = fakeJournal({ state: 'unlocked' });
    journal.turningOff.set(true);
    render(
      <JournalContext value={journal}>
        <TryInstead />
      </JournalContext>,
    );
    expect(ideas()).toEqual(builtIn);
  });

  it('has no screen-time button on the web, even when the plugin would answer', async () => {
    native.canOpenScreenTime = vi.fn(async () => ({ available: true }));
    renderTry('off');
    await act(() => new Promise((resolve) => setTimeout(resolve, 0)));
    expect(native.canOpenScreenTime).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'See your screen time' })).toBeNull();
  });

  it.each([
    ['says it is unavailable', () => vi.fn(async () => ({ available: false }))],
    ['rejects', () => vi.fn(() => Promise.reject(new Error('not implemented')))],
    ['lacks the method', () => undefined],
  ])('has no screen-time button in the app when the plugin %s', async (_, method) => {
    env.native = true;
    const canOpen = method();
    native.canOpenScreenTime = canOpen;
    renderTry('off');
    if (canOpen) await vi.waitFor(() => expect(canOpen).toHaveBeenCalled());
    await act(() => new Promise((resolve) => setTimeout(resolve, 0)));
    expect(screen.queryByRole('button', { name: 'See your screen time' })).toBeNull();
  });

  it('opens the phone screen-time overview from the app when it is available', async () => {
    env.native = true;
    native.canOpenScreenTime = vi.fn(async () => ({ available: true }));
    renderTry('off');

    await userEvent.click(await screen.findByRole('button', { name: 'See your screen time' }));
    await vi.waitFor(() => expect(native.openScreenTime).toHaveBeenCalledTimes(1));
  });
});
