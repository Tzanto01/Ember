import 'fake-indexeddb/auto';
import { IDBFactory } from 'fake-indexeddb';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiClient } from '../api-client';
import { DeviceStore } from '../device-store';
import { Http } from '../http';
import { OfflineQueue } from '../offline-queue';
import { ApiError, problemToMessage } from '../problem';
import { LocalBackend, newLocalData } from './backend';
import { LocalStore } from './store';
import type { LocalData, LocalHabit } from './types';

const now = new Date('2026-11-01T10:00:00Z');
const today = '2026-11-01';

function localHabit(id: number, overrides: Partial<LocalHabit> = {}): LocalHabit {
  return {
    id,
    name: `Habit ${id}`,
    frequency: 'daily',
    weeklyTarget: null,
    days: null,
    timesPerDay: 1,
    reminderTime: null,
    reminderChannels: [],
    pausedFrom: null,
    pausedUntil: null,
    createdAt: '2026-10-20T08:00:00Z',
    routineId: null,
    routinePosition: null,
    ...overrides,
  };
}

function seed(habits: LocalHabit[] = []): LocalData {
  const data = newLocalData(
    { timezone: 'Europe/Berlin', theme: null },
    new Date('2026-10-01T00:00:00Z'),
  );
  data.habits = habits;
  data.profile.nextIds.habit = habits.length + 1;
  return data;
}

async function rejection(promise: Promise<unknown>): Promise<ApiError> {
  const error = await promise.then(
    () => null,
    (e: unknown) => e,
  );
  expect(error).toBeInstanceOf(ApiError);
  return error as ApiError;
}

const local = (store: LocalStore) => new LocalBackend(store, { native: false, now: () => now });

