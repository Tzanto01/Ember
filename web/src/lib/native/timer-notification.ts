import type { LocalNotificationSchema, LocalNotificationsPlugin } from '@capacitor/local-notifications';
import { currentLanguage, onLanguageChange, t } from '../i18n';
import { Store } from '../store';
import { calmMode, notificationColor, themeApplied } from '../theme';
import type { TimerNotifier, TimerRecord } from '../timer/habit-timer';
import { timerSound, timerSounds } from '../timer/timer-sound';
import { timerNotificationId } from './reminder-plan';
import { reminderNames } from './reminder-names';

const id = timerNotificationId;

function due({ endsAt }: TimerRecord): boolean {
  return endsAt !== null && endsAt > Date.now();
}

function runOf({ userId, habitId, endsAt }: TimerRecord): string {
  return JSON.stringify([userId, habitId, endsAt]);
}

/**
 * Android app: one local notification at the running timer's end, so it arrives with the app closed and offline.
 * Without exact alarms Android may deliver it late, so then an end the app sees itself shows it at once. It
 * touches only {@link timerNotificationId}, names the habit only while `reminderNames` is on, and goes through a silent
 * channel unless the timer may sound. A change of names, sound, calm mode, language or theme while a timer runs
 * schedules it again. Every call runs after the one before and works on the latest wish, so a notification cleared
 * while it was being scheduled never stays.
 */
export class TimerNotification implements TimerNotifier {
  readonly allowed = new Store(false);
  readonly late = new Store(false);
  private chain: Promise<void> = Promise.resolve();
  private wanted: TimerRecord | null = null;
  private ask = false;
  private scheduled: { run: string; exact: boolean } | null = null;
  private readonly channels = new Map<string, string>();

  constructor(
    private readonly notifications: () => Promise<{ LocalNotifications: LocalNotificationsPlugin }> = () =>
      import('@capacitor/local-notifications'),
  ) {
    const again = () => {
      if (this.wanted) void this.queue(() => this.apply());
    };
    for (const store of [reminderNames, timerSound, calmMode]) store.subscribe(again);
    themeApplied.subscribe(again);
    onLanguageChange(again);
  }

  check(): Promise<void> {
    return this.queue(async () => {
      const plugin = (await this.notifications()).LocalNotifications;
      const { granted, exact } = await this.grants(plugin);
      const record = this.wanted;
      const followed = granted ? record && this.scheduledFor(record)?.exact === exact : !this.scheduled;
      if (record && due(record) && !followed) await this.apply();
      else this.show(granted, exact);
    });
  }

  running(record: TimerRecord, ask = false): Promise<void> {
    this.wanted = record;
    this.ask ||= ask;
    return this.queue(() => this.apply());
  }

  async ended(record: TimerRecord): Promise<boolean> {
    this.wanted = record;
    let told = false;
    await this.queue(async () => {
      told = await this.tell(record);
    });
    return told;
  }

  clear(): Promise<void> {
    this.wanted = null;
    this.ask = false;
    return this.queue(() => this.apply());
  }

  private queue(run: () => Promise<void>): Promise<void> {
    this.chain = this.chain.then(run).catch(() => undefined);
    return this.chain;
  }

  /** True when the notification of `record`'s end is shown, its exact alarm is set, or the user already acted on it. */
  private async tell(record: TimerRecord): Promise<boolean> {
    const plugin = (await this.notifications()).LocalNotifications;
    const { granted, exact } = await this.grants(plugin);
    this.show(granted, exact);
    if (!granted) return false;
    const { notifications: delivered } = await plugin.getDeliveredNotifications();
    if (delivered.some((notification) => notification.id === id)) return true;
    const pending = (await plugin.getPending()).notifications.some((notification) => notification.id === id);
    if (exact || !pending) return pending || !!this.scheduledFor(record);
    if (!(await this.channelReady(plugin)) || this.wanted !== record) return false;
    await plugin.schedule({ notifications: [{ ...this.content(record), isExactNotification: false }] });
    return true;
  }

  private async apply(): Promise<void> {
    const record = this.wanted;
    const plugin = (await this.notifications()).LocalNotifications;
    if (!record) {
      this.scheduled = null;
      await plugin.cancel({ notifications: [{ id }] }).catch(() => undefined);
      await plugin.removeDeliveredNotificationsById({ ids: [id] });
      return;
    }
    const current = () => this.wanted === record && due(record);
    if (!current()) return;
    const ask = this.ask;
    this.ask = false;
    const { granted, exact } = await this.grants(plugin, ask);
    const ready = granted && (await this.channelReady(plugin));
    this.show(ready, exact);
    if (!current()) return;
    if (!ready) {
      this.scheduled = null;
      await plugin.cancel({ notifications: [{ id }] });
      return;
    }
    try {
      await plugin.schedule({
        notifications: [
          {
            ...this.content(record),
            schedule: { at: new Date(record.endsAt!), allowWhileIdle: true },
            isExactNotification: exact,
          },
        ],
      });
      this.scheduled = { run: runOf(record), exact };
    } catch {
      this.scheduled = null;
      this.show(false, exact);
      await plugin.cancel({ notifications: [{ id }] });
    }
  }

  private content(record: TimerRecord): LocalNotificationSchema {
    return {
      id,
      title: reminderNames.get() ? record.name : 'Ember',
      body: t('timer.notify.body'),
      channelId: this.channel().id,
      iconColor: notificationColor(),
      extra: { userId: record.userId, habitId: record.habitId, date: record.date, timer: true },
    };
  }

  private scheduledFor(record: TimerRecord): { exact: boolean } | null {
    return this.scheduled?.run === runOf(record) ? this.scheduled : null;
  }

  private show(allowed: boolean, exact: boolean): void {
    this.allowed.set(allowed);
    this.late.set(allowed && !exact);
  }

  /** The notification permission, asked for when `ask` and the system never did, and whether exact alarms are allowed. */
  private async grants(plugin: LocalNotificationsPlugin, ask = false): Promise<{ granted: boolean; exact: boolean }> {
    let { display } = await plugin.checkPermissions();
    if (display === 'prompt' && ask) ({ display } = await plugin.requestPermissions());
    const exact = (await plugin.checkExactNotificationSetting().catch(() => null))?.exact_alarm === 'granted';
    return { granted: display === 'granted', exact };
  }

  private channel(): { id: string; name: string; description: string; importance: 2 | 3 } {
    return timerSounds()
      ? { id: 'timer-sound', name: t('timer.channel.soundName'), description: t('timer.channel.soundDescription'), importance: 3 }
      : { id: 'timer', name: t('timer.channel.name'), description: t('timer.channel.description'), importance: 2 };
  }

  /** Creates the channel the notification goes to; false when Android has none, where the notification couldn't be silent. */
  private async channelReady(plugin: LocalNotificationsPlugin): Promise<boolean> {
    const channel = this.channel();
    const language = currentLanguage();
    if (this.channels.get(channel.id) === language) return true;
    try {
      await plugin.createChannel({ ...channel, vibration: false });
    } catch {
      return false;
    }
    this.channels.set(channel.id, language);
    return true;
  }
}
