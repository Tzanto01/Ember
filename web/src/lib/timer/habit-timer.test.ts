import 'fake-indexeddb/auto';
import { IDBFactory } from 'fake-indexeddb';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiClient } from '../api-client';
import { AuthService } from '../auth';
import { DeviceStore } from '../device-store';
import { Http } from '../http';
import type { AnyHabit, Habit } from '../models';
import { OfflineQueue } from '../offline-queue';
import { Store } from '../store';
import { type FetchMock, habit, letGoHabit, me, mockFetch } from '@/test/test-support';
import { HabitTimer, type TimerNotifier, type TimerRecord } from './habit-timer';
import { timerSound } from './timer-sound';

const key = 'ember.habitTimer';
const minute = 60_000;
const start = Date.parse('2026-11-01T10:00:00Z');
const walk: Habit = { ...habit, timerMinutes: 30 };
const record: TimerRecord = {
  userId: 'u1',
  habitId: 1,
  name: 'Walk',
  minutes: 30,
  date: '2026-11-01',
  endsAt: start + 30 * minute,
  remainingMs: null,
};

class FakeNotifier implements TimerNotifier {
  readonly allowed = new Store(false);
  readonly late = new Store(false);
  /** The end the device would notify at, or null. */
  scheduled: number | null = null;
  asked = 0;
  ends: TimerRecord[] = [];
  tells = true;
  check = async () => undefined;
  running = async (r: TimerRecord, ask = false) => {
    this.scheduled = r.endsAt;
    if (ask) this.asked++;
  };
  ended = async (r: TimerRecord) => {
    this.ends.push(r);
    return this.tells;
  };
  clear = async () => {
    this.scheduled = null;
  };
}

