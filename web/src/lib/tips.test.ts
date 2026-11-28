import { describe, expect, it } from 'vitest';
import { habit } from '@/test/test-support';
import type { Habit } from './models';
import { pickTip, pictureOfferFor, readTipMemory, type TipFacts, writeTipMemory } from './tips';

const base: TipFacts = {
  today: '2026-11-14',
  firstDay: '2026-11-01',
  waitDay: null,
  seen: [],
  whatsNew: false,
  calm: false,
  native: false,
  account: true,
  showStreaks: true,
  plainJournal: true,
  discordLinked: true,
  habits: 1,
  activeHabits: 1,
  routines: 1,
  checkInDays: 0,
  hasNote: false,
  pictureOffer: false,
};

const everything: Partial<TipFacts> = {
  showStreaks: false,
  hasNote: true,
  activeHabits: 3,
  habits: 3,
  routines: 0,
  checkInDays: 7,
  discordLinked: false,
};

describe('pickTip', () => {
  it('shows nothing when no condition holds', () => {
    expect(pickTip(base)).toBeNull();
  });

  it('picks one tip at a time in the contract order, skipping seen ones', () => {
    const all = { ...base, ...everything };
    expect(pickTip(all)).toBe('streaks');
    expect(pickTip({ ...all, seen: ['streaks'] })).toBe('private-journal');
    expect(pickTip({ ...all, seen: ['streaks', 'private-journal'] })).toBe('routines');
    expect(pickTip({ ...all, seen: ['streaks', 'private-journal', 'routines'] })).toBe('android');
    expect(pickTip({ ...all, seen: ['streaks', 'private-journal', 'routines', 'android'] })).toBe(
      'discord',
    );
    expect(
      pickTip({ ...all, seen: ['streaks', 'private-journal', 'routines', 'android', 'discord'] }),
    ).toBeNull();
  });

  it('waits until the next local day after an answer, except for the streaks question', () => {
    const due = { ...base, ...everything, seen: ['streaks' as const] };
    expect(pickTip({ ...due, waitDay: '2026-11-14' })).toBeNull();
    expect(pickTip({ ...due, waitDay: '2026-11-13' })).toBe('private-journal');
    expect(pickTip({ ...due, seen: [], waitDay: '2026-11-14' })).toBe('streaks');
  });

  it('allows only the streaks question on the account first day', () => {
    const first = { ...base, ...everything, firstDay: '2026-11-14' };
    expect(pickTip(first)).toBe('streaks');
    expect(pickTip({ ...first, seen: ['streaks'] })).toBeNull();
  });

  it('shows nothing while the What new card shows, not even streaks', () => {
    expect(pickTip({ ...base, ...everything, whatsNew: true })).toBeNull();
  });

  it('shows only streaks in calm mode', () => {
    const calm = { ...base, ...everything, calm: true };
    expect(pickTip(calm)).toBe('streaks');
    expect(pickTip({ ...calm, seen: ['streaks'] })).toBeNull();
  });

  it('offers a picture only after every other tip, and only on a later, unanswered day outside calm mode', () => {
    const all = { ...base, ...everything, pictureOffer: true };
    const allSeen = ['streaks', 'private-journal', 'routines', 'android', 'discord'] as const;
    expect(pickTip({ ...all, seen: ['streaks', 'private-journal', 'routines', 'android'] })).toBe(
      'discord',
    );
    expect(pickTip({ ...all, seen: [...allSeen] })).toBe('picture');
    expect(pickTip({ ...base, pictureOffer: true })).toBe('picture');
    expect(pickTip({ ...base, pictureOffer: true, waitDay: '2026-11-14' })).toBeNull();
    expect(pickTip({ ...base, pictureOffer: true, firstDay: '2026-11-14' })).toBeNull();
    expect(pickTip({ ...base, pictureOffer: true, calm: true })).toBeNull();
    expect(pickTip({ ...base, pictureOffer: true, checkInDays: null, native: false })).toBeNull();
  });

  it('asks about streaks only with a habit and streaks off', () => {
    expect(pickTip({ ...base, showStreaks: false })).toBe('streaks');
    expect(pickTip({ ...base, showStreaks: false, habits: 0 })).toBeNull();
  });

  it('offers the private journal only with an account, a plain journal and a saved note', () => {
    const due = { ...base, hasNote: true };
    expect(pickTip(due)).toBe('private-journal');
    expect(pickTip({ ...due, account: false })).toBeNull();
    expect(pickTip({ ...due, plainJournal: false })).toBeNull();
    expect(pickTip({ ...due, hasNote: false })).toBeNull();
  });

  it('offers routines from 3 active habits without a routine', () => {
    const due = { ...base, habits: 3, activeHabits: 3, routines: 0 };
    expect(pickTip(due)).toBe('routines');
    expect(pickTip({ ...due, activeHabits: 2 })).toBeNull();
    expect(pickTip({ ...due, routines: 1 })).toBeNull();
  });

  it('offers Android on the web only, from 3 days with check-ins', () => {
    const due = { ...base, checkInDays: 3 };
    expect(pickTip(due)).toBe('android');
    expect(pickTip({ ...due, checkInDays: 2 })).toBeNull();
    expect(pickTip({ ...due, native: true })).toBeNull();
  });

  it('offers Discord when signed in, not linked and from 7 days with check-ins', () => {
    const due = { ...base, native: true, discordLinked: false, checkInDays: 7 };
    expect(pickTip(due)).toBe('discord');
    expect(pickTip({ ...due, checkInDays: 6 })).toBeNull();
    expect(pickTip({ ...due, discordLinked: true })).toBeNull();
    expect(pickTip({ ...due, account: false })).toBeNull();
  });

  it('waits while a higher tip is still unknown instead of showing a lower one first', () => {
    const due = { ...base, hasNote: null, habits: 3, activeHabits: 3, routines: 0 };
    expect(pickTip(due)).toBeNull();
    expect(pickTip({ ...due, hasNote: false })).toBe('routines');
    expect(
      pickTip({ ...base, routines: null, habits: 3, activeHabits: 3, checkInDays: 3 }),
    ).toBeNull();
  });
});

