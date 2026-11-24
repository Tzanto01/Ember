import type { LocalNotificationSchema, LocalNotificationsPlugin } from '@capacitor/local-notifications';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApiClient } from '../api-client';
import type { DeviceStore } from '../device-store';
import i18n from '../i18n';
import { applyCalm } from '../theme';
import type { TimerRecord } from '../timer/habit-timer';
import { timerSound } from '../timer/timer-sound';
import { capacitorPlugin } from '@/test/test-support';
import { reminderNames } from './reminder-names';
import { Reminders } from './reminders';
import { TimerNotification, timerNotificationId } from './timer-notification';
import type { EmberWidgetPlugin } from './widget';

const end = Date.parse('2026-11-01T10:30:00Z');
const record: TimerRecord = {
  userId: 'u1',
  habitId: 7,
  name: 'Walk',
  minutes: 30,
  date: '2026-11-01',
  endsAt: end,
  remainingMs: null,
};

/** The plugin as Android keeps it: pending notifications by id, the ones shown, and the channels. */
class FakeAndroid {
  pending = new Map<number, LocalNotificationSchema>();
  shown = new Set<number>();
  channels = new Map<string, { importance?: number; vibration?: boolean; name: string }>();
  display = 'granted';
  answer = 'granted';
  exact = 'granted';
  asked = 0;
  hold: Promise<void> = Promise.resolve();

  plugin = capacitorPlugin({
    checkPermissions: async () => ({ display: this.display }),
    requestPermissions: async () => {
      this.asked++;
      this.display = this.answer;
      return { display: this.display };
    },
    checkExactNotificationSetting: async () => {
      await this.hold;
      return { exact_alarm: this.exact };
    },
    createChannel: async (channel: { id: string; name: string }) => void this.channels.set(channel.id, channel),
    schedule: async ({ notifications }: { notifications: LocalNotificationSchema[] }) => {
      for (const n of notifications) {
        this.shown.delete(n.id);
        this.pending.set(n.id, n);
      }
    },
    getPending: async () => ({ notifications: [...this.pending.values()] }),
    cancel: async ({ notifications }: { notifications: { id: number }[] }) => {
      for (const { id } of notifications) this.pending.delete(id);
    },
    removeDeliveredNotificationsById: async ({ ids }: { ids: number[] }) => {
      for (const id of ids) this.shown.delete(id);
    },
    removeAllDeliveredNotifications: async () => this.shown.clear(),
  }) as unknown as LocalNotificationsPlugin;

  fire() {
    for (const id of this.pending.keys()) this.shown.add(id);
    this.pending.clear();
  }
}