describe('LocalBackend through ApiClient', () => {
  let store: LocalStore;
  let api: ApiClient;
  let fetchSpy: ReturnType<typeof vi.fn>;

  function connect(native = false): void {
    const http = new Http({ native: false, apiBase: '' });
    http.local = new LocalBackend(store, { native, now: () => now }).handle;
    api = new ApiClient(http, new OfflineQueue(new DeviceStore()));
  }

  beforeEach(() => {
    (globalThis as unknown as { indexedDB: unknown }).indexedDB = new IDBFactory();
    fetchSpy = vi.fn(() => Promise.reject(new TypeError('no network in local mode')));
    vi.stubGlobal('fetch', fetchSpy);
    store = new LocalStore();
    connect();
  });

  afterEach(() => {
    expect(fetchSpy).not.toHaveBeenCalled();
    vi.unstubAllGlobals();
  });

  it('checks in past days and today and the habit, its days and the day stats follow, all without fetch', async () => {
    await store.replace(seed([localHabit(1)]));

    await api.setDay(1, '2026-10-31', true);
    const habit = await api.setDay(1, today, true);

    expect(habit.today).toEqual({ date: today, state: 'done', completed: true, count: 1 });
    expect(habit.stats.streak).toBe(2);
    expect((await api.getHabit(1)).today.state).toBe('done');
    expect((await api.getDays(1, '2026-10-31', today)).map((d) => d.state)).toEqual([
      'done',
      'done',
    ]);
    expect(await api.dayStats(today, today)).toEqual([
      { date: today, scheduled: 1, done: 1, partial: 0, showedUp: true },
    ]);

    const cleared = await api.clearDay(1, today);
    expect(cleared.today.state).toBe('pending');
  });

  it('stores a language choice on the device, reads it back as the profile language and rejects anything but en or de', async () => {
    await store.replace(seed());
    vi.stubGlobal('navigator', { languages: ['en-GB'], language: 'en-GB' });

    expect((await api.getMe()).language).toBe('en');
    expect((await api.updateMe({ language: 'de' })).language).toBe('de');
    expect(localStorage.getItem('ember.language')).toBe('de');
    expect((await api.getMe()).language).toBe('de');

    const bad = await rejection(api.updateMe({ language: 'fr' } as never));
    expect(bad.status).toBe(400);
    expect(bad.type).toBe('validation');
    expect(bad.problem?.errors?.['language']).toBeTruthy();
    expect(localStorage.getItem('ember.language')).toBe('de');
    localStorage.clear();
  });

  it('answers bad input with the problem types the screens read', async () => {
    await store.replace(seed([localHabit(1, { timesPerDay: 2 })]));

    const name = await rejection(api.createHabit({ name: '   ', frequency: 'daily' }));
    expect(name.status).toBe(400);
    expect(name.type).toBe('validation');
    expect(problemToMessage(name).fields['name']).toBeTruthy();

    expect((await rejection(api.setDay(1, '2026-11-02', true))).type).toBe('in_future');
    expect((await rejection(api.setDay(1, '2026-10-19', true))).type).toBe('before_habit_start');
    expect((await rejection(api.setDayCount(1, today, 3))).status).toBe(400);
    expect((await rejection(api.getHabit(99))).type).toBe('not_found');
    expect(
      (await rejection(api.updateMe({ timezone: 'Mars/Olympus' }))).problem?.errors?.['timezone'],
    ).toBeTruthy();
    expect(
      (
        await rejection(
          api.createHabit({
            name: 'All week',
            frequency: 'weekdays',
            days: [...'1234567'].map(() => 'mon'),
          }),
        )
      ).problem?.errors?.['days'],
    ).toBeTruthy();
  });

  it('stops at 25 habits with habit_limit', async () => {
    await store.replace(seed(Array.from({ length: 25 }, (_, i) => localHabit(i + 1))));

    const error = await rejection(api.createHabit({ name: 'One more', frequency: 'daily' }));

    expect(error.status).toBe(409);
    expect(error.type).toBe('habit_limit');
  });

  it('recomputes today after timesPerDay rises, so a done day becomes partial', async () => {
    await store.replace(seed([localHabit(1, { timesPerDay: 2 })]));
    await api.setDayCount(1, today, 2);

    const habit = await api.updateHabit(1, { timesPerDay: 3 });

    expect(habit.timesPerDay).toBe(3);
    expect(habit.today.state).toBe('partial');
  });

  it('keeps several reminder times sorted on the device, the earliest when timesPerDay drops, and refuses too many', async () => {
    connect(true);
    await store.replace(seed([]));

    const created = await api.createHabit({
      name: 'Water',
      frequency: 'daily',
      timesPerDay: 3,
      reminderTimes: ['20:00', '08:00', '13:00'],
      reminderChannels: [],
    });
    expect(created).toMatchObject({
      reminderTime: '08:00',
      reminderTimes: ['08:00', '13:00', '20:00'],
      reminderChannels: [],
    });
    expect((await api.getHabit(created.id)).reminderTimes).toEqual(['08:00', '13:00', '20:00']);

    const fewer = await api.updateHabit(created.id, { timesPerDay: 2 });
    expect(fewer.reminderTimes).toEqual(['08:00', '13:00']);

    const tooMany = await rejection(
      api.updateHabit(created.id, { reminderTimes: ['07:00', '12:00', '18:00'] }),
    );
    expect(tooMany.problem?.errors?.['reminderTimes']).toBeTruthy();
    const cleared = await api.updateHabit(created.id, { reminderTimes: [] });
    expect(cleared).toMatchObject({ reminderTime: null, reminderTimes: [] });
  });

  it('keeps a habit in one routine at a time, in the listed order, and caps routines at 10', async () => {
    await store.replace(seed([localHabit(1), localHabit(2), localHabit(3)]));

    const morning = await api.createRoutine({ name: 'Morning', habitIds: [3, 1] });
    const evening = await api.createRoutine({ name: 'Evening', habitIds: [1] });

    expect(morning.habitIds).toEqual([3, 1]);
    expect(evening.habitIds).toEqual([1]);
    expect((await api.listRoutines()).map((r) => r.habitIds)).toEqual([[3], [1]]);
    expect((await api.getHabit(1)).routineId).toBe(evening.id);
    expect((await rejection(api.createRoutine({ name: 'Ghost', habitIds: [42] }))).type).toBe(
      'not_found',
    );

    for (let i = 0; i < 8; i++) await api.createRoutine({ name: `R${i}` });
    const full = await rejection(api.createRoutine({ name: 'Eleventh' }));
    expect(full.type).toBe('conflict');
    expect(full.problem?.detail).toBeTruthy();

    await api.deleteRoutine(evening.id);
    expect((await api.getHabit(1)).routineId).toBeNull();
  });

  it('saves plaintext notes with own moods and refuses archived or unknown moods', async () => {
    await store.replace(seed([localHabit(1)]));
    const calm = await api.createMood({ label: '  Calm  ', emoji: '🌿' });
    const old = await api.createMood({ label: 'Old' });
    await api.updateMood(old.id, { archived: true });

    const saved = await api.saveDayNote(today, {
      text: ' A slow walk.\nThen tea. ',
      mood: `custom:${calm.id}`,
    });

    expect(calm).toMatchObject({ label: 'Calm', emoji: '🌿', archived: false });
    expect(saved.note).toEqual({
      date: today,
      text: 'A slow walk.\nThen tea.',
      mood: `custom:${calm.id}`,
    });
    expect(await api.listDayNotes()).toEqual([saved.note]);
    expect((await rejection(api.saveDayNote(today, { mood: `custom:${old.id}` }))).type).toBe(
      'validation',
    );
    expect((await rejection(api.saveDayNote(today, { mood: 'custom:99' }))).type).toBe(
      'validation',
    );
    expect((await rejection(api.saveDayNote('2026-11-02', { text: 'Tomorrow' }))).type).toBe(
      'in_future',
    );
  });

  it('saves a note for today without habits, while other days still need a habit and a low day stays refused', async () => {
    await store.replace(seed());

    const saved = await api.saveDayNote(today, { text: 'Rough morning.' });

    expect(saved.note).toEqual({ date: today, text: 'Rough morning.', mood: null });
    expect((await rejection(api.saveDayNote('2026-10-31', { text: 'Yesterday' }))).type).toBe(
      'before_habit_start',
    );
    expect((await rejection(api.markLowDay(today))).type).toBe('before_habit_start');
  });

  it('writes showStreaks false for a new profile, reads a missing one as true and saves the switch', async () => {
    await store.replace(seed());
    expect((await api.getMe()).showStreaks).toBe(false);

    const older = seed();
    delete older.profile.showStreaks;
    await store.replace(older);
    expect((await api.getMe()).showStreaks).toBe(true);

    expect((await api.updateMe({ showStreaks: false })).showStreaks).toBe(false);
    expect((await store.load())?.profile.showStreaks).toBe(false);
    const error = await rejection(api.updateMe({ showStreaks: 'no' as unknown as boolean }));
    expect(error.problem?.errors).toEqual({ showStreaks: ['Must be true or false.'] });
  });

  it('starts a new profile with no seen tips, reads a missing list by whether habits exist, and marks tips on the device', async () => {
    await store.replace(seed([localHabit(1)]));
    expect((await api.getMe()).tipsSeen).toEqual([]);

    const older = seed([localHabit(1)]);
    delete older.profile.tipsSeen;
    await store.replace(older);
    expect((await api.getMe()).tipsSeen).toEqual(['streaks']);
    const empty = seed();
    delete empty.profile.tipsSeen;
    await store.replace(empty);
    expect((await api.getMe()).tipsSeen).toEqual([]);

    await api.markTipSeen('routines');
    await api.markTipSeen('routines');
    expect((await store.load())?.profile.tipsSeen).toEqual(['routines']);
    expect((await rejection(api.markTipSeen('nope' as 'routines'))).problem?.errors).toEqual({
      id: ['Unknown tip.'],
    });
  });

  it('marks the streaks question seen when showStreaks is saved either way', async () => {
    await store.replace(seed([localHabit(1)]));

    expect((await api.updateMe({ showStreaks: false })).tipsSeen).toEqual(['streaks']);
  });

  it('reads a missing calm mode as off and saves the switch on the device', async () => {
    await store.replace(seed());
    expect((await api.getMe()).calmMode).toBe(false);

    expect((await api.updateMe({ calmMode: true })).calmMode).toBe(true);
    expect((await store.load())?.profile.calmMode).toBe(true);
    const error = await rejection(api.updateMe({ calmMode: 'yes' as unknown as boolean }));
    expect(error.problem?.errors).toEqual({ calmMode: ['Must be true or false.'] });
  });

  it('keeps the Today order on the device, trimmed like the API: own keys once, no routine members, null resets', async () => {
    await store.replace(
      seed([localHabit(1), localHabit(2, { routineId: 1, routinePosition: 0 }), localHabit(3)]),
    );
    await api.createRoutine({ name: 'Morning', habitIds: [2] });
    expect((await api.getMe()).todayOrder).toBeNull();

    const kept = await api.setTodayOrder(['h:3', 'r:1', 'h:2', 'h:3', 'h:99', 'r:7', 'x:1', 'h:1']);

    expect(kept).toEqual(['h:3', 'r:1', 'h:1']);
    expect((await api.getMe()).todayOrder).toEqual(['h:3', 'r:1', 'h:1']);
    expect(await api.setTodayOrder(null)).toBeNull();
    expect((await api.getMe()).todayOrder).toBeNull();
    const tooLong = await rejection(api.setTodayOrder(Array.from({ length: 501 }, () => 'h:1')));
    expect([tooLong.status, tooLong.type]).toEqual([400, 'validation']);
  });

  it('saves "Offer a note after check-in" on the device, off until chosen', async () => {
    await store.replace(seed());
    expect((await api.getMe()).offerHabitNote).toBe(false);
    expect((await api.updateMe({ offerHabitNote: true })).offerHabitNote).toBe(true);
    expect((await api.getMe()).offerHabitNote).toBe(true);
  });

  it('keeps plaintext habit notes per habit and day within the API rules, and drops them with their habit', async () => {
    await store.replace(seed([localHabit(1), localHabit(2)]));

    expect(await api.saveHabitNote(1, today, { text: '  Felt heavy, went anyway.  ' })).toEqual({
      habitId: 1,
      date: today,
      text: 'Felt heavy, went anyway.',
    });
    await api.saveHabitNote(2, '2026-10-30', { text: 'Short one.' });
    expect(await api.listHabitNotes('2026-10-01', today, 1)).toEqual([
      { habitId: 1, date: today, text: 'Felt heavy, went anyway.' },
    ]);
    expect((await api.listHabitNotes('2026-10-01', today)).map((n) => n.habitId)).toEqual([2, 1]);

    expect((await rejection(api.saveHabitNote(1, '2026-11-02', { text: 'Later' }))).type).toBe(
      'in_future',
    );
    expect((await rejection(api.saveHabitNote(1, '2026-10-01', { text: 'Early' }))).type).toBe(
      'before_habit_start',
    );
    expect((await rejection(api.saveHabitNote(1, today, { text: 'a'.repeat(281) }))).status).toBe(
      400,
    );
    expect((await rejection(api.saveHabitNote(9, today, { text: 'Nobody' }))).type).toBe(
      'not_found',
    );
    expect(await api.saveHabitNote(1, today, { text: 'a'.repeat(280) })).toMatchObject({
      text: 'a'.repeat(280),
    });

    await api.saveHabitNote(1, today, null);
    expect(await api.listHabitNotes('2026-10-01', today, 1)).toEqual([]);
    await api.deleteHabit(2);
    expect(await api.listHabitNotes('2026-10-01', today)).toEqual([]);
  });

  it('caps active moods at 20 with conflict', async () => {
    await store.replace(seed());
    for (let i = 0; i < 20; i++) await api.createMood({ label: `Mood ${i}` });

    expect((await rejection(api.createMood({ label: 'Twenty-one' }))).type).toBe('conflict');
  });

  it('answers me for the device and refuses what needs a server', async () => {
    const data = seed();
    await store.replace(data);

    expect(await api.getMe()).toMatchObject({
      id: data.profile.userId,
      email: null,
      hasPassword: false,
      timezone: 'Europe/Berlin',
      timezoneSet: true,
      localToday: today,
      consent: { given: true, policyVersion: 'device', currentPolicyVersion: 'device' },
      journal: { encrypted: false },
    });
    expect((await rejection(api.getJournalKey())).status).toBe(404);
    expect((await rejection(api.twoFactorStatus())).status).toBe(404);
    expect((await rejection(api.importData({}))).status).toBe(404);
    expect((await rejection(api.getVapidPublicKey())).status).toBe(501);
    expect((await rejection(api.exportData('csv'))).status).toBe(400);
    await api.antiforgery();
  });

  it('keeps the second reminder setting on the device, off until chosen, and refuses other values', async () => {
    await store.replace(seed());

    expect((await api.getMe()).secondReminderMinutes).toBe(0);
    expect((await api.updateMe({ secondReminderMinutes: 30 })).secondReminderMinutes).toBe(30);
    expect((await store.load())?.profile.secondReminderMinutes).toBe(30);
    expect((await api.updateMe({ graceDaysPerWeek: 2 })).secondReminderMinutes).toBe(30);
    const error = await rejection(api.updateMe({ secondReminderMinutes: 45 as 30 }));
    expect(error.problem?.errors).toEqual({
      secondReminderMinutes: ['Must be 0, 15, 30, 60 or 120.'],
    });
    expect((await api.getMe()).secondReminderMinutes).toBe(30);
  });

  it('exports the device data as a format v2 backup file without recording the backup time', async () => {
    await store.replace(seed([localHabit(1)]));

    const file = await api.exportData('json');

    expect(file.fileName).toBe('ember-backup-2026-11-01.json');
    const doc = JSON.parse(await file.blob.text());
    expect(doc.format_version).toBe(2);
    expect(doc.habits[0]).toMatchObject({
      id: 1,
      name: 'Habit 1',
      times_per_day: 1,
      routine_id: null,
    });
    expect((await store.load())?.profile.lastBackupAt).toBeNull();
  });

  it('clamps exported check-in counts to the times per day', async () => {
    const data = seed([localHabit(1, { timesPerDay: 2 })]);
    data.logs = [{ habitId: 1, date: '2026-10-25', completed: true, count: 5 }];
    await store.replace(data);

    const doc = JSON.parse(await (await api.exportData('json')).blob.text());

    expect(doc.logs).toEqual([{ habit_id: 1, date: '2026-10-25', completed: true, count: 2 }]);
  });

  describe('let-go habits', () => {
    const letGo = (id = 1) => localHabit(id, { kind: 'letGo' });

    it('creates a let-go habit as daily and records, counts, merges and undoes slips without fetch', async () => {
      await store.replace(seed());

      const created = await api.createHabit({ name: 'Coffee', kind: 'letGo', frequency: 'daily' });
      expect(created).toMatchObject({ kind: 'letGo', frequency: 'daily', timesPerDay: 1 });

      await api.addSlip(created.id, '2026-11-01', 'tired');
      const after = await api.addSlip(created.id, '2026-11-01', 'sad');
      expect(after.kind === 'letGo' && after.today).toMatchObject({
        date: today,
        state: 'slipped',
        slips: 2,
      });
      expect(await api.getSlips(created.id, '2026-10-30', today)).toEqual([
        { date: today, count: 2, reasons: ['tired', 'sad'] },
      ]);

      const undone = await api.clearSlips(created.id, today);
      expect(undone.kind === 'letGo' && undone.today.state).toBe('free');
      expect(await api.getSlips(created.id, '2026-10-30', today)).toEqual([]);
      expect((await api.clearSlips(created.id, today)).id).toBe(created.id);
      expect(fetchSpy).not.toHaveBeenCalled();
    });

    it('answers a bad slip with a validation problem on the field at fault', async () => {
      await store.replace(seed([letGo(1), localHabit(2)]));

      const reason = await rejection(api.addSlip(1, today, 'angry' as never));
      expect(reason.status).toBe(400);
      expect(reason.type).toBe('validation');
      expect(reason.problem?.errors?.['reason']).toBeTruthy();
      expect(
        (await rejection(api.addSlip(1, '2026-11-02'))).problem?.errors?.['date'],
      ).toBeTruthy();
      expect(
        (await rejection(api.addSlip(1, '2026-10-19'))).problem?.errors?.['date'],
      ).toBeTruthy();
      expect((await rejection(api.addSlip(2, today))).problem?.errors?.['kind']).toBeTruthy();
      expect((await rejection(api.clearSlips(2, today))).problem?.errors?.['kind']).toBeTruthy();
      expect((await rejection(api.addSlip(99, today))).type).toBe('not_found');
      expect(await api.getSlips(2, '2026-10-30', today)).toEqual([]);
      expect((await store.load())?.slips ?? []).toEqual([]);
    });

    it('refuses check-ins on a let-go habit with a problem on kind', async () => {
      await store.replace(seed([letGo()]));

      expect((await rejection(api.setDay(1, today, true))).problem?.errors?.['kind']).toBeTruthy();
    });

    it('refuses a schedule, a second reminder time or a routine for a let-go habit, naming the field', async () => {
      await store.replace(seed([letGo(1), localHabit(2)]));

      const field = async (body: Parameters<typeof api.createHabit>[0]) =>
        Object.keys((await rejection(api.createHabit(body))).problem?.errors ?? {});
      expect(
        await field({ name: 'A', kind: 'letGo', frequency: 'weekly', weeklyTarget: 2 }),
      ).toContain('frequency');
      expect(await field({ name: 'A', kind: 'letGo', frequency: 'daily', timesPerDay: 2 })).toEqual(
        ['timesPerDay'],
      );
      expect(
        await field({ name: 'A', kind: 'letGo', frequency: 'daily', weeklyTarget: 2 }),
      ).toEqual(['weeklyTarget']);
      expect(
        await field({
          name: 'A',
          kind: 'letGo',
          frequency: 'daily',
          reminderTimes: ['08:00', '20:00'],
        }),
      ).toEqual(['reminderTimes']);
      expect(
        (await rejection(api.updateHabit(1, { timesPerDay: 3 }))).problem?.errors?.['timesPerDay'],
      ).toBeTruthy();
      expect(
        (await rejection(api.updateHabit(2, { kind: 'letGo' } as never))).problem?.errors?.['kind'],
      ).toBeTruthy();
      expect(
        (await rejection(api.createRoutine({ name: 'Night', habitIds: [1] }))).problem?.errors?.[
          'habitIds'
        ],
      ).toBeTruthy();
    });

    it('exports kind let_go and its slips, and drops the slips with the habit', async () => {
      const data = seed([letGo(1), localHabit(2)]);
      data.slips = [{ habitId: 1, date: '2026-10-25', count: 2, reasons: ['lonely'] }];
      await store.replace(data);

      const doc = JSON.parse(await (await api.exportData('json')).blob.text());
      expect(doc.habits.map((h: { kind: string }) => h.kind)).toEqual(['let_go', 'build']);
      expect(doc.slips).toEqual([
        { habit_id: 1, date: '2026-10-25', count: 2, reasons: ['lonely'] },
      ]);

      await api.deleteHabit(1);
      expect(JSON.parse(await (await api.exportData('json')).blob.text()).slips).toEqual([]);
    });
  });

  describe('pausing', () => {
    it('pauses from today until a day, drops a pause that began today on resume and keeps the past part of an older one', async () => {
      await store.replace(seed([localHabit(1), localHabit(2, { pausedFrom: '2026-10-30', pausedUntil: '2026-11-05' })]));

      expect(await api.pauseHabit(1, '2026-11-05')).toMatchObject({
        pausedFrom: today,
        pausedUntil: '2026-11-05',
        pausedToday: true,
      });
      expect(await api.pauseHabit(1, null)).toMatchObject({ pausedFrom: null, pausedUntil: null, pausedToday: false });
      expect(await api.pauseHabit(2, null)).toMatchObject({
        pausedFrom: '2026-10-30',
        pausedUntil: '2026-10-31',
        pausedToday: false,
      });
    });

    it('refuses a day before today, a bad day and an unknown habit', async () => {
      await store.replace(seed([localHabit(1)]));

      const past = await rejection(api.pauseHabit(1, '2026-10-31'));
      expect(past.type).toBe('validation');
      expect(past.problem?.errors).toEqual({ until: ["Can't be before today."] });
      expect(
        (await rejection(api.pauseHabit(1, 'tomorrow' as never))).problem?.errors,
      ).toHaveProperty('until');
      expect((await rejection(api.pauseHabit(42, '2026-11-05'))).type).toBe('not_found');
    });
  });

  it('lists all habits, let-go ones too, in creation order as GET /habits/:id shows them', async () => {
    await store.replace(
      seed([localHabit(1), localHabit(2, { kind: 'letGo' } as Partial<LocalHabit>)]),
    );
    await api.setDay(1, today, true);

    const list = await api.listHabits();

    expect(list.map((h) => h.id)).toEqual([1, 2]);
    expect(list).toEqual([await api.getHabit(1), await api.getHabit(2)]);
    expect(list[0]?.today.state).toBe('done');
  });

  it('accepts the device consent and the DM reminders resume as no-ops, and refuses another policy version', async () => {
    const data = seed();
    await store.replace(data);

    await api.giveConsent('device', true);
    await api.resumeDmReminders();
    const other = await rejection(api.giveConsent('2', true));

    expect(other.problem?.errors).toEqual({ policyVersion: ['Not the current policy version.'] });
    expect((await api.getMe()).consent).toMatchObject({ given: true, withdrawnAt: null });
  });

  /**
   * Parity finding: the API contract answers 400 on `ageConfirmed` unless it is true; the local backend accepts
   * any value.
   */
  it('accepts consent without the age confirmation', async () => {
    await store.replace(seed());

    await expect(api.giveConsent('device', false)).resolves.toBeUndefined();
  });

  describe('routines', () => {
    it('renames a routine, sets and clears its reminder and replaces its members', async () => {
      await store.replace(seed([localHabit(1), localHabit(2)]));
      const routine = await api.createRoutine({ name: 'Morning', habitIds: [1] });

      const changed = await api.updateRoutine(routine.id, {
        name: '  Evening  ',
        reminderTime: '21:30',
        habitIds: [2],
      });
      expect(changed).toMatchObject({ name: 'Evening', reminderTime: '21:30', habitIds: [2] });
      expect((await api.getHabit(1)).routineId).toBeNull();

      const cleared = await api.updateRoutine(routine.id, { clearReminder: true });
      expect(cleared).toMatchObject({ name: 'Evening', reminderTime: null, habitIds: [2] });
    });

    it('refuses an empty name, a clear mixed with a time, a bad time and an unknown routine', async () => {
      await store.replace(seed());
      const routine = await api.createRoutine({ name: 'Morning' });

      expect(
        (await rejection(api.updateRoutine(routine.id, { name: '  ' }))).problem?.errors,
      ).toHaveProperty('name');
      expect(
        (
          await rejection(
            api.updateRoutine(routine.id, { reminderTime: '08:00', clearReminder: true }),
          )
        ).problem?.errors,
      ).toEqual({ reminderTime: ["Can't be combined with clearReminder."] });
      expect(
        (await rejection(api.updateRoutine(routine.id, { reminderTime: '8am' }))).problem?.errors,
      ).toHaveProperty('reminderTime');
      expect((await rejection(api.updateRoutine(99, { name: 'Ghost' }))).type).toBe('not_found');
    });

    /**
     * Parity finding: the API contract ignores `reminderChannels` in request bodies without validating them; the
     * local backend answers 400 for an unknown channel.
     */
    it('refuses an unknown reminder channel', async () => {
      await store.replace(seed());
      const routine = await api.createRoutine({ name: 'Morning' });

      const error = await rejection(
        api.updateRoutine(routine.id, { reminderChannels: ['carrier-pigeon'] as never }),
      );

      expect(error.problem?.errors).toHaveProperty('reminderChannels');
    });

    it('reorders routines, and refuses a repeated id, a missing routine or a non-list', async () => {
      await store.replace(seed());
      const a = await api.createRoutine({ name: 'A' });
      const b = await api.createRoutine({ name: 'B' });

      await api.setRoutineOrder([b.id, a.id]);
      expect((await api.listRoutines()).map((r) => r.id)).toEqual([b.id, a.id]);

      expect((await rejection(api.setRoutineOrder([a.id, a.id]))).problem?.errors).toHaveProperty(
        'ids',
      );
      expect((await rejection(api.setRoutineOrder([a.id]))).type).toBe('conflict');
      expect((await rejection(api.setRoutineOrder([a.id, 99]))).type).toBe('conflict');
      expect((await api.listRoutines()).map((r) => r.id)).toEqual([b.id, a.id]);
      expect((await local(store).handle('PUT', '/api/routines/order', {})).status).toBe(400);
    });
  });

  describe('low days', () => {
    it('lists the marked days in the range oldest first, ends at today by default and forgets a cleared day', async () => {
      await store.replace(seed([localHabit(1)]));
      for (const day of ['2026-10-30', '2026-10-25', today]) await api.markLowDay(day);

      expect(await api.listLowDays()).toEqual(['2026-10-25', '2026-10-30', today]);
      expect(await api.listLowDays('2026-10-26', '2026-10-30')).toEqual(['2026-10-30']);

      await api.clearLowDay('2026-10-30');
      await api.clearLowDay('2026-10-29');
      expect(await api.listLowDays()).toEqual(['2026-10-25', today]);
    });

    it('defaults to the 366 days ending at the to date', async () => {
      const data = seed([localHabit(1)]);
      data.lowDays = ['2025-10-31', '2025-11-01'];
      await store.replace(data);

      expect(await api.listLowDays(undefined, today)).toEqual(['2025-11-01']);
    });

    it('refuses a future or too early day to mark, a from after to and a bad range', async () => {
      await store.replace(seed([localHabit(1)]));

      expect((await rejection(api.markLowDay('2026-11-02'))).type).toBe('in_future');
      expect((await rejection(api.markLowDay('2026-10-19'))).type).toBe('before_habit_start');
      expect(
        (await rejection(api.listLowDays('2026-11-01', '2026-10-01'))).problem?.errors,
      ).toEqual({
        from: ["Can't be after to."],
      });
      const raw = local(store);
      expect((await raw.handle('GET', '/api/low-days?from=nope', undefined)).status).toBe(400);
    });
  });

  describe('weekly stats', () => {
    it('sums the seven days ending yesterday per habit and leaves let-go habits out', async () => {
      await store.replace(
        seed([localHabit(1), localHabit(2, { frequency: 'weekly', weeklyTarget: 2 })]),
      );
      for (const day of ['2026-10-30', '2026-10-31', today]) await api.setDay(1, day, true);
      await api.setDay(2, '2026-10-29', true);

      const stats = await api.weeklyStats();

      expect(stats.habits).toEqual([
        { habitId: 1, name: 'Habit 1', done: 2, possible: 7, streak: 3 },
        { habitId: 2, name: 'Habit 2', done: 1, possible: 2, streak: null },
      ]);
      expect([stats.totalDone, stats.totalPossible]).toEqual([3, 9]);
      expect(stats.headline).not.toBe('');
      expect(stats.footer).not.toBe('');
    });

    /**
     * Parity finding: the API contract answers empty `headline` and `footer` when nothing was possible that week;
     * the local backend answers the quiet-week lines.
     */
    it('answers the quiet-week lines when no habit was possible', async () => {
      await store.replace(seed());

      const stats = await api.weeklyStats();

      expect(stats).toMatchObject({ totalDone: 0, totalPossible: 0, habits: [] });
      expect(stats.headline).not.toBe('');
    });
  });

  it('lists the habit templates with the contract fields, a weekly target only on weekly ones', async () => {
    const templates = await api.listTemplates();

    expect(templates.length).toBeGreaterThan(0);
    for (const template of templates) {
      expect(template).toMatchObject({
        key: expect.any(String),
        name: expect.any(String),
        description: expect.any(String),
        suggestedReminderTime: expect.stringMatching(/^\d\d:\d\d$/),
      });
      expect(template.weeklyTarget === null).toBe(template.frequency === 'daily');
    }
  });

  it('lists moods oldest first with archived ones marked, and a deleted note leaves the journal', async () => {
    await store.replace(seed([localHabit(1)]));
    const first = await api.createMood({ label: 'Calm' });
    const second = await api.createMood({ label: 'Tired' });
    await api.updateMood(first.id, { archived: true });
    await api.saveDayNote(today, { text: 'Hello' });

    expect((await api.listMoods()).map((m) => [m.id, m.label, m.archived])).toEqual([
      [first.id, 'Calm', true],
      [second.id, 'Tired', false],
    ]);

    expect((await api.saveDayNote(today, null)).note).toBeNull();
    expect(await api.listDayNotes()).toEqual([]);
    expect((await api.saveDayNote(today, null)).note).toBeNull();
  });

  describe('requests the API would answer differently', () => {
    const problem = async (response: Response) => ({
      status: response.status,
      type: response.headers.get('Content-Type'),
      body: await response.json(),
    });

    it('answers an unknown route or a wrong method with 404 not_found as a problem document', async () => {
      await store.replace(seed());
      const raw = local(store);

      for (const [method, url] of [
        ['GET', '/api/nothing-here'],
        ['DELETE', '/api/me'],
        ['GET', '/api/habits/abc'],
      ] as const) {
        expect(await problem(await raw.handle(method, url, undefined))).toEqual({
          status: 404,
          type: 'application/problem+json',
          body: { type: 'not_found', status: 404 },
        });
      }
    });

    it('answers a bad or undecodable date in the path with 400 validation on date', async () => {
      await store.replace(seed([localHabit(1)]));
      const raw = local(store);

      for (const [method, url] of [
        ['PUT', '/api/low-days/2026-13-45'],
        ['DELETE', '/api/day-notes/not-a-date'],
        ['PUT', '/api/habits/1/days/%E0%A4%A'],
      ] as const) {
        const answer = await problem(
          await raw.handle(method, url, method === 'PUT' ? {} : undefined),
        );
        expect(answer.status).toBe(400);
        expect(answer.body).toMatchObject({
          type: 'validation',
          errors: { date: ['Must be a date as yyyy-MM-dd.'] },
        });
      }
    });

    it('answers 401 unauthenticated while the device has no data', async () => {
      const answer = await problem(await local(store).handle('GET', '/api/habits', undefined));

      expect(answer).toMatchObject({ status: 401, body: { type: 'unauthenticated' } });
    });
  });

  it.each([
    [false, []],
    [true, ['mobile']],
  ])(
    'gives new habits and routines the reminder channels of the build (native %s)',
    async (native, channels) => {
      await store.replace(seed());
      connect(native);

      expect(
        (await api.createHabit({ name: 'Read', frequency: 'daily' })).reminderChannels,
      ).toEqual(channels);
      expect((await api.createRoutine({ name: 'Night' })).reminderChannels).toEqual(channels);
    },
  );
});
