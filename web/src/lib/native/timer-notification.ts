import type { LocalNotificationsPlugin } from '@capacitor/local-notifications';
import { currentLanguage, onLanguageChange, t } from '../i18n';
import { Store } from '../store';
import { calmMode, notificationColor, themeApplied } from '../theme';
import type { TimerNotifier, TimerRecord } from '../timer/habit-timer';
import { timerSound, timerSounds } from '../timer/timer-sound';
import { reminderNames } from './reminder-names';

/** The one notification id the habit timer uses; `reminderId()` never returns it. */
export const timerNotificationId = 1;

const id = timerNotificationId;

/**
 * Android app: one local notification at the running timer's end, so it arrives with the app closed and offline. It
 * touches only {@link timerNotificationId}, names the habit only while `reminderNames` is on, and goes through a silent
 * channel unless the timer may sound. A change of names, sound, calm mode, language or theme while a timer runs
 * schedules it again. Every call runs after the one before and works on the latest wish, so a notification cleared
 * while it was being scheduled never stays.
 */
export class TimerNotification implements TimerNotifier {
  readonly allowed = new Store(false);
  private chain: Promise<void> = Promise.resolve();
  private wanted: TimerRecord | null = null;
  private ask = false;
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
      this.allowed.set((await (await this.plugin()).checkPermissions()).display === 'granted');
    });
  }

  running(record: TimerRecord, ask = false): Promise<void> {
    this.wanted = record;
    this.ask ||= ask;
    return this.queue(() => this.apply());
  }

  ended(): Promise<boolean> {
    return Promise.resolve(true);
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

  private async plugin(): Promise<LocalNotificationsPlugin> {
    return (await this.notifications()).LocalNotifications;
  }

  private async apply(): Promise<void> {
    const record = this.wanted;
    const plugin = await this.plugin();
    if (!record) {
      await plugin.cancel({ notifications: [{ id }] }).catch(() => undefined);
      await plugin.removeDeliveredNotificationsById({ ids: [id] });
      return;
    }
    const due = () => this.wanted === record && record.endsAt !== null && record.endsAt > Date.now();
    if (!due()) return;
    const granted = await this.permitted(plugin);
    if (granted && !(await this.channelReady(plugin))) return;
    const exact = (await plugin.checkExactNotificationSetting().catch(() => null))?.exact_alarm === 'granted';
    if (!due()) return;
    if (!granted) {
      await plugin.cancel({ notifications: [{ id }] });
      return;
    }
    const channel = this.channel();
    await plugin.schedule({
      notifications: [
        {
          id,
          title: reminderNames.get() ? record.name : 'Ember',
          body: t('timer.notify.body'),
          channelId: channel.id,
          iconColor: notificationColor(),
          extra: { userId: record.userId, habitId: record.habitId, date: record.date, timer: true },
          schedule: { at: new Date(record.endsAt!), allowWhileIdle: true },
          isExactNotification: exact,
        },
      ],
    });
  }

  private async permitted(plugin: LocalNotificationsPlugin): Promise<boolean> {
    const ask = this.ask;
    this.ask = false;
    let { display } = await plugin.checkPermissions();
    if (display === 'prompt' && ask) ({ display } = await plugin.requestPermissions());
    this.allowed.set(display === 'granted');
    return display === 'granted';
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
      this.allowed.set(false);
      return false;
    }
    this.channels.set(channel.id, language);
    return true;
  }
}