describe('TimerNotification', () => {
  let android: FakeAndroid;
  let timer: TimerNotification;
  const scheduled = () => android.pending.get(timerNotificationId);

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'], now: end - 30 * 60_000 });
    android = new FakeAndroid();
    timer = new TimerNotification(async () => ({ LocalNotifications: android.plugin }));
  });

  afterEach(async () => {
    await timer.clear();
    vi.useRealTimers();
    reminderNames.set(false);
    timerSound.set(false);
    applyCalm(false);
  });

  it('is scheduled at the end, silent and nameless, and leads to the habit', async () => {
    await timer.running(record);

    expect(scheduled()).toMatchObject({
      title: 'Ember',
      channelId: 'timer',
      extra: { userId: 'u1', habitId: 7, date: '2026-11-01', timer: true },
      schedule: { at: new Date(end), allowWhileIdle: true },
      isExactNotification: true,
    });
    expect(JSON.stringify(scheduled())).not.toContain('Walk');
    expect(android.channels.get('timer')).toMatchObject({ importance: 2, vibration: false });
    expect(timer.allowed.get()).toBe(true);
  });

  it('follows names, sound, calm mode and language while the timer runs', async () => {
    await timer.running(record);
    const settled = () => timer.check();

    reminderNames.set(true);
    timerSound.set(true);
    await settled();
    expect(scheduled()).toMatchObject({ title: 'Walk', channelId: 'timer-sound' });
    expect(android.channels.get('timer-sound')).toMatchObject({ importance: 3, vibration: false });

    applyCalm(true);
    const english = scheduled()?.body;
    await i18n.changeLanguage('de');
    await settled();
    expect(scheduled()).toMatchObject({ channelId: 'timer' });
    expect(scheduled()?.body).not.toBe(english);
  });

  it('leaves nothing pending or shown after a clear', async () => {
    await timer.running(record);
    await timer.clear();
    expect(android.pending.size).toBe(0);

    await timer.running(record);
    android.fire();
    await timer.clear();
    expect(android.shown.size).toBe(0);
  });

  it('keeps what was delivered when a setting changes after the end', async () => {
    await timer.running(record);
    vi.setSystemTime(end + 1000);
    android.fire();
    timerSound.set(true);
    await timer.check();

    expect(android.shown).toContain(timerNotificationId);
    expect(android.pending.size).toBe(0);
  });

  it('ends with nothing scheduled when it is cleared while being scheduled', async () => {
    let go: () => void = () => undefined;
    android.hold = new Promise((resolve) => (go = () => resolve()));
    const running = timer.running(record);
    await vi.waitFor(() => expect(android.channels.size).toBe(1));
    const cleared = timer.clear();
    reminderNames.set(true);
    go();
    await Promise.all([running, cleared]);

    expect(android.pending.size).toBe(0);
  });

  it('asks for the permission only for a start, only when Android never asked, and schedules nothing without it', async () => {
    android.display = 'prompt';
    android.answer = 'denied';
    await timer.running(record);
    expect(android.asked).toBe(0);

    await timer.running(record, true);
    await timer.running(record, true);
    expect(android.asked).toBe(1);
    expect(android.pending.size).toBe(0);
    expect(timer.allowed.get()).toBe(false);
  });

  it('schedules nothing on an Android without channels, where it could not stay silent', async () => {
    android.plugin.createChannel = async () => {
      throw new Error('unavailable');
    };
    await timer.running(record);
    expect(android.pending.size).toBe(0);
    expect(timer.allowed.get()).toBe(false);
  });

  describe('next to the reminders', () => {
    let reminders: Reminders;
    const reminder = { id: 500, title: 'Ember', body: '' };

    beforeEach(async () => {
      const offline = { listHabits: async () => [], listRoutines: async () => [] } as unknown as ApiClient;
      const device = { saveHabits: async () => undefined, edits: async () => [] } as unknown as DeviceStore;
      const widget = { update: async () => undefined, clear: async () => undefined } as unknown as EmberWidgetPlugin;
      reminders = new Reminders(offline, device, async () => ({ LocalNotifications: android.plugin }), async () => ({ EmberWidget: widget }));
      await timer.running(record);
      android.pending.set(reminder.id, reminder);
    });

    it('survives a rebuild and a pause of the reminders, which still cancel their own', async () => {
      Object.assign(android.plugin, {
        registerActionTypes: async () => undefined,
        getDeliveredNotifications: async () => ({ notifications: [] }),
      });
      await reminders.rebuild('u1', 'Europe/Berlin');
      expect([...android.pending.keys()]).toEqual([timerNotificationId]);

      android.pending.set(reminder.id, reminder);
      await reminders.pause();
      expect([...android.pending.keys()]).toEqual([timerNotificationId]);
    });

    it('leaves the reminders alone when it is cleared', async () => {
      android.shown.add(501);
      await timer.clear();
      expect([...android.pending.keys()]).toEqual([reminder.id]);
      expect([...android.shown]).toEqual([501]);
    });

    it('goes with the reminders at the end of the session', async () => {
      await reminders.clearAll();
      await timer.clear();
      expect(android.pending.size).toBe(0);
      expect(android.shown.size).toBe(0);
    });

    it('is never accepted as a reminder’s Done', async () => {
      const extra = { userId: 'u1', habitId: 7, date: '2026-11-01', timer: true };
      localStorage.setItem('ember.reminderIssued', JSON.stringify([{ id: timerNotificationId, ...extra }]));
      expect(reminders.wasIssued(timerNotificationId, extra)).toBe(false);

      const write = vi.fn();
      const api = { listHabits: async () => [{ id: 7, name: 'Walk', timesPerDay: 1, today: { date: '2026-11-01', count: 0 } }], setDayCount: write };
      const tapped = new Reminders(api as unknown as ApiClient, {} as DeviceStore);
      await expect(tapped.checkIn('u1', 'Europe/Berlin', extra, {} as never)).resolves.toBeNull();
      expect(write).not.toHaveBeenCalled();
      localStorage.removeItem('ember.reminderIssued');
    });
  });
});
