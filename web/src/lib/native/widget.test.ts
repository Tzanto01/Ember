import 'fake-indexeddb/auto';
import type { LocalNotificationsPlugin } from '@capacitor/local-notifications';
import { IDBFactory } from 'fake-indexeddb';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiClient } from '../api-client';
import { DeviceStore } from '../device-store';
import { Http } from '../http';
import type { Habit } from '../models';
import { OfflineQueue } from '../offline-queue';
import { type FetchMock, habit, letGoHabit, mockFetch } from '../test-support';
import { reminderNames } from './reminder-names';
import { Reminders } from './reminders';
import { followWidgetStyle } from '../startup';
import { applyCalm, applyGentleMotion, applyTheme, chooseColorScheme, chooseTheme, resetTheme } from '../theme';
import { type EmberWidgetPlugin, type WidgetSnapshot, widgetSnapshot } from './widget';

describe('widget snapshot', () => {
  let mock: FetchMock;
  let reminders: Reminders;
  const update = vi.fn(async () => undefined);
  const clear = vi.fn(async () => undefined);

  beforeEach(() => {
    (globalThis as unknown as { indexedDB: unknown }).indexedDB = new IDBFactory();
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-11-01T08:00:00Z'));
    update.mockClear();
    clear.mockClear();
    mock = mockFetch();
    const device = new DeviceStore();
    const plugin = {
      createChannel: async () => undefined,
      registerActionTypes: async () => undefined,
      getPending: async () => ({ notifications: [] }),
      cancel: async () => undefined,
      removeAllDeliveredNotifications: async () => undefined,
      checkPermissions: async () => ({ display: 'granted' }),
      schedule: async () => ({}),
    } as unknown as LocalNotificationsPlugin;
    const widget = { update, clear } as unknown as EmberWidgetPlugin;
    reminders = new Reminders(
      new ApiClient(new Http({ native: true, apiBase: '' }), new OfflineQueue(device)),
      device,
      async () => ({ LocalNotifications: plugin }),
      async () => ({ EmberWidget: widget }),
    );
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  const phoneRegistered = async () =>
    (await mock.next('POST', '/api/me/phones')).respond(200, {
      id: 'phone-5',
      kind: 'phone',
      label: 'Pixel 8',
      enabled: true,
      current: true,
    });

  const day = (state: Habit['today']['state'], count = 0) => ({
    date: '2026-11-01',
    state,
    completed: null,
    count,
  });

  it("pushes today's habits by the Today rules after a rebuild, with done of total, leaving out rest days and pauses, and tomorrow's from the schedule", async () => {
    const done = reminders.rebuild('u1', 'Europe/Berlin');
    (await mock.next('GET', '/api/habits')).respond(200, [
      { ...habit, id: 1, name: 'Walk', timesPerDay: 3, today: day('partial', 2) },
      { ...habit, id: 2, name: 'Stretch', frequency: 'weekdays', days: ['mon'], today: day('offday') },
      { ...habit, id: 3, name: 'Read', pausedToday: true, pausedUntil: '2026-11-01', today: day('paused') },
      { ...habit, id: 4, name: 'Water', today: day('done', 1) },
      { ...habit, id: 5, name: 'Journal', today: day('pending') },
    ]);
    (await mock.next('GET', '/api/routines')).respond(200, []);
    await phoneRegistered();
    await done;

    const open = (id: number, name: string, timesPerDay = 1) => ({ id, name, state: 'open', count: 0, timesPerDay });
    expect(update).toHaveBeenCalledWith({
      userId: 'u1',
      date: '2026-11-01',
      timeZone: 'Europe/Berlin',
      habits: [
        open(5, 'Journal'),
        { id: 1, name: 'Walk', state: 'partial', count: 2, timesPerDay: 3 },
        { id: 4, name: 'Water', state: 'done', count: 1, timesPerDay: 1 },
      ],
      more: 0,
      done: 1,
      total: 3,
      next: {
        date: '2026-11-02',
        habits: [open(5, 'Journal'), open(3, 'Read'), open(2, 'Stretch'), open(1, 'Walk', 3), open(4, 'Water')],
        more: 0,
        done: 0,
        total: 5,
      },
    });
  });

  it("follows the account's Today order with routines as one block in member order, keeping done habits in place", async () => {
    const done = reminders.rebuild('u1', 'Europe/Berlin', 0, ['h:4', 'r:7', 'h:1']);
    (await mock.next('GET', '/api/habits')).respond(200, [
      { ...habit, id: 1, name: 'Walk' },
      { ...habit, id: 2, name: 'Stretch' },
      { ...habit, id: 3, name: 'Water' },
      { ...habit, id: 4, name: 'Meds', today: day('done', 1) },
    ]);
    (await mock.next('GET', '/api/routines')).respond(200, [
      { id: 7, name: 'Morning', reminderTime: null, reminderChannels: [], habitIds: [3, 2] },
    ]);
    await phoneRegistered();
    await done;

    const snapshot = (update.mock.calls[0] as unknown as [WidgetSnapshot])[0];
    expect(snapshot.habits.map((h) => h.id)).toEqual([4, 3, 2, 1]);
    expect([snapshot.done, snapshot.total]).toEqual([1, 4]);
    expect(snapshot.next!.habits.map((h) => h.id)).toEqual([4, 3, 2, 1]);
  });

  it('leaves let-go habits out of the rows, the count and the more count', () => {
    const snapshot = widgetSnapshot('u1', 'Europe/Berlin', '2026-11-01', [habit, letGoHabit]);

    expect(snapshot.habits.map((h) => h.id)).toEqual([1]);
    expect([snapshot.more, snapshot.total]).toEqual([0, 1]);
    expect(JSON.stringify(snapshot)).not.toContain('Coffee');
  });

  it('carries up to 25 habits and counts the rest as more, still counting all of them in the total', () => {
    const habits = Array.from({ length: 27 }, (_, i) => ({ ...habit, id: i + 1, name: `Habit ${String(i + 1).padStart(2, '0')}` }));

    const snapshot = widgetSnapshot('u1', 'Europe/Berlin', '2026-11-01', habits);

    expect(snapshot.habits).toHaveLength(25);
    expect(snapshot.habits.at(-1)!.id).toBe(25);
    expect([snapshot.more, snapshot.total]).toEqual([2, 27]);
  });

  it('tells the widget its theme, colour scheme and motion at start and after each change, with motion off in calm mode', async () => {
    const setStyle = vi.fn(async () => undefined);
    applyGentleMotion(true);
    applyCalm(false);

    followWidgetStyle(true, async () => ({ EmberWidget: { setStyle } as unknown as EmberWidgetPlugin }));
    await vi.waitFor(() => expect(setStyle).toHaveBeenCalledTimes(1));
    expect(setStyle).toHaveBeenLastCalledWith({ theme: 'hearthlight', motion: true, colorScheme: 'system' });

    applyCalm(true);
    await vi.waitFor(() => expect(setStyle).toHaveBeenLastCalledWith({ theme: 'hearthlight', motion: false, colorScheme: 'system' }));
    applyCalm(false);
    applyGentleMotion(false);
    chooseColorScheme('dark');
    chooseTheme('tidepool');
    await vi.waitFor(() => expect(setStyle).toHaveBeenLastCalledWith({ theme: 'tidepool', motion: false, colorScheme: 'dark' }));
    expect(localStorage.getItem('ember.gentleMotion')).toBe('0');

    applyGentleMotion(true);
    localStorage.removeItem('ember.colorScheme');
    document.documentElement.classList.remove('dark');
    resetTheme();
  });

  it('sends the same style again after a failed call, on the next change of any input', async () => {
    const setStyle = vi
      .fn<() => Promise<void>>()
      .mockRejectedValueOnce(new Error('not ready'))
      .mockResolvedValue(undefined);
    applyGentleMotion(true);
    applyCalm(false);

    followWidgetStyle(true, async () => ({ EmberWidget: { setStyle } as unknown as EmberWidgetPlugin }));
    await vi.waitFor(() => expect(setStyle).toHaveBeenCalledTimes(1));
    applyGentleMotion(true);
    applyTheme('hearthlight');

    await vi.waitFor(() => expect(setStyle).toHaveBeenCalledTimes(2));
    applyTheme('hearthlight');
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(setStyle).toHaveBeenCalledTimes(2);
    resetTheme();
  });

  it('clears the widget and the names setting with the reminders', async () => {
    reminderNames.set(true);

    await reminders.clearAll();

    expect(clear).toHaveBeenCalledTimes(1);
    expect(reminderNames.get()).toBe(false);
    expect(localStorage.getItem('ember.reminderNames')).toBeNull();
  });
});
