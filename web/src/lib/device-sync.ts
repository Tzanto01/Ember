import type { ApiClient } from './api-client';
import type { AuthService } from './auth';
import { isDayEdit } from './device-store';
import { reminderNames } from './native/reminder-names';
import { checkInTarget, doneAction, type Reminders } from './native/reminders';
import { capacitorWidget } from './native/widget';
import { browserTimeZone } from './local-date';
import { navigate } from './navigation';
import type { OfflineQueue } from './offline-queue';
import { Store } from './store';
import { themeApplied } from './theme';

/**
 * Keeps the device in step with the account: replays queued offline day edits when the user is known, when an
 * edit is queued, when the browser comes back online and (native) when the app resumes; in the native build
 * (`reminders` set) it also re-reads `me` on resume and rebuilds the local reminders after every sync and
 * whenever `me` holds a new time zone, second reminder setting or Today order, or the theme changes (the reminder icon follows
 * its colour). When a user is known whose zone was never set, it sends the device's zone (not UTC) once per user
 * on this device (a failure waits for the next app start). Native "Done" taps on a reminder this app issued ({@link Reminders.wasIssued})
 * and widget row taps check in through {@link Reminders.checkIn} and open Today (a widget tap with `open: true` only opens Today
 * at that habit); a plain tap on a habit reminder opens Today
 * at that habit (`/today?habit=<id>`).
 */
export class DeviceSync {
  private userId: string | null = null;
  private timeZone: string | null = null;
  private secondMinutes: number | null = null;
  private withdrawn = false;
  private todayOrder = 'null';

  /** Bumps after a replay sent or dropped something, so screens can reload. */
  readonly synced = new Store(0);
  /** Queued edits dropped since the app started (404, a day outside the editable range, or a body the server refused). */
  readonly dropped = new Store(0);
  /** The habit just checked in from a reminder or the widget (native), for a short confirmation; null when none. */
  readonly nativeCheckIn = new Store<{ name: string } | null>(null);

  constructor(
    private readonly auth: AuthService,
    private readonly api: ApiClient,
    private readonly queue: OfflineQueue,
    private readonly reminders: Reminders | null,
  ) {
    auth.me.subscribe(() => this.follow());
    queue.added.subscribe(() => void this.sync());
    if (reminders) {
      themeApplied.subscribe(() => this.rebuildReminders());
      reminderNames.subscribe(() => this.rebuildReminders());
    }
    this.follow();
  }

  /** Registers the online listener and, in the native build, the resume and notification-tap listeners. */
  start(): void {
    window.addEventListener('online', () => void this.sync());
    if (this.reminders) void this.listenNative();
  }

  /**
   * Replays the signed-in user's queue, then (native) the widget's pending check-ins ({@link Reminders.replayWidget}, after
   * the queue so a queued count can't lower one) and rebuilds the reminders. Never throws.
   */
  async sync(): Promise<void> {
    const me = this.auth.me.get();
    if (!me) return;
    const result = await this.queue
      .replay(me.id, (edit) =>
        isDayEdit(edit) ? this.api.sendDayEdit(edit, true) : this.api.sendNoteEdit(edit, true),
      )
      .catch(() => null);
    if (result?.dropped) this.dropped.update((count) => count + result.dropped);
    if (result && (result.sent || result.dropped)) this.synced.update((count) => count + 1);
    await this.reminders?.replayWidget(me.id, me.timezone, this.queue);
    this.rebuildReminders();
  }

  /** Native build only: rebuilds the local reminders for the signed-in user, or pauses them while the consent is withdrawn. */
  rebuildReminders(): void {
    const me = this.auth.me.get();
    if (!this.reminders || !me) return;
    void (me.consent?.withdrawnAt
      ? this.reminders.pause()
      : this.reminders.rebuild(me.id, me.timezone, me.secondReminderMinutes, me.todayOrder ?? null));
  }

  private follow(): void {
    const me = this.auth.me.get();
    const id = me?.id ?? null;
    if (id !== this.userId) {
      this.userId = id;
      this.queue.userId.set(id);
      this.nativeCheckIn.set(null);
      this.dropped.set(0);
      if (id) void this.queue.refreshCount(id).then(() => this.sync());
      if (id && !me?.timezoneSet) void this.claimTimeZone(id);
    }
    const timeZone = me?.timezone ?? null;
    const secondMinutes = me?.secondReminderMinutes ?? null;
    const withdrawn = !!me?.consent?.withdrawnAt;
    const todayOrder = JSON.stringify(me?.todayOrder ?? null);
    if (timeZone !== this.timeZone || secondMinutes !== this.secondMinutes || withdrawn !== this.withdrawn || todayOrder !== this.todayOrder) {
      this.timeZone = timeZone;
      this.secondMinutes = secondMinutes;
      this.withdrawn = withdrawn;
      this.todayOrder = todayOrder;
      this.rebuildReminders();
    }
  }

  private async claimTimeZone(id: string): Promise<void> {
    const timezone = browserTimeZone();
    const claimed = `ember.tzClaimed.${id}`;
    if (!timezone || timezone === 'UTC' || timezone === 'Etc/UTC' || localStorage.getItem(claimed)) return;
    const me = await this.api.updateMe({ timezone }).catch(() => null);
    if (!me) return;
    localStorage.setItem(claimed, '1');
    if (this.auth.me.get()?.id === id) this.auth.setMe(me);
  }

  private async listenNative(): Promise<void> {
    const { App } = await import('@capacitor/app');
    await App.addListener('resume', () => {
      void (async () => {
        await this.auth.loadMe(true).catch(() => null);
        await this.sync();
      })();
    });
    const { LocalNotifications } = await import('@capacitor/local-notifications');
    await LocalNotifications.addListener('localNotificationActionPerformed', (event) => {
      const { id, extra } = event.notification;
      const done = event.actionId === doneAction && !!this.reminders?.wasIssued(id, extra);
      void this.checkIn(done ? extra : null, event.actionId === doneAction ? null : extra);
    });
    const { EmberWidget } = await capacitorWidget();
    await EmberWidget.addListener('widgetTap', (tap) =>
      (tap as { open?: unknown } | null)?.open === true ? void this.checkIn(null, tap) : void this.checkIn(tap),
    );
  }

  /**
   * Checks in `extra` (validated by {@link Reminders.checkIn}) for the signed-in user, then opens Today, at the
   * habit of `tapped` when that is one of the user's habit reminders.
   */
  private async checkIn(extra: unknown, tapped: unknown = null): Promise<void> {
    const me = this.auth.me.get() ?? (await this.auth.loadMe().catch(() => null));
    const name = me && extra ? await this.reminders?.checkIn(me.id, me.timezone, extra, this.queue) : null;
    const habitId = me && tapped ? checkInTarget(tapped, me.id)?.habitId : undefined;
    const letGo = !!habitId && (tapped as { letGo?: unknown }).letGo === true;
    navigate(letGo ? `/history/${habitId}` : habitId ? `/today?habit=${habitId}` : '/today');
    if (name) this.nativeCheckIn.set({ name });
  }
}
