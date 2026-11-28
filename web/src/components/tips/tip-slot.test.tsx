import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ComponentType } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { type Habit, type Me, tipIds } from '@/lib/models';
import { auth } from '@/lib/services';
import { type FetchMock, habit, me, mockFetch } from '@/test/test-support';
import { readTipMemory } from '@/lib/tips';
import { renderPage } from '@/test/test-utils';
import { TipSlot } from './tip-slot';

const notes = [{ version: '0.10.0-beta.1', date: '2026-11-14', notes: { en: ['Something new.'] } }];
const three: Habit[] = [1, 2, 3].map((id) => ({ ...habit, id, name: `Habit ${id}` }));

function renderSlot(f: FetchMock, user: Me, slot: ComponentType) {
  auth.setMe(user);
  renderPage('/today', slot, '/today');
  return f.next('GET', '/release-notes.json').then((call) => call.respond(200, notes));
}

describe('TipSlot', () => {
  let f: FetchMock;

  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('ember.seenReleaseNotes', '0.10.0-beta.1');
    f = mockFetch();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
  });

  it('asks about streaks and turns them on with "Show streaks"', async () => {
    await renderSlot(f, { ...me, id: 'streaks-yes', showStreaks: false, tipsSeen: [] }, () => (
      <TipSlot place="top" habits={[habit]} />
    ));

    expect(
      await screen.findByRole('heading', { name: 'Do you want to see streaks?' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Dismiss tip' })).toBeNull();
    await userEvent.click(screen.getByRole('button', { name: 'Show streaks' }));
    const patch = await f.next('PATCH', '/api/me');
    expect(patch.body).toEqual({ showStreaks: true });
    patch.respond(200, { ...me, id: 'streaks-yes', showStreaks: true, tipsSeen: ['streaks'] });

    await waitFor(() =>
      expect(screen.queryByRole('heading', { name: 'Do you want to see streaks?' })).toBeNull(),
    );
    expect(auth.me.get()?.showStreaks).toBe(true);
  });

  it('keeps streaks off with "Not for me", hiding at once and marking the question seen', async () => {
    await renderSlot(f, { ...me, id: 'streaks-no', showStreaks: false, tipsSeen: [] }, () => (
      <TipSlot place="top" habits={[habit]} />
    ));

    await userEvent.click(await screen.findByRole('button', { name: 'Not for me' }));

    expect(screen.queryByRole('heading', { name: 'Do you want to see streaks?' })).toBeNull();
    (await f.next('PUT', '/api/me/tips/streaks')).respond(204);
    expect(auth.me.get()).toMatchObject({ showStreaks: false, tipsSeen: ['streaks'] });
    expect(f.all.some((c) => c.method === 'PATCH')).toBe(false);
    await waitFor(() =>
      expect(readTipMemory('streaks-no')).toMatchObject({ wait: '2026-11-01', pending: [] }),
    );
  });

  it('dismisses a link tip with the × and sends the mark', async () => {
    await renderSlot(f, { ...me, id: 'dismiss', tipsSeen: ['streaks'] }, () => (
      <TipSlot place="below" habits={three} routines={[]} />
    ));
    (await f.next('GET', '/api/day-notes')).respond(200, []);

    expect(await screen.findByRole('link', { name: 'Make a routine' })).toHaveAttribute(
      'href',
      '/routines/new',
    );
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss tip' }));

    expect(screen.queryByRole('link', { name: 'Make a routine' })).toBeNull();
    (await f.next('PUT', '/api/me/tips/routines')).respond(204);
    await waitFor(() => expect(readTipMemory('dismiss').pending).toEqual([]));
  });

  it('keeps a failed mark on the device, stays hidden and sends it again when back online', async () => {
    await renderSlot(f, { ...me, id: 'offline', tipsSeen: ['streaks'] }, () => (
      <TipSlot place="below" habits={three} routines={[]} />
    ));
    (await f.next('GET', '/api/day-notes')).respond(200, []);

    await userEvent.click(await screen.findByRole('button', { name: 'Dismiss tip' }));
    (await f.next('PUT', '/api/me/tips/routines')).fail();

    await waitFor(() => expect(readTipMemory('offline').pending).toEqual(['routines']));
    expect(screen.queryByRole('link', { name: 'Make a routine' })).toBeNull();
    act(() => void window.dispatchEvent(new Event('online')));
    (await f.next('PUT', '/api/me/tips/routines')).respond(204);
    await waitFor(() => expect(readTipMemory('offline').pending).toEqual([]));
  });

  it('sends marks left from an earlier start and keeps those tips hidden meanwhile', async () => {
    localStorage.setItem(
      'ember.tips.restart',
      JSON.stringify({ first: '2026-10-01', pending: ['routines'] }),
    );
    await renderSlot(f, { ...me, id: 'restart', tipsSeen: ['streaks'] }, () => (
      <TipSlot place="below" habits={three} routines={[]} />
    ));

    (await f.next('PUT', '/api/me/tips/routines')).respond(204);
    (await f.next('GET', '/api/day-notes')).respond(200, []);
    await waitFor(() => expect(readTipMemory('restart').pending).toEqual([]));
    expect(screen.queryByRole('link', { name: 'Make a routine' })).toBeNull();
  });
});

describe('TipSlot picture offer', () => {
  let f: FetchMock;
  const allSeen = [...tipIds];
  const stretch: Habit = { ...habit, id: 1, name: 'Stretch' };
  const read: Habit = { ...habit, id: 2, name: 'Read' };
  const walk: Habit = { ...habit, id: 3, name: 'Walk' };

  beforeEach(() => {
    localStorage.clear();
    localStorage.setItem('ember.seenReleaseNotes', '0.10.0-beta.1');
    f = mockFetch();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    auth.clearSession();
  });

  const offer = (id: string, suggestPictures?: boolean) =>
    renderSlot(f, { ...me, id, tipsSeen: allSeen, suggestPictures }, () => (
      <TipSlot place="top" habits={[stretch, read, walk]} forToday={[stretch, read, walk]} />
    ));
  const question = {
    name: '“Read” could show as a small shelf that fills with each session. Try it?',
  };

  it('asks about the first matching habit and "Show me" sets its picture, then says where to switch it off', async () => {
    await offer('pictures-yes');

    expect(await screen.findByRole('heading', question)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Show me' }));
    const patch = await f.next('PATCH', '/api/habits/2');
    expect(patch.body).toEqual({ picture: 'shelf', pictureOfferAnswered: true });
    patch.respond(200, {
      ...read,
      picture: 'shelf',
      portionAmount: 15,
      portionUnit: 'min',
      pictureOfferAnswered: true,
    });

    expect(
      await screen.findByRole('heading', { name: '“Read” shows its picture now.' }),
    ).toHaveFocus();
    expect(screen.getByText("You can switch it off in the habit's settings.")).toBeInTheDocument();
    expect(screen.queryByRole('heading', question)).toBeNull();
    expect(readTipMemory('pictures-yes').wait).toBe('2026-11-01');
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss tip' }));
    expect(screen.queryByRole('heading', { name: '“Read” shows its picture now.' })).toBeNull();
  });

  it('records "Not for this habit" on the habit only, hiding the question at once', async () => {
    await offer('pictures-no');

    await userEvent.click(await screen.findByRole('button', { name: 'Not for this habit' }));

    expect(screen.queryByRole('heading', question)).toBeNull();
    const patch = await f.next('PATCH', '/api/habits/2');
    expect(patch.body).toEqual({ pictureOfferAnswered: true });
    patch.respond(200, { ...read, pictureOfferAnswered: true });
    expect(f.all.some((c) => c.url.includes('/api/me/tips'))).toBe(false);
    expect(readTipMemory('pictures-no').wait).toBe('2026-11-01');
  });

  it('brings the question back, saying so, when "Not for this habit" did not save', async () => {
    await offer('pictures-no-fail');

    await userEvent.click(await screen.findByRole('button', { name: 'Not for this habit' }));
    expect(screen.queryByRole('heading', question)).toBeNull();
    (await f.next('PATCH', '/api/habits/2')).fail();

    expect(await screen.findByRole('heading', question)).toBeInTheDocument();
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(readTipMemory('pictures-no-fail').wait).toBeNull();
  });

  it('keeps the question and shows no note when "Show me" did not save', async () => {
    await offer('pictures-yes-fail');

    await userEvent.click(await screen.findByRole('button', { name: 'Show me' }));
    (await f.next('PATCH', '/api/habits/2')).fail();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByRole('heading', question)).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: '“Read” shows its picture now.' })).toBeNull();
    expect(readTipMemory('pictures-yes-fail').wait).toBeNull();
  });

  it('never asks once the account turned picture suggestions off', async () => {
    await offer('pictures-off', false);
    await act(async () => {});

    expect(screen.queryByRole('heading', question)).toBeNull();
    expect(screen.queryByRole('button', { name: 'Show me' })).toBeNull();
  });
});