describe('tip memory', () => {
  it('keeps the first day, the wait day and known pending marks per user, ignoring junk', () => {
    localStorage.clear();
    writeTipMemory('u1', { first: '2026-11-01', pending: ['routines'] });
    writeTipMemory('u1', { wait: '2026-11-14' });
    expect(readTipMemory('u1')).toEqual({
      first: '2026-11-01',
      wait: '2026-11-14',
      pending: ['routines'],
    });
    expect(readTipMemory('u2')).toEqual({ first: null, wait: null, pending: [] });

    localStorage.setItem(
      'ember.tips.u3',
      JSON.stringify({ first: 'soon', pending: ['nope', 'android'] }),
    );
    expect(readTipMemory('u3')).toEqual({ first: null, wait: null, pending: ['android'] });
    localStorage.setItem('ember.tips.u4', '{');
    expect(readTipMemory('u4')).toEqual({ first: null, wait: null, pending: [] });
  });
});

describe('pictureOfferFor', () => {
  const walk: Habit = { ...habit, id: 1, name: 'Evening walk' };
  const read: Habit = { ...habit, id: 2, name: 'Read' };

  it("offers the first matching habit in Today's order, with the picture its name suggests", () => {
    expect(pictureOfferFor([{ ...habit, id: 3, name: 'Stretch' }, read, walk], undefined)).toEqual({
      habit: read,
      picture: 'shelf',
    });
  });

  it('skips habits that have a picture, were asked already or are paused, and offers nothing when switched off', () => {
    expect(pictureOfferFor([{ ...read, picture: 'kindling' }, walk], true)?.habit).toBe(walk);
    expect(pictureOfferFor([{ ...read, pictureOfferAnswered: true }, walk], true)?.habit).toBe(
      walk,
    );
    expect(pictureOfferFor([{ ...read, pausedToday: true }, walk], true)?.habit).toBe(walk);
    expect(pictureOfferFor([read, walk], false)).toBeNull();
  });
});
