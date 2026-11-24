import { t } from '../i18n';
import { navigate } from '../navigation';
import { Store } from '../store';
import type { TimerNotifier, TimerRecord } from './habit-timer';
import { timerSounds } from './timer-sound';

const tag = 'ember-timer';

function granted(): boolean {
  return typeof Notification !== 'undefined' && Notification.permission === 'granted';
}

/**
 * Web build: one notification from the page when a timer ends while the page is hidden, only with a notification
 * permission the browser already has (it never asks). Names the habit only while `showNames` is on.
 */
export class PageNotification implements TimerNotifier {
  readonly allowed = new Store(granted());
  /** Always false: the page shows its notification itself, at the end. */
  readonly late = new Store(false);
  private chain: Promise<unknown> = Promise.resolve();
  private clears = 0;
  private shown: Notification | null = null;

  constructor(
    private readonly registration: () => Promise<ServiceWorkerRegistration | null>,
    private readonly showNames: Pick<Store<boolean>, 'get'>,
  ) {}

  check(): Promise<void> {
    this.allowed.set(granted());
    return Promise.resolve();
  }

  running(): Promise<void> {
    void this.clear();
    return this.check();
  }

  ended(record: TimerRecord): Promise<boolean> {
    if (document.visibilityState !== 'hidden' || !granted()) return Promise.resolve(false);
    const clears = this.clears;
    return this.queue(async () => clears !== this.clears || (await this.show(record)), false);
  }

  clear(): Promise<void> {
    this.clears++;
    this.shown?.close();
    this.shown = null;
    if (!granted()) return Promise.resolve();
    return this.queue(async () => {
      const registration = await this.registration();
      for (const notification of (await registration?.getNotifications({ tag })) ?? []) notification.close();
    }, undefined);
  }

  private queue<T>(run: () => Promise<T>, fallback: T): Promise<T> {
    const next = this.chain.then(run).catch(() => fallback);
    this.chain = next;
    return next;
  }

  private async show(record: TimerRecord): Promise<boolean> {
    const url = `/today?habit=${record.habitId}`;
    const title = this.showNames.get() ? record.name : 'Ember';
    const options: NotificationOptions = {
      body: t('timer.notify.body'),
      tag,
      silent: !timerSounds(),
      data: { onActionClick: { default: { operation: 'navigateLastFocusedOrOpen', url } } },
    };
    const clears = this.clears;
    const registration = await this.registration().catch(() => null);
    if (clears !== this.clears) return true;
    if (registration) {
      await registration.showNotification(title, options);
      return true;
    }
    const notification = new Notification(title, options);
    notification.onclick = () => {
      window.focus();
      notification.close();
      navigate(url);
    };
    this.shown = notification;
    return true;
  }
}