describe('HabitTimer', () => {
  let fetch: FetchMock;
  let device: DeviceStore;
  let queue: OfflineQueue;
  let auth: AuthService;
  let notifier: FakeNotifier;
  let timer: HabitTimer;
  let confirmed: number;
  let stops: Array<() => void>;

  /** A fresh page of the app on this device, as after a reload, with `user` signed in. */
  function open(user: typeof me | null = me): HabitTimer {
    const api = new ApiClient(new Http({ native: false, apiBase: '' }), queue);
    auth = new AuthService(api, queue, null, null);
    const opened = new HabitTimer({
      api,
      device,
      queue,
      me: auth.me,
      notifier,
      onConfirmed: () => confirmed++,
    });
    auth.timer = opened;
    api.onHabits = (habits) => opened.reconcile(habits);
    api.onCheckIn = (id, date) => opened.checkedIn(id, date);
    stops.push(opened.listen());
    if (user) auth.setMe(user);
    queue.userId.set(user?.id ?? null);
    return opened;
  }

  const phase = () => timer.view.get()?.phase ?? null;
  const writes = () => fetch.all.filter((call) => call.method !== 'GET');
  const sleep = (until: number) => vi.setSystemTime(until);
  const wake = () => window.dispatchEvent(new Event('focus'));
  const store = (value: unknown) => localStorage.setItem(key, JSON.stringify(value));
  const answerHabits = async (habits: AnyHabit[]) => (await fetch.next('GET', '/api/habits')).respond(200, habits);

  beforeEach(() => {
    (globalThis as unknown as { indexedDB: unknown }).indexedDB = new IDBFactory();
    localStorage.clear();
    vi.useFakeTimers({ toFake: ['Date', 'setTimeout', 'clearTimeout'], now: start });
    fetch = mockFetch();
    device = new DeviceStore();
    queue = new OfflineQueue(device);
    notifier = new FakeNotifier();
    confirmed = 0;
    stops = [];
    timer = open();
  });

  afterEach(() => {
    stops.forEach((stop) => stop());
    vi.useRealTimers();
    vi.unstubAllGlobals();
    localStorage.clear();
    timerSound.set(false);
  });

  /** Lets IndexedDB and fetch settle: they need the real timers. */
  const ended = () => {
    timer.start(walk);
    sleep(start + 31 * minute);
    wake();
    vi.useRealTimers();
    vi.useFakeTimers({ toFake: ['Date'], now: start + 31 * minute });
  };

  describe('wall clock', () => {
    it('runs to its end on time and never checks anything in by itself', async () => {
      timer.start(walk);
      await vi.advanceTimersByTimeAsync(29 * minute);
      expect(phase()).toBe('running');
      expect(timer.remaining()).toBe(minute);

      await vi.advanceTimersByTimeAsync(minute);

      expect(phase()).toBe('ended');
      expect(timer.remaining()).toBe(0);
      expect(notifier.ends).toHaveLength(1);
      await vi.advanceTimersByTimeAsync(60 * minute);
      expect(fetch.all).toEqual([]);
    });

    it('has ended when the device wakes after the end, without a late notification', async () => {
      timer.start(walk);
      sleep(start + 3 * 60 * minute);
      wake();

      expect(phase()).toBe('ended');
      expect(notifier.ends).toEqual([]);
    });

    it('keeps its end through a reload, also while the timers of a background tab stood still', () => {
      timer.start(walk);
      sleep(start + 10 * minute);
      timer = open();

      expect(phase()).toBe('running');
      expect(timer.remaining()).toBe(20 * minute);
      expect(notifier.scheduled).toBe(start + 30 * minute);
    });

    it('keeps the day it started on across midnight in the account zone', () => {
      sleep(Date.parse('2026-11-01T22:50:00Z'));
      timer.start(walk);
      sleep(Date.parse('2026-11-01T23:30:00Z'));
      wake();

      expect(timer.view.get()).toMatchObject({ phase: 'ended', record: { date: '2026-11-01' } });
    });

    it('takes its full length across a change to winter time', async () => {
      sleep(Date.parse('2026-10-25T00:45:00Z'));
      timer.start(walk);
      await vi.advanceTimersByTimeAsync(29 * minute);
      expect(phase()).toBe('running');
      await vi.advanceTimersByTimeAsync(minute);

      expect(phase()).toBe('ended');
      expect(timer.view.get()?.record.date).toBe('2026-10-25');
    });
  });

  it('pauses with what is left, however long, and resumes from there', async () => {
    timer.start(walk);
    sleep(start + 10 * minute);
    timer.pause();
    expect(phase()).toBe('paused');
    expect(notifier.scheduled).toBeNull();

    sleep(start + 24 * 60 * minute);
    wake();
    expect(timer.remaining()).toBe(20 * minute);

    timer.resume();
    expect(phase()).toBe('running');
    expect(notifier.scheduled).toBe(start + (24 * 60 + 20) * minute);
    expect(notifier.asked).toBe(1);
  });

  it('holds one timer: starting another replaces it', () => {
    timer.start(walk);
    timer.start({ id: 2, name: 'Read', timerMinutes: 10 });

    expect(timer.view.get()?.record).toMatchObject({ habitId: 2, name: 'Read' });
    expect(notifier.scheduled).toBe(start + 10 * minute);
  });

  it.each([null, undefined, 0, 241, 1.5])('starts nothing with a length of %s', (timerMinutes) => {
    timer.start({ id: 1, name: 'Walk', timerMinutes });
    expect(phase()).toBeNull();
  });

  it('leaves nothing after a stop', () => {
    timer.start(walk);
    timer.stop();

    expect(phase()).toBeNull();
    expect(localStorage.getItem(key)).toBeNull();
    expect(notifier.scheduled).toBeNull();
    expect(writes()).toEqual([]);
  });

  describe('ownership', () => {
    it('is gone at the end of the session, with the sound setting', () => {
      timerSound.set(true);
      timer.start(walk);
      auth.clearSession();

      expect(phase()).toBeNull();
      expect(localStorage.getItem(key)).toBeNull();
      expect(notifier.scheduled).toBeNull();
      expect(timerSound.get()).toBe(false);
    });

    it('is gone after a wipe of the device (account deletion)', async () => {
      timer.start(walk);
      vi.useRealTimers();
      await auth.wipeDevice();

      expect(localStorage.getItem(key)).toBeNull();
      expect(notifier.scheduled).toBeNull();
    });

    it('is gone when the app opens signed out', async () => {
      timer.start(walk);
      timer = open(null);
      const loaded = auth.loadMe();
      (await fetch.next('GET', '/api/me')).respond(401);
      await loaded;

      expect(localStorage.getItem(key)).toBeNull();
      expect(notifier.scheduled).toBeNull();
    });

    it('is gone once another user is signed in', () => {
      timer.start(walk);
      timer = open({ ...me, id: 'u2' });

      expect(phase()).toBeNull();
      expect(localStorage.getItem(key)).toBeNull();
      expect(notifier.scheduled).toBeNull();
    });

    it.each([
      ['the previous user', me],
      ['an old consent state', { ...me, consent: { ...me.consent, given: false, withdrawnAt: '2026-11-01T09:00:00Z' } }],
    ])('is hidden, not removed, by a tab that still knows %s', (_case, stale) => {
      timer = open(stale);
      const kept = { ...record, userId: stale === me ? 'u2' : 'u1' };
      store(kept);
      notifier.scheduled = kept.endsAt;
      window.dispatchEvent(new StorageEvent('storage', { key }));
      wake();
      auth.setMe({ ...stale });

      expect(phase()).toBeNull();
      expect(JSON.parse(localStorage.getItem(key) ?? 'null')).toEqual(kept);
      expect(notifier.scheduled).toBe(kept.endsAt);
    });

    it('goes when this page changes to another user, and stays when it is theirs', () => {
      timer.start(walk);
      auth.setMe({ ...me, id: 'u2' });
      expect(localStorage.getItem(key)).toBeNull();

      store(record);
      auth.setMe(me);
      expect(phase()).toBe('running');
    });

    it('waits, hidden, while the user is not known yet', () => {
      timer.start(walk);
      timer = open(null);
      expect(phase()).toBeNull();

      auth.setMe(me);
      expect(phase()).toBe('running');
    });

    it('is gone while the consent is withdrawn and cannot start', () => {
      timer.start(walk);
      auth.setMe({ ...me, consent: { ...me.consent, given: false, withdrawnAt: '2026-11-01T10:05:00Z' } });
      expect(localStorage.getItem(key)).toBeNull();
      expect(notifier.scheduled).toBeNull();

      timer.start(walk);
      expect(phase()).toBeNull();
    });

    it.each([
      ['gone', []],
      ['let-go now', [{ ...letGoHabit, id: 1 }]],
    ])('clears when its habit is %s, and takes over a new name', (_case, habits) => {
      timer.start(walk);
      timer.reconcile([{ ...walk, name: 'Stroll' }]);
      expect(timer.view.get()).toMatchObject({ phase: 'running', record: { name: 'Stroll' } });

      timer.reconcile(habits);
      expect(phase()).toBeNull();
      expect(localStorage.getItem(key)).toBeNull();
    });
  });

  describe('storage', () => {
    it.each([
      ['no JSON', '{'],
      ['another shape', '[]'],
      ['a length out of range', JSON.stringify({ ...record, minutes: 241 })],
      ['an end beyond its length', JSON.stringify({ ...record, endsAt: start + 72 * 60 * minute })],
      ['more left than its length', JSON.stringify({ ...record, endsAt: null, remainingMs: 31 * minute })],
      ['both an end and time left', JSON.stringify({ ...record, remainingMs: minute })],
      ['neither', JSON.stringify({ ...record, endsAt: null })],
      ['an unreal date', JSON.stringify({ ...record, date: '2026-02-30' })],
    ])('reads %s as no timer and removes it', (_case, raw) => {
      localStorage.setItem(key, raw);
      timer = open();

      expect(phase()).toBeNull();
      expect(localStorage.getItem(key)).toBeNull();
    });

    it('works without a storage that keeps it', async () => {
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
        throw new Error('full');
      });
      timer.start(walk);
      await vi.advanceTimersByTimeAsync(30 * minute);
      expect(phase()).toBe('ended');
      vi.restoreAllMocks();
    });

    it('follows the other tabs of this browser', () => {
      const other = () => window.dispatchEvent(new StorageEvent('storage', { key }));
      store(record);
      other();
      expect(phase()).toBe('running');

      store({ ...record, endsAt: null, remainingMs: 5 * minute });
      other();
      expect(phase()).toBe('paused');

      localStorage.removeItem(key);
      other();
      expect(phase()).toBeNull();
    });

    it('tells the end once, not again in a tab that sees it later', async () => {
      timer.start(walk);
      await vi.advanceTimersByTimeAsync(30 * minute);
      open();

      expect(notifier.ends).toHaveLength(1);
    });
  });

  it('is not told by a tab that finds it told by another one while it looks', async () => {
    timer.start(walk);
    sleep(start + 30 * minute);
    const read = Storage.prototype.getItem;
    let reads = 0;
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, name) {
      const raw = read.call(this, name);
      if (name === key && ++reads === 2) store({ ...record, told: true });
      return raw;
    });
    wake();
    vi.restoreAllMocks();

    expect(phase()).toBe('ended');
    expect(notifier.ends).toEqual([]);
  });

  describe('the check-in it offers', () => {
    beforeEach(() => ended());

    it('is there while the day is open and not done', async () => {
      const offer = timer.offer();
      await answerHabits([walk]);
      await expect(offer).resolves.toEqual({ canCheckIn: true, yesterday: false });
    });

    it.each<[string, Partial<Habit>]>([
      ['done meanwhile', { today: { ...habit.today, state: 'done', completed: true, count: 1 } }],
      ['paused that day', { pausedFrom: '2026-11-01', pausedUntil: '2026-11-05', pausedToday: true }],
      ['shown as paused today', { today: { ...habit.today, state: 'paused' } }],
    ])('is not there for a habit %s', async (_case, change) => {
      const offer = timer.offer();
      await answerHabits([{ ...walk, ...change }]);
      await expect(offer).resolves.toMatchObject({ canCheckIn: false });
      expect(phase()).toBe('ended');
    });

    it('is for yesterday after midnight, by that day’s own state', async () => {
      vi.setSystemTime(Date.parse('2026-11-02T08:00:00Z'));
      const next = { ...walk, today: { ...habit.today, date: '2026-11-02', state: 'done' as const, count: 1 } };
      const offer = timer.offer();
      await answerHabits([next]);
      (await fetch.next('GET', '/api/habits/1/days?from=2026-11-01&to=2026-11-01')).respond(200, [habit.today]);
      await expect(offer).resolves.toEqual({ canCheckIn: true, yesterday: true });

      const done = timer.confirm();
      await answerHabits([next]);
      (await fetch.next('GET', '/api/habits/1/days?from=2026-11-01&to=2026-11-01')).respond(200, [habit.today]);
      const put = await fetch.next('PUT', '/api/habits/1/days/2026-11-01');
      expect(put.body).toEqual({ completed: true });
      put.respond(200, next);
      await expect(done).resolves.toEqual({ status: 'done' });
    });

    it('is not there once the day is closed', async () => {
      vi.setSystemTime(Date.parse('2026-11-03T08:00:00Z'));
      await expect(timer.offer()).resolves.toEqual({ canCheckIn: false, yesterday: false });
      await expect(timer.confirm()).resolves.toEqual({ status: 'unavailable' });
      expect(fetch.all).toEqual([]);
    });

    it('checks in once for two taps and clears the timer', async () => {
      const taps = [timer.confirm(), timer.confirm()];
      await answerHabits([walk]);
      const put = await fetch.next('PUT', '/api/habits/1/days/2026-11-01');
      expect(put.body).toEqual({ completed: true });
      put.respond(200, { ...walk, today: { ...habit.today, state: 'done', completed: true, count: 1 } });

      await expect(Promise.all(taps)).resolves.toEqual([{ status: 'done' }, { status: 'done' }]);
      expect(writes()).toHaveLength(1);
      expect(phase()).toBeNull();
      expect(localStorage.getItem(key)).toBeNull();
      expect(confirmed).toBe(1);
    });

    it('adds one to the count read right before', async () => {
      const done = timer.confirm();
      await answerHabits([{ ...walk, timesPerDay: 3, today: { ...habit.today, state: 'partial', count: 1 } }]);
      const put = await fetch.next('PUT', '/api/habits/1/days/2026-11-01');
      expect(put.body).toEqual({ count: 2 });
      put.respond(200, { ...walk, timesPerDay: 3, today: { ...habit.today, state: 'partial', count: 2 } });
      await expect(done).resolves.toEqual({ status: 'done' });
    });

    it('never goes above the habit’s times a day', async () => {
      const done = timer.confirm();
      await answerHabits([{ ...walk, timesPerDay: 3, today: { ...habit.today, state: 'done', completed: true, count: 3 } }]);
      await expect(done).resolves.toEqual({ status: 'unavailable' });
      expect(writes()).toEqual([]);
    });

    it('counts a check-in waiting in the offline queue', async () => {
      await device.addEdit('u1', { habitId: 1, date: '2026-11-01', completed: null, count: 1 });
      await queue.refreshCount('u1');
      const done = timer.confirm();
      await answerHabits([{ ...walk, timesPerDay: 3 }]);

      await expect(done).resolves.toEqual({ status: 'done' });
      expect((await device.edits('u1')).map((edit) => 'count' in edit && edit.count)).toEqual([1, 2]);
    });

    it('is queued offline, for a habit only the device’s cache still knows', async () => {
      await device.saveHabits({ userId: 'u1', timeZone: me.timezone, habits: [walk] });
      const done = timer.confirm();
      (await fetch.next('GET', '/api/habits')).fail();
      (await fetch.next('PUT', '/api/habits/1/days/2026-11-01')).fail();

      await expect(done).resolves.toEqual({ status: 'done' });
      expect(await device.edits('u1')).toMatchObject([{ habitId: 1, date: '2026-11-01', completed: true }]);
      expect(phase()).toBeNull();
    });

    it('is queued offline when the cached list is from before the timer’s day', async () => {
      const older = { ...walk, today: { ...habit.today, date: '2026-10-31', state: 'done' as const, count: 1 } };
      await device.saveHabits({ userId: 'u1', timeZone: me.timezone, habits: [older] });
      const day = '/api/habits/1/days?from=2026-11-01&to=2026-11-01';
      const offer = timer.offer();
      (await fetch.next('GET', '/api/habits')).fail();
      (await fetch.next('GET', day)).fail();
      await expect(offer).resolves.toEqual({ canCheckIn: true, yesterday: false });

      const done = timer.confirm();
      (await fetch.next('GET', '/api/habits')).fail();
      (await fetch.next('GET', day)).fail();
      (await fetch.next('PUT', '/api/habits/1/days/2026-11-01')).fail();
      await expect(done).resolves.toEqual({ status: 'done' });
      expect(await device.edits('u1')).toMatchObject([{ habitId: 1, date: '2026-11-01', completed: true }]);
    });

    it.each([
      [1, true],
      [3, false],
    ])('offline after midnight with %i a day and the day unseen: %s, so no real count is lowered', async (timesPerDay, canCheckIn) => {
      vi.setSystemTime(Date.parse('2026-11-02T08:00:00Z'));
      const newer = { ...walk, timesPerDay, today: { ...habit.today, date: '2026-11-02' } };
      await device.saveHabits({ userId: 'u1', timeZone: me.timezone, habits: [newer] });
      const offer = timer.offer();
      (await fetch.next('GET', '/api/habits')).fail();
      (await fetch.next('GET', '/api/habits/1/days?from=2026-11-01&to=2026-11-01')).fail();
      await expect(offer).resolves.toEqual({ canCheckIn, yesterday: true });
    });

    it('is not there offline when the device knows nothing of the habit', async () => {
      const offer = timer.offer();
      (await fetch.next('GET', '/api/habits')).fail();
      await expect(offer).resolves.toMatchObject({ canCheckIn: false });
    });

    it('keeps the timer when the server refuses', async () => {
      const done = timer.confirm();
      await answerHabits([walk]);
      (await fetch.next('PUT', '/api/habits/1/days/2026-11-01')).respond(429, {});

      await expect(done).resolves.toMatchObject({ status: 'failed', error: { status: 429 } });
      expect(phase()).toBe('ended');
      expect(confirmed).toBe(0);
    });

    it('writes nothing when the session ended while it was reading', async () => {
      const done = timer.confirm();
      const list = await fetch.next('GET', '/api/habits');
      auth.clearSession();
      list.respond(200, [walk]);

      await expect(done).resolves.toEqual({ status: 'unavailable' });
      expect(writes()).toEqual([]);
    });

    it('is over after a check-in of that habit and day made elsewhere, and of no other', () => {
      timer.checkedIn(2, '2026-11-01');
      timer.checkedIn(1, '2026-10-31');
      expect(phase()).toBe('ended');

      timer.checkedIn(1, '2026-11-01');
      expect(phase()).toBeNull();
    });
  });

  it('keeps running when its habit is checked in meanwhile, and an undone count is no check-in', async () => {
    const api = new ApiClient(new Http({ native: false, apiBase: '' }), queue);
    api.onCheckIn = (id, date) => timer.checkedIn(id, date);
    const several = { ...walk, timesPerDay: 3, today: { ...habit.today, state: 'partial' as const, count: 2 } };
    timer.start(walk);
    timer.checkedIn(1, '2026-11-01');
    expect(phase()).toBe('running');
    ended();

    const listed = api.listHabits();
    await answerHabits([several]);
    await listed;
    const undone = api.setDayCount(1, '2026-11-01', 1);
    (await fetch.next('PUT', '/api/habits/1/days/2026-11-01')).respond(200, several);
    await undone;
    expect(phase()).toBe('ended');

    const added = api.setDayCount(1, '2026-11-01', 3);
    (await fetch.next('PUT', '/api/habits/1/days/2026-11-01')).respond(200, several);
    await added;
    expect(phase()).toBeNull();
  });

  describe('sound', () => {
    const notes = vi.fn();

    beforeEach(() => {
      notes.mockClear();
      const node = { connect: (next: unknown) => next };
      const ramp = { value: 0, setValueAtTime() {}, linearRampToValueAtTime() {}, exponentialRampToValueAtTime() {} };
      vi.stubGlobal(
        'AudioContext',
        class {
          state = 'running';
          currentTime = 0;
          destination = {};
          createOscillator = () => ({ ...node, frequency: ramp, start: notes, stop() {} });
          createGain = () => ({ ...node, gain: ramp });
        },
      );
      notifier.tells = false;
    });

    it.each([
      ['plays when on', true, true, true],
      ['is silent when off', false, true, false],
      ['is silent when the end was seen late', true, false, false],
    ])('%s', async (_case, on, live, plays) => {
      timerSound.set(on);
      timer.start(walk);
      if (live) await vi.advanceTimersByTimeAsync(30 * minute);
      else {
        sleep(start + 33 * minute);
        wake();
        await vi.advanceTimersByTimeAsync(0);
      }
      expect(notes.mock.calls.length > 0).toBe(plays);
    });

    it('is silent in calm mode', async () => {
      const { applyCalm } = await import('../theme');
      timerSound.set(true);
      applyCalm(true);
      timer.start(walk);
      await vi.advanceTimersByTimeAsync(30 * minute);
      expect(notes).not.toHaveBeenCalled();
      applyCalm(false);
    });
  });
});
