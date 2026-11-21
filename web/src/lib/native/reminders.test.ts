import 'fake-indexeddb/auto';
import type { LocalNotificationsPlugin, ScheduleOptions } from '@capacitor/local-notifications';
import { IDBFactory } from 'fake-indexeddb';
import { afterEach, beforeEach, describe, expect, it, type Mock, vi } from 'vitest';
import { ApiClient } from '../api-client';
import { DeviceStore } from '../device-store';
import { env } from '../env';
import { Http } from '../http';
import type { AnyHabit, Habit, ReminderDestination, Routine } from '../models';
import { OfflineQueue } from '../offline-queue';
import { type FetchMock, habit, letGoHabit, mockFetch } from '@/test/test-support';
import { applyTheme } from '../theme';
import { reminderNames } from './reminder-names';
import { planDays } from './reminder-plan';
import { doneActionType, Reminders, reminderBody, secondReminderBody } from './reminders';
import type { EmberWidgetPlugin } from './widget';

const zone = 'Europe/Berlin';
const reminded: Habit = { ...habit, reminderTime: '20:00', reminderChannels: ['mobile'] };

describe('Reminders', () => {
  let mock: FetchMock;
  let device: DeviceStore;
  let reminders: Reminders;
  let schedule: Mock<(options: ScheduleOptions) => Promise<unknown>>;
  let getPending: Mock;
  let delivered: Array<{ id: number; extra: unknown }>;
  let removeDelivered: Mock<(delivered: { notifications: Array<{ id: number }> }) => Promise<void>>;
  let cancel: Mock;
  let checkPermissions: Mock;
  let requestPermissions: Mock;
  let checkExact: Mock;
  let changeExact: Mock;
  let exactAlarms: boolean;
  let widget: Partial<EmberWidgetPlugin>;

  beforeEach(async () => {
    (globalThis as unknown as { indexedDB: unknown }).indexedDB = new IDBFactory();
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-11-01T08:00:00Z'));
    mock = mockFetch();
    device = new DeviceStore();
    schedule = vi.fn<(options: ScheduleOptions) => Promise<unknown>>(async () => ({}));
    getPending = vi.fn(async () => ({ notifications: [] }));
    delivered = [];
    removeDelivered = vi.fn<(delivered: { notifications: Array<{ id: number }> }) => Promise<void>>(async () => undefined);
    cancel = vi.fn(async () => undefined);
    checkPermissions = vi.fn(async () => ({ display: 'granted' }));
    requestPermissions = vi.fn(async () => ({ display: 'granted' }));
    checkExact = vi.fn(async () => ({ exact_alarm: 'denied' }));
    changeExact = vi.fn(async () => ({ exact_alarm: 'granted' }));
    exactAlarms = false;
    const plugin = {
      checkExactNotificationSetting: checkExact,
      changeExactNotificationSetting: changeExact,
      createChannel: async () => undefined,
      getPending,
      getDeliveredNotifications: async () => ({
        notifications: delivered.map(({ id }) => ({ id, title: 'Ember', body: reminderBody(), data: {} })),
      }),
      getByIds: async ({ ids }: { ids: number[] }) => ({
        notifications: delivered.filter(({ id }) => ids.includes(id)),
      }),
      removeDeliveredNotifications: removeDelivered,
      cancel,
      checkPermissions,
      requestPermissions,
      registerActionTypes: async () => undefined,
      schedule,
    } as unknown as LocalNotificationsPlugin;
    widget = {
      update: async () => undefined,
      clear: async () => undefined,
    };
    const api = new ApiClient(new Http({ native: true, apiBase: '' }), new OfflineQueue(device));
    reminders = new Reminders(
      api,
      device,
      async () => ({ LocalNotifications: plugin }),
      async () => ({ EmberWidget: widget as EmberWidgetPlugin }),
      () => exactAlarms,
    );
    await device.saveHabits({ userId: 'u1', timeZone: zone, habits: [reminded] });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    reminderNames.set(false);
    localStorage.clear();
    env.apiBase = '';
  });

  const phone: ReminderDestination = {
    id: 'phone-5',
    kind: 'phone',
    label: 'Pixel 8',
    enabled: true,
    current: true,
  };

  async function phoneRegistered(answer: ReminderDestination | null = phone) {
    const call = await mock.next('POST', '/api/me/phones');
    if (answer) call.respond(200, answer);
    else call.fail();
    return call;
  }

  async function rebuildOnline(habits: AnyHabit[], routines: unknown[] = []) {
    const done = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, habits);
    (await mock.next('GET', '/api/routines')).respond(200, routines);
    await phoneRegistered();
    await done;
    return schedule.mock.calls[0][0].notifications;
  }

  const scheduledAt = () =>
    schedule.mock.calls[0][0].notifications.map((n) => n.schedule?.at?.toISOString());

  it("offline, plans from the cached habits with the queued check-in applied, so today's reminder is cancelled", async () => {
    localStorage.setItem('ember.reminderPhone', JSON.stringify({ id: 'phone-5', enabled: true }));
    await device.addEdit('u1', { habitId: 1, date: '2026-11-01', completed: true });

    const done = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).fail();
    await done;

    const at = scheduledAt();
    expect(at).toHaveLength(planDays - 1);
    expect(at[0]).toBe('2026-11-02T19:00:00.000Z');
  });

  it('asks for the notification permission when the phone never asked, then schedules once granted', async () => {
    checkPermissions.mockResolvedValue({ display: 'prompt' });
    requestPermissions.mockImplementation(async () => {
      checkPermissions.mockResolvedValue({ display: 'granted' });
      return { display: 'granted' };
    });

    const notifications = await rebuildOnline([reminded]);

    expect(requestPermissions).toHaveBeenCalledTimes(1);
    expect(notifications).toHaveLength(planDays);
  });

  it('schedules inexact alarms while exact ones are not allowed, so a rebuild never opens the setting', async () => {
    const notifications = await rebuildOnline([reminded]);

    expect(notifications.every((n) => n.isExactNotification === false)).toBe(true);
    expect(changeExact).not.toHaveBeenCalled();
  });

  it('schedules exact alarms once Android allows them, so reminders arrive on time', async () => {
    checkExact.mockResolvedValue({ exact_alarm: 'granted' });

    const notifications = await rebuildOnline([reminded]);

    expect(notifications.every((n) => n.isExactNotification === true)).toBe(true);
  });

  it('in the beta app, opens the Alarms & reminders setting once after notifications are allowed', async () => {
    exactAlarms = true;

    expect(await reminders.requestPermission()).toBe(true);
    expect(await reminders.requestPermission()).toBe(true);

    expect(changeExact).toHaveBeenCalledTimes(1);
  });

  it('never opens the Alarms & reminders setting outside the beta app or when notifications are denied', async () => {
    await reminders.requestPermission();
    exactAlarms = true;
    checkPermissions.mockResolvedValue({ display: 'denied' });
    requestPermissions.mockResolvedValue({ display: 'denied' });
    await reminders.requestPermission();

    expect(changeExact).not.toHaveBeenCalled();
  });

  it('leaves pending reminders alone while the plan is unchanged, and replaces them when it changes', async () => {
    await rebuildOnline([reminded]);
    await rebuildOnline([reminded]);

    expect(schedule).toHaveBeenCalledTimes(1);
    expect(getPending).toHaveBeenCalledTimes(1);

    await rebuildOnline([{ ...reminded, reminderTime: '21:00' }]);

    expect(schedule).toHaveBeenCalledTimes(2);
  });

  it('schedules one notification with Done per reminder time, keeps their ids across edits, and cancels them with the habit', async () => {
    const water: Habit = {
      ...reminded,
      id: 2,
      name: 'Water',
      timesPerDay: 3,
      reminderTime: '08:00',
      reminderTimes: ['08:00', '13:00', '20:00'],
    };
    const first = await rebuildOnline([water]);

    expect(first).toHaveLength(2 + 3 * (planDays - 1));
    expect(first.slice(0, 3).map((n) => n.schedule?.at?.toISOString())).toEqual([
      '2026-11-01T12:00:00.000Z',
      '2026-11-01T19:00:00.000Z',
      '2026-11-02T07:00:00.000Z',
    ]);
    expect(first.every((n) => n.actionTypeId === doneActionType && n.title === 'Ember')).toBe(true);

    getPending.mockResolvedValue({ notifications: first.map(({ id }) => ({ id })) });
    const edited = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, [reminded, water]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await phoneRegistered();
    await edited;
    const second = schedule.mock.calls[1][0].notifications;
    const waterIds = (list: typeof first) =>
      list.filter((n) => (n.extra as { habitId: number }).habitId === 2).map((n) => n.id);

    expect(waterIds(second)).toEqual(waterIds(first));
    expect(cancel).toHaveBeenLastCalledWith({ notifications: first.map(({ id }) => ({ id })) });

    getPending.mockResolvedValue({ notifications: second.map(({ id }) => ({ id })) });
    const deleted = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, []);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await phoneRegistered();
    await deleted;

    expect(schedule).toHaveBeenCalledTimes(2);
    expect(cancel).toHaveBeenLastCalledWith({ notifications: second.map(({ id }) => ({ id })) });
  });

  it('schedules the same plan again after the reminders were cleared', async () => {
    await rebuildOnline([reminded]);
    await reminders.clearAll();

    await rebuildOnline([reminded]);

    expect(schedule).toHaveBeenCalledTimes(2);
  });

  it('schedules nothing from the cache when the API answers 401', async () => {
    const done = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(401);
    await done;

    expect(schedule).not.toHaveBeenCalled();
    expect(getPending).not.toHaveBeenCalled();
  });

  it('plans from the live habit list and caches it', async () => {
    const done = reminders.rebuild('u1', 'Asia/Tokyo');
    (await mock.next('GET', '/api/habits')).respond(200, [{ ...reminded, name: 'Read' }]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await phoneRegistered();
    await done;

    expect(scheduledAt()[0]).toBe('2026-11-01T11:00:00.000Z');
    expect(await device.habits('u1')).toMatchObject({
      timeZone: 'Asia/Tokyo',
      habits: [{ name: 'Read' }],
    });
  });

  it("tints the reminder icon with the current theme's colour", async () => {
    applyTheme('tidepool');
    const notifications = await rebuildOnline([reminded]);

    expect(new Set(notifications.map((n) => n.iconColor))).toEqual(new Set(['#7DD5D8']));
  });

  it('names no habit but still offers Done for that habit and day while "Show habit names" is off', async () => {
    const [first] = await rebuildOnline([reminded]);

    expect(first).toMatchObject({ title: 'Ember', body: reminderBody(), actionTypeId: doneActionType });
    expect(first.extra).toEqual({ userId: 'u1', habitId: 1, date: '2026-11-01' });
    expect(JSON.stringify(first)).not.toContain('Walk');
    expect(reminders.wasIssued(first.id, first.extra)).toBe(true);
  });

  describe('delivered reminders', () => {
    const deliver = (...entries: Array<[number, Record<string, unknown>]>) => {
      delivered = entries.map(([id, extra]) => ({ id, extra }));
    };
    const removed = () =>
      removeDelivered.mock.calls.flatMap(([{ notifications }]) => notifications.map(({ id }) => id));
    const doneToday = { date: '2026-11-01', state: 'done', completed: true, count: 1, skipped: false } as const;

    it("removes a done habit's and a finished routine's reminder for that day, even when the plan is unchanged", async () => {
      const morning: Habit = { ...reminded, reminderTime: '08:30' };
      const reading: Habit = {
        ...reminded,
        id: 2,
        name: 'Read',
        timesPerDay: 3,
        today: { date: '2026-11-01', state: 'partial', completed: null, count: 1, skipped: false },
      };
      const stretch: Habit = { ...reminded, id: 3, name: 'Stretch', today: doneToday };
      const routine: Routine = { id: 4, name: 'Start', reminderTime: '08:00', reminderChannels: ['mobile'], habitIds: [3] };
      const evening: Routine = { ...routine, id: 5, name: 'Evening', habitIds: [2, 3] };
      await rebuildOnline([morning, reading, stretch], [routine, evening]);
      deliver(
        [11, { userId: 'u1', habitId: 1, date: '2026-11-01' }],
        [12, { userId: 'u1', habitId: 1, date: '2026-10-31' }],
        [13, { userId: 'u1', habitId: 2, date: '2026-11-01' }],
        [14, { userId: 'u1', routineId: 4, date: '2026-11-01' }],
        [15, { userId: 'u2', habitId: 1, date: '2026-11-01' }],
        [16, { userId: 'u1', routineId: 5, date: '2026-11-01' }],
      );

      await rebuildOnline([{ ...morning, today: doneToday }, reading, stretch], [routine, evening]);

      expect(schedule).toHaveBeenCalledTimes(1);
      expect(removed()).toEqual([11, 14]);
    });

    it('still schedules when reading the delivered reminders fails', async () => {
      delivered = [{ id: 11, extra: { userId: 'u1', habitId: 1, date: '2026-11-01' } }];
      removeDelivered.mockRejectedValue(new Error('shade unavailable'));

      const notifications = await rebuildOnline([{ ...reminded, today: doneToday }]);

      expect(removeDelivered).toHaveBeenCalledTimes(1);
      expect(notifications).toHaveLength(planDays - 1);
    });

    it("removes a weekly habit's reminders from earlier that week once its target is met", async () => {
      deliver(
        [21, { userId: 'u1', habitId: 1, date: '2026-10-30' }],
        [22, { userId: 'u1', habitId: 1, date: '2026-10-25' }],
      );

      await rebuildOnline([
        { ...reminded, frequency: 'weekly', weeklyTarget: 3, stats: { ...reminded.stats, week: { done: 3, target: 3 } } },
      ]);

      expect(removed()).toEqual([21]);
    });
  });

  it('names the habit with a Done action, and the routine without one, while "Show habit names" is on', async () => {
    reminderNames.set(true);
    const routine = {
      id: 4,
      name: 'Evening',
      reminderTime: '19:00',
      reminderChannels: ['mobile'],
      habitIds: [2],
    };
    const notifications = await rebuildOnline([reminded, { ...reminded, id: 2, name: 'Read' }], [routine]);

    expect(notifications[0]).toMatchObject({
      title: 'Evening',
      body: reminderBody(),
      extra: { userId: 'u1', routineId: 4, date: '2026-11-01' },
    });
    expect(notifications[0].actionTypeId).toBeUndefined();
    expect(notifications[1]).toMatchObject({
      title: 'Walk',
      actionTypeId: doneActionType,
      extra: { userId: 'u1', habitId: 1, date: '2026-11-01' },
    });
    expect(JSON.stringify(notifications)).not.toContain('Read');
  });

  it('follows each reminder with a second one under its own id, with the same title, Done and day', async () => {
    reminderNames.set(true);
    const done = reminders.rebuild('u1', zone, 30);
    (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await phoneRegistered();
    await done;
    const [first, second] = schedule.mock.calls[0][0].notifications;

    expect(first).toMatchObject({ title: 'Walk', body: reminderBody(), actionTypeId: doneActionType });
    expect(second).toMatchObject({
      title: 'Walk',
      body: secondReminderBody(),
      actionTypeId: doneActionType,
      extra: first.extra,
    });
    expect(second.id).not.toBe(first.id);
    expect(second.schedule?.at?.toISOString()).toBe('2026-11-01T19:30:00.000Z');
    expect(reminders.wasIssued(second.id, second.extra)).toBe(true);
  });

  it('gives a let-go habit one quiet check-in a day with no Done action and no second reminder', async () => {
    reminderNames.set(true);
    const coffee = { ...letGoHabit, reminderTime: '21:00', reminderChannels: ['mobile'] };
    const done = reminders.rebuild('u1', zone, 30);
    (await mock.next('GET', '/api/habits')).respond(200, [coffee]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await phoneRegistered();
    await done;
    const notifications = schedule.mock.calls[0][0].notifications;

    expect(notifications).toHaveLength(planDays);
    expect(notifications[0]).toMatchObject({
      title: 'Coffee',
      body: 'A quiet check-in for Coffee. If anything happened today, you can note it in Ember.',
      extra: { userId: 'u1', habitId: 9, date: '2026-11-01', letGo: true },
    });
    expect(notifications.every((n) => n.actionTypeId === undefined)).toBe(true);
    expect(new Set(notifications.map((n) => (n.extra as { date: string }).date)).size).toBe(planDays);
    expect(reminders.wasIssued(notifications[0].id, notifications[0].extra)).toBe(false);
  });

  it('keeps the habit name out of a let-go check-in while "Show habit names" is off', async () => {
    const [first] = await rebuildOnline([{ ...letGoHabit, reminderTime: '21:00', reminderChannels: ['mobile'] }]);

    expect(first.title).toBe('Ember');
    expect(first.body).toBe('A quiet check-in. If anything happened today, you can note it in Ember.');
    expect(JSON.stringify(first)).not.toContain('Coffee');
  });

  it('schedules nothing for a let-go habit without a check-in time', async () => {
    const done = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, [letGoHabit]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await phoneRegistered();
    await done;

    expect(schedule).not.toHaveBeenCalled();
  });

  it('remembers which "Done" notifications it issued, for that exact extra only', async () => {
    reminderNames.set(true);
    const [first] = await rebuildOnline([reminded]);
    const extra = { userId: 'u1', habitId: 1, date: '2026-11-01' };

    expect(reminders.wasIssued(first.id, extra)).toBe(true);
    expect(reminders.wasIssued(first.id, { ...extra, habitId: 2 })).toBe(false);
    expect(reminders.wasIssued(first.id + 100, extra)).toBe(false);

    reminderNames.set(false);
    await reminders.clearAll();
    expect(reminders.wasIssued(first.id, extra)).toBe(false);
  });

  it('keeps one record per issued "Done", dropping stale future ones, and still schedules when storage is full', async () => {
    reminderNames.set(true);
    const stale = { id: 99, userId: 'u1', habitId: 1, date: '2026-11-05' };
    localStorage.setItem('ember.reminderIssued', JSON.stringify([stale]));
    await rebuildOnline([reminded]);
    const once = localStorage.getItem('ember.reminderIssued');
    localStorage.removeItem('ember.reminderPlanned');

    const again = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await phoneRegistered();
    await again;

    expect(localStorage.getItem('ember.reminderIssued')).toBe(once);
    expect(JSON.parse(once ?? '[]')).toHaveLength(planDays);
    expect(reminders.wasIssued(99, stale)).toBe(false);

    localStorage.removeItem('ember.reminderPlanned');
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('full', 'QuotaExceededError');
    });
    const full = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await phoneRegistered();
    await full;
    vi.restoreAllMocks();

    expect(schedule).toHaveBeenCalledTimes(3);
  });

  it('keeps planning the cached routines when only the routine list fails', async () => {
    const routine: Routine = {
      id: 4,
      name: 'Evening',
      reminderTime: '19:00',
      reminderChannels: ['mobile'],
      habitIds: [1],
    };
    await device.saveHabits({ userId: 'u1', timeZone: zone, habits: [reminded], routines: [routine] });

    const done = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
    (await mock.next('GET', '/api/routines')).respond(500);
    await phoneRegistered();
    await done;

    expect(schedule.mock.calls[0][0].notifications[0].extra).toMatchObject({ routineId: 4 });
    expect((await device.habits('u1'))?.routines).toEqual([routine]);
  });

  it('offline, plans the cached routines too', async () => {
    localStorage.setItem('ember.reminderPhone', JSON.stringify({ id: 'phone-5', enabled: true }));
    await device.saveHabits({
      userId: 'u1',
      timeZone: zone,
      habits: [reminded],
      routines: [{ id: 4, name: 'Evening', reminderTime: '19:00', reminderChannels: ['mobile'], habitIds: [1] }],
    });

    const done = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).fail();
    await done;

    expect(schedule.mock.calls[0][0].notifications[0].extra).toEqual({
      userId: 'u1',
      routineId: 4,
      date: '2026-11-01',
    });
  });

  it('registers this phone by its install id and model, and follows Settings and per-item choices for it', async () => {
    vi.stubGlobal('navigator', { userAgent: 'Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/AP1A; wv) Chrome/130' });
    const done = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, [
      reminded,
      { ...reminded, id: 2, reminderExclusions: ['phone-5'] },
    ]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    const registration = (await phoneRegistered()) as { body: { installId: string; label: string } };
    await done;

    expect(registration.body).toEqual({ installId: localStorage.getItem('ember.installId'), label: 'Pixel 8' });
    expect(registration.body.installId).toMatch(/^[A-Za-z0-9_-]{16,64}$/);
    expect(scheduledAt()).toHaveLength(planDays);
    expect(schedule.mock.calls[0][0].notifications.every((n) => (n.extra as { habitId: number }).habitId === 1)).toBe(
      true,
    );

    getPending.mockResolvedValue({ notifications: [{ id: 1 }] });
    const off = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    expect((await phoneRegistered({ ...phone, enabled: false })).body).toEqual(registration.body);
    await off;

    expect(schedule).toHaveBeenCalledTimes(1);
    expect(cancel).toHaveBeenLastCalledWith({ notifications: [{ id: 1 }] });
  });

  it('plans with the last registration when registering fails, and with nothing when it never succeeded', async () => {
    const never = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await phoneRegistered(null);
    await never;

    expect(schedule).not.toHaveBeenCalled();

    await rebuildOnline([reminded]);
    localStorage.removeItem('ember.reminderPlanned');
    const failed = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await phoneRegistered(null);
    await failed;

    expect(schedule).toHaveBeenCalledTimes(2);
  });

  it('forgets the cached registration on clearAll, so an offline rebuild plans nothing and asks no server', async () => {
    await rebuildOnline([reminded]);
    await reminders.clearAll();
    await device.saveHabits({ userId: 'u1', timeZone: zone, habits: [reminded] });

    const offline = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).fail();
    await offline;

    expect(mock.all.filter((c) => c.url === '/api/me/phones')).toHaveLength(1);
    expect(schedule).toHaveBeenCalledTimes(1);
  });

  it('registers nothing while notifications are denied', async () => {
    checkPermissions.mockResolvedValue({ display: 'denied' });

    const done = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await done;

    expect(mock.all.some((c) => c.url === '/api/me/phones')).toBe(false);
    expect(requestPermissions).not.toHaveBeenCalled();
    expect(schedule).not.toHaveBeenCalled();
  });

  it('never asks for the notification permission on a rebuild when no item has a reminder', async () => {
    checkPermissions.mockResolvedValue({ display: 'prompt' });

    const done = reminders.rebuild('u1', zone);
    (await mock.next('GET', '/api/habits')).respond(200, [habit]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await done;

    expect(requestPermissions).not.toHaveBeenCalled();
    expect(mock.all.some((c) => c.url === '/api/me/phones')).toBe(false);
  });

  it('in no-account mode, registers nothing and plans the habits with the mobile channel', async () => {
    localStorage.setItem('ember.mode', 'local');

    const done = reminders.rebuild('u1', zone, 30);
    (await mock.next('GET', '/api/habits')).respond(200, [
      { ...reminded, serverReminders: true, reminderExclusions: ['phone-5'] },
      { ...reminded, id: 2, reminderChannels: [] },
    ]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await done;
    const notifications = schedule.mock.calls[0][0].notifications;

    expect(mock.all.some((c) => c.url === '/api/me/phones')).toBe(false);
    expect(notifications).toHaveLength(2 * planDays);
    expect(notifications.every((n) => (n.extra as { habitId: number }).habitId === 1)).toBe(true);
  });

  it('turns this phone on from Settings: asks for the permission, registers it and switches its destination on', async () => {
    const enabling = reminders.enablePhone();
    (await mock.next('POST', '/api/me/phones')).respond(200, { ...phone, enabled: false });
    const patch = await mock.next('PATCH', '/api/me/reminder-destinations/phone-5');
    expect(patch.body).toEqual({ enabled: true });
    patch.respond(200, phone);

    expect(await enabling).toEqual(phone);
    expect(JSON.parse(localStorage.getItem('ember.reminderPhone') ?? 'null')).toEqual({ id: 'phone-5', enabled: true });
  });

  it('turns this phone on with nothing sent when the permission is refused', async () => {
    checkPermissions.mockResolvedValue({ display: 'denied' });
    requestPermissions.mockResolvedValue({ display: 'denied' });

    expect(await reminders.enablePhone()).toBeNull();
    expect(mock.all).toHaveLength(0);
  });

  it('calls the native notifications plugin without awaiting the proxy itself', async () => {
    const native = new Reminders(
      new ApiClient(new Http({ native: true, apiBase: '' }), new OfflineQueue(device)),
      device,
    );

    expect(await native.requestPermission()).toBe(true);
  });

  describe('widget key', () => {
    let setKey: Mock;

    beforeEach(() => {
      env.apiBase = 'https://ember.example/base';
      setKey = vi.fn(async () => undefined);
      widget.hasKey = async () => ({ value: false });
      widget.setKey = setKey;
    });

    const keyRequests = () => mock.all.filter((c) => c.url === '/api/me/widget-key');

    it('requests a key on a live rebuild while the widget has none and stores it with the API origin', async () => {
      const done = reminders.rebuild('u1', zone);
      (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
      (await mock.next('GET', '/api/routines')).respond(200, []);
      (await mock.next('POST', '/api/me/widget-key')).respond(200, { key: 'synthetic-widget-key' });
      await phoneRegistered();
      await done;

      expect(setKey).toHaveBeenCalledWith({ key: 'synthetic-widget-key', apiBase: 'https://ember.example' });
      expect(schedule).toHaveBeenCalledTimes(1);
    });

    it('replaces a key left from before once per run and after a sign-out, and keeps its own', async () => {
      widget.hasKey = async () => ({ value: true });
      const rebuildWithKey = async (key?: string) => {
        const done = reminders.rebuild('u1', zone);
        (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
        (await mock.next('GET', '/api/routines')).respond(200, []);
        if (key) (await mock.next('POST', '/api/me/widget-key')).respond(200, { key });
        await phoneRegistered();
        await done;
      };

      await rebuildWithKey('synthetic-key-1');
      await rebuildWithKey();
      await reminders.clearAll();
      await rebuildWithKey('synthetic-key-2');

      expect(keyRequests()).toHaveLength(2);
      expect(setKey.mock.calls.map(([options]) => options.key)).toEqual(['synthetic-key-1', 'synthetic-key-2']);
    });

    it.each([
      ['the native build has no hasKey', () => delete widget.hasKey],
      ['the API base is not an absolute https URL', () => (env.apiBase = '')],
    ])('requests no key when %s', async (_, arrange) => {
      arrange();

      await rebuildOnline([reminded]);

      expect(keyRequests()).toEqual([]);
      expect(setKey).not.toHaveBeenCalled();
    });

    it('requests no key in no-account mode', async () => {
      localStorage.setItem('ember.mode', 'local');

      const done = reminders.rebuild('u1', zone);
      (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
      (await mock.next('GET', '/api/routines')).respond(200, []);
      await done;

      expect(keyRequests()).toEqual([]);
      expect(schedule).toHaveBeenCalledTimes(1);
    });

    it('still schedules the reminders when the key request fails', async () => {
      const done = reminders.rebuild('u1', zone);
      (await mock.next('GET', '/api/habits')).respond(200, [reminded]);
      (await mock.next('GET', '/api/routines')).respond(200, []);
      (await mock.next('POST', '/api/me/widget-key')).respond(500);
      await phoneRegistered();
      await done;

      expect(setKey).not.toHaveBeenCalled();
      expect(schedule).toHaveBeenCalledTimes(1);
    });
  });

  describe('widget pending check-ins', () => {
    const twice: Habit = { ...habit, timesPerDay: 2 };
    let queue: OfflineQueue;
    let takePending: Mock;

    beforeEach(() => {
      queue = new OfflineQueue(device);
      queue.userId.set('u1');
      takePending = vi.fn(async () => ({
        checkIns: [{ userId: 'u1', habitId: 1, date: '2026-11-01', count: 2 }],
      }));
      widget.takePending = takePending;
    });

    const puts = () => mock.all.filter((c) => c.method === 'PUT');

    it('sends the absolute count when the server has fewer check-ins', async () => {
      const done = reminders.replayWidget('u1', zone, queue);
      (await mock.next('GET', '/api/habits')).respond(200, [{ ...twice, today: { ...twice.today, count: 1 } }]);
      (await mock.next('GET', '/api/routines')).respond(200, []);
      const put = await mock.next('PUT', '/api/habits/1/days/2026-11-01');
      expect(put.body).toEqual({ count: 2 });
      put.respond(200, twice);
      await done;
    });

    it('caps the count at timesPerDay', async () => {
      takePending.mockResolvedValue({ checkIns: [{ userId: 'u1', habitId: 1, date: '2026-11-01', count: 5 }] });

      const done = reminders.replayWidget('u1', zone, queue);
      (await mock.next('GET', '/api/habits')).respond(200, [twice]);
      (await mock.next('GET', '/api/routines')).respond(200, []);
      const put = await mock.next('PUT', '/api/habits/1/days/2026-11-01');
      expect(put.body).toEqual({ count: 2 });
      put.respond(200, twice);
      await done;
    });

    it('sends nothing when the server already has the check-in', async () => {
      const done = reminders.replayWidget('u1', zone, queue);
      (await mock.next('GET', '/api/habits')).respond(200, [{ ...twice, today: { ...twice.today, count: 2 } }]);
      (await mock.next('GET', '/api/routines')).respond(200, []);
      await done;

      expect(puts()).toEqual([]);
      expect(await device.edits('u1')).toEqual([]);
    });

    it("drops another user's and malformed entries without asking the server", async () => {
      takePending.mockResolvedValue({
        checkIns: [
          { userId: 'u2', habitId: 1, date: '2026-11-01', count: 1 },
          { userId: 'u1', habitId: 1, date: '2026-11-01', count: 0 },
          { userId: 'u1', habitId: 1, date: '2026-11-01', count: 1.5 },
        ],
      });

      await reminders.replayWidget('u1', zone, queue);

      expect(mock.all).toEqual([]);
      expect(await device.edits('u1')).toEqual([]);
    });

    it('queues the absolute count when the API is unreachable', async () => {
      await device.saveHabits({ userId: 'u1', timeZone: zone, habits: [twice] });

      const done = reminders.replayWidget('u1', zone, queue);
      (await mock.next('GET', '/api/habits')).fail();
      (await mock.next('PUT', '/api/habits/1/days/2026-11-01')).fail();
      await done;

      expect(await device.edits('u1')).toMatchObject([{ habitId: 1, date: '2026-11-01', count: 2 }]);
    });

    it.each([
      [503, [{ habitId: 1, date: '2026-11-01', count: 2 }]],
      [404, []],
    ])('after the PUT answers %i keeps the entry in the queue unless the queue would drop it', async (status, kept) => {
      const done = reminders.replayWidget('u1', zone, queue);
      (await mock.next('GET', '/api/habits')).respond(200, [twice]);
      (await mock.next('GET', '/api/routines')).respond(200, []);
      (await mock.next('PUT', '/api/habits/1/days/2026-11-01')).respond(status);
      await done;

      expect(await device.edits('u1')).toMatchObject(kept);
    });

    it("queues an earlier day's entry when reading that day fails", async () => {
      takePending.mockResolvedValue({ checkIns: [{ userId: 'u1', habitId: 1, date: '2026-10-31', count: 1 }] });

      const done = reminders.replayWidget('u1', zone, queue);
      (await mock.next('GET', '/api/habits')).respond(200, [twice]);
      (await mock.next('GET', '/api/routines')).respond(200, []);
      (await mock.next('GET', '/api/habits/1/days?from=2026-10-31&to=2026-10-31')).respond(503);
      await done;

      expect(puts()).toEqual([]);
      expect(await device.edits('u1')).toMatchObject([{ habitId: 1, date: '2026-10-31', count: 1 }]);
    });

    it('queues the entry as is when the API is unreachable and nothing is cached', async () => {
      queue.userId.set('u3');
      takePending.mockResolvedValue({ checkIns: [{ userId: 'u3', habitId: 1, date: '2026-11-01', count: 2 }] });

      const done = reminders.replayWidget('u3', zone, queue);
      (await mock.next('GET', '/api/habits')).fail();
      await done;

      expect(await device.edits('u3')).toMatchObject([{ habitId: 1, date: '2026-11-01', count: 2 }]);
    });

    it('drops the entries on a 401', async () => {
      queue.userId.set('u3');
      takePending.mockResolvedValue({ checkIns: [{ userId: 'u3', habitId: 1, date: '2026-11-01', count: 2 }] });

      const done = reminders.replayWidget('u3', zone, queue);
      (await mock.next('GET', '/api/habits')).respond(401);
      await done;

      expect(puts()).toEqual([]);
      expect(await device.edits('u3')).toEqual([]);
    });
  });
});

vi.mock('@capacitor/local-notifications', async () => ({
  LocalNotifications: (await import('@/test/test-support')).capacitorPlugin({
    checkPermissions: async () => ({ display: 'granted' }),
  }),
}));
