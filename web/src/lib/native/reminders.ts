import type { LocalNotificationsPlugin } from '@capacitor/local-notifications';
import { type ApiClient, unreachable } from '../api-client';
import { type DeviceStore, withDayEdit } from '../device-store';
import { env } from '../env';
import { currentLanguage, t } from '../i18n';
import { addDays, isLocalDate } from '../local-date';
import { localModeOn } from '../local/mode';
import { type AnyHabit, isLetGo, type LocalDateString, type ReminderDestination, type Routine } from '../models';
import { type OfflineQueue, shouldDrop } from '../offline-queue';
import { asApiError } from '../problem';
import { notificationColor } from '../theme';
import { reminderNames } from './reminder-names';
import { cachedPhone, forgetPhone, installId, phoneLabel, type ReminderPhone, rememberPhone } from './phone';
import { localDate, type PlannedReminder, planReminders, type ReminderTarget, settled, timerNotificationId } from './reminder-plan';
import { updaterAvailable } from './updater';
import { capacitorWidget, type EmberWidgetPlugin, heldRows, widgetSnapshot } from './widget';

/** Lock-screen text: names no habit (contract, web push rule). */
export const reminderBody = () => t('reminders.body');
/** Body of the second reminder (contract v1.8). */
export const secondReminderBody = () => t('reminders.secondBody');
/** Body of a let-go habit's quiet check-in (v1.21); names the habit only when reminder names are on. */
export const letGoReminderBody = (name: string | null) =>
  name ? t('letgo.reminder.body', { name }) : t('letgo.reminder.bodyNameless');
/** The notification action type carrying the "Done" button (single-habit reminders, with or without names). */
export const doneActionType = 'ember-done';
/** The `actionId` of the "Done" button. */
export const doneAction = 'done';
const channelId = 'reminders';
const issuedKey = 'ember.reminderIssued';
const plannedKey = 'ember.reminderPlanned';
const exactAskedKey = 'ember.exactAlarmAsked';

interface Issued {
  id: number;
  userId: string;
  habitId: number;
  date: LocalDateString;
}

function readIssued(): Issued[] {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(issuedKey) ?? '[]');
    return Array.isArray(value) ? (value as Issued[]) : [];
  } catch {
    return [];
  }
}

async function digest(value: unknown): Promise<string> {
  const bytes = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(JSON.stringify(value)));
  return Array.from(new Uint8Array(bytes), (b) => b.toString(16).padStart(2, '0')).join('');
}

const capacitorNotifications = () => import('@capacitor/local-notifications');

/** A validated check-in target from a notification `extra` or a widget tap. */
export interface CheckInTarget {
  habitId: number;
  date: LocalDateString;
}

/**
 * `extra` as a check-in for `userId`: a string `userId` equal to it, a positive integer `habitId` and a real
 * `yyyy-MM-dd` `date`; anything else (other user, routine reminder, malformed) is null.
 */
export function checkInTarget(extra: unknown, userId: string): CheckInTarget | null {
  if (typeof extra !== 'object' || extra === null) return null;
  const { userId: owner, habitId, date } = extra as Record<string, unknown>;
  if (typeof owner !== 'string' || owner !== userId) return null;
  if (typeof habitId !== 'number' || !Number.isSafeInteger(habitId) || habitId <= 0) return null;
  if (typeof date !== 'string' || !isLocalDate(date)) return null;
  return { habitId, date };
}

/** The habit ({@link checkInTarget}) or routine reminder of `userId` that `extra` names; null for anything else, the habit timer's included. */
function remindedOf(extra: unknown, userId: string): Pick<PlannedReminder, 'habitId' | 'routineId' | 'date'> | null {
  if (typeof extra !== 'object' || extra === null || (extra as { timer?: unknown }).timer === true) return null;
  const habit = checkInTarget(extra, userId);
  if (habit) return habit;
  const { userId: owner, routineId, date } = extra as Record<string, unknown>;
  if (typeof owner !== 'string' || owner !== userId) return null;
  if (typeof routineId !== 'number' || !Number.isSafeInteger(routineId) || routineId <= 0) return null;
  if (typeof date !== 'string' || !isLocalDate(date)) return null;
  return { routineId, date };
}

/**
 * Native build only: keeps the device's local reminder notifications in step with the habits and routines that
 * remind on this phone ({@link ReminderTarget}: in no-account mode the `mobile` channel; with an account this
 * phone's destination, registered on every online rebuild while notifications are allowed and cached for offline
 * ones), so they fire offline. The habit timer's notification ({@link timerNotificationId}) is left alone except by
 * {@link clearAll}. A rebuild whose plan differs from the last scheduled one replaces every
 * pending notification with it; an unchanged plan leaves them alone.
 */
export class Reminders {
  private chain: Promise<void> = Promise.resolve();
  private channelLanguage: string | null = null;
  private keyHandedOff = false;
  private cleared = 0;

  /** Called when a check-in was queued here without going through {@link ApiClient}; set by the composition root. */
  onCheckIn: ((habitId: number, date: LocalDateString) => void) | null = null;

  constructor(
    private readonly api: ApiClient,
    private readonly store: DeviceStore,
    private readonly notifications: () => Promise<{ LocalNotifications: LocalNotificationsPlugin }> = capacitorNotifications,
    private readonly widget: () => Promise<{ EmberWidget: EmberWidgetPlugin }> = capacitorWidget,
    private readonly exactAlarms: () => boolean = updaterAvailable,
  ) {}

  /**
   * Rebuilds the user's reminders from the current habit list (when offline: the cached list with the queued
   * day edits applied, so an offline check-in still cancels today's reminder), in `timeZone`, with a second
   * reminder `secondMinutes` after each one when above 0, pushes the widget's snapshot in the account's Today
   * `order` (marking the rows with queued day edits as held), and removes the delivered reminders that have
   * nothing left to remind of. Rebuilds run one after another; one still waiting when {@link clearAll} comes is dropped. Never throws.
   */
  rebuild(userId: string, timeZone: string, secondMinutes = 0, order: string[] | null = null): Promise<void> {
    const cleared = this.cleared;
    this.chain = this.chain
      .then(() => (cleared === this.cleared ? this.run(userId, timeZone, secondMinutes, order) : undefined))
      .catch(() => undefined);
    return this.chain;
  }

  /** Cancels every scheduled reminder (not the habit timer's notification) and clears the widget while the health-data consent is withdrawn. Never throws. */
  pause(): Promise<void> {
    this.chain = this.chain
      .then(async () => {
        localStorage.removeItem(plannedKey);
        await this.toWidget((widget) => widget.clear());
        await this.cancelPending();
      })
      .catch(() => undefined);
    return this.chain;
  }

  /**
   * Asks for the notification permission (Android 13+ `POST_NOTIFICATIONS`); true when granted. In the beta app,
   * once granted, also opens the "Alarms & reminders" setting once per install so reminders can fire on time.
   */
  async requestPermission(): Promise<boolean> {
    const plugin = (await this.notifications()).LocalNotifications;
    const status = await plugin.checkPermissions();
    const granted =
      status.display === 'granted' || (await plugin.requestPermissions()).display === 'granted';
    if (granted && this.exactAlarms() && !localStorage.getItem(exactAskedKey)) {
      localStorage.setItem(exactAskedKey, '1');
      if (!(await this.exactAllowed())) await this.allowExact();
    }
    return granted;
  }

  /**
   * Settings "This phone" (account mode): asks for the notification permission ({@link requestPermission}), then
   * registers this phone and turns its destination on. Resolves with the destination, or null when the permission
   * was refused; rejects with the API error when the server refuses or can't be reached. Rebuild the reminders
   * afterwards.
   */
  async enablePhone(): Promise<ReminderDestination | null> {
    if (!(await this.requestPermission())) return null;
    let destination = await this.api.registerPhone(installId(), phoneLabel());
    if (!destination.enabled) destination = await this.api.setReminderDestination(destination.id, true);
    rememberPhone(destination);
    return destination;
  }

  /**
   * Cancels every scheduled reminder and the habit timer's notification, clears delivered ones and the widget, forgets this phone's cached destination
   * and resets "Show habit names in reminders" (sign-out, session end, account deletion).
   */
  async clearAll(): Promise<void> {
    this.cleared++;
    reminderNames.set(false);
    this.keyHandedOff = false;
    forgetPhone();
    localStorage.removeItem(issuedKey);
    localStorage.removeItem(plannedKey);
    this.chain = this.chain
      .then(async () => {
        await this.toWidget((widget) => widget.clear());
        const plugin = (await this.notifications()).LocalNotifications;
        await this.cancelPending(true);
        await plugin.removeAllDeliveredNotifications();
      })
      .catch(() => undefined);
    return this.chain;
  }

  /**
   * True when this app scheduled a notification with "Done" under `id` carrying exactly `extra` (recent days),
   * so a notification action forged by another app through the exported activity is not acted on; never the habit timer's.
   */
  wasIssued(id: unknown, extra: unknown): boolean {
    if (id === timerNotificationId || typeof extra !== 'object' || extra === null) return false;
    const { userId, habitId, date } = extra as Record<string, unknown>;
    return readIssued().some(
      (issued) =>
        issued.id === id &&
        issued.userId === userId &&
        issued.habitId === habitId &&
        issued.date === date,
    );
  }

  /**
   * "Done" from a reminder or a widget tap: when `extra` is a valid {@link checkInTarget} for `userId` and names a
   * habit the app knows (live list, or offline the cached one with queued edits), adds one to that day's count
   * (capped at `timesPerDay`) with the absolute `PUT { count }`, queued in `queue` when the API is unreachable, and
   * resolves with the habit's name; anything stale, foreign or unreadable resolves null and writes nothing.
   */
  async checkIn(
    userId: string,
    timeZone: string,
    extra: unknown,
    queue: OfflineQueue,
  ): Promise<string | null> {
    const target = checkInTarget(extra, userId);
    if (!target || (extra as { timer?: unknown }).timer === true) return null;
    try {
      const loaded = await this.load(userId, timeZone);
      const day = loaded && loaded !== 'signedOut' ? await this.day(loaded.habits, target) : null;
      if (!day) return null;
      if (day.count < day.timesPerDay) await this.sendCount(target, day.count + 1, queue);
      return day.habit.name;
    } catch {
      return null;
    }
  }

  /**
   * Native build only: takes the widget's pending check-ins and, for each valid one of `userId` (a
   * {@link checkInTarget} with an integer `count` of at least 1), sends the absolute `PUT { count }` (capped at
   * `timesPerDay`) only while the habit's current count (as in {@link checkIn}) is lower, so a check-in the server
   * already has is never repeated; queued in `queue` when that `PUT` fails in any way the queue would retry
   * ({@link shouldDrop}). Without any habit list (offline, nothing cached) each is queued as is; when the habit list
   * answers 401 they are dropped, as are unknown and let-go habits. Never throws.
   */
  async replayWidget(userId: string, timeZone: string, queue: OfflineQueue): Promise<void> {
    const cleared = this.cleared;
    try {
      const { EmberWidget } = await this.widget();
      if (typeof EmberWidget.takePending !== 'function') return;
      const { checkIns } = await EmberWidget.takePending();
      const pending = (Array.isArray(checkIns) ? checkIns : []).flatMap((entry) => {
        const target = checkInTarget(entry, userId);
        return target && Number.isSafeInteger(entry.count) && entry.count >= 1 ? [{ target, count: entry.count }] : [];
      });
      if (!pending.length) return;
      const loaded = await this.load(userId, timeZone, cleared);
      if (loaded === 'signedOut') return;
      for (const { target, count } of pending) {
        try {
          if (!loaded) {
            await queue.add({ ...target, completed: null, count });
            this.onCheckIn?.(target.habitId, target.date);
            continue;
          }
          const day = await this.day(loaded.habits, target);
          const aimed = day ? Math.min(count, day.timesPerDay) : 0;
          if (day && day.count < aimed)
            await this.sendCount(target, aimed, queue).catch(async (error: unknown) => {
              if (!shouldDrop(asApiError(error))) await queue.add({ ...target, completed: null, count: aimed });
            });
        } catch (error) {
          if (!shouldDrop(asApiError(error))) await queue.add({ ...target, completed: null, count }).catch(() => undefined);
        }
      }
    } catch {}
  }

  /** The non-let-go habit `target` names in `habits` with its count that day (today's from the list, else `getDays`); null when unknown. */
  private async day(habits: AnyHabit[], target: CheckInTarget) {
    const habit = habits.find(({ id }) => id === target.habitId);
    if (!habit || isLetGo(habit)) return null;
    const count =
      habit.today.date === target.date
        ? habit.today.count
        : ((await this.api.getDays(habit.id, target.date, target.date))[0]?.count ?? 0);
    return { habit, count, timesPerDay: habit.timesPerDay ?? 1 };
  }

  /** The absolute `PUT { count }` for `target`, queued in `queue` when the API is unreachable; rejects otherwise. */
  private async sendCount(target: CheckInTarget, count: number, queue: OfflineQueue): Promise<void> {
    const edit = { ...target, completed: null, count };
    await this.api.setDayCount(edit.habitId, edit.date, edit.count).catch(async (error: unknown) => {
      if (!unreachable(error) || !(await queue.add(edit))) throw error;
      this.onCheckIn?.(target.habitId, target.date);
    });
  }

  /**
   * The habit list: live (and cached, unless {@link clearAll} came since `cleared` was read), or when the API can't answer the cached one with queued day edits applied;
   * `signedOut` on a 401, null when nothing is cached.
   */
  private async load(
    userId: string,
    timeZone: string,
    cleared = this.cleared,
  ): Promise<{ habits: AnyHabit[]; routines: Routine[]; live: boolean } | 'signedOut' | null> {
    try {
      const habits = await this.api.listHabits();
      const routines = await this.api
        .listRoutines()
        .catch(async () => (await this.store.habits(userId).catch(() => null))?.routines);
      await this.store.saveHabits({ userId, timeZone, habits, routines }).catch(() => undefined);
      if (cleared !== this.cleared) await this.store.deleteHabits(userId).catch(() => undefined);
      return { habits, routines: routines ?? [], live: true };
    } catch (error) {
      if (asApiError(error).status === 401) return 'signedOut';
      const cached = await this.store.habits(userId).catch(() => null);
      if (!cached) return null;
      const queued = await this.store.edits(userId).catch(() => []);
      return {
        habits: cached.habits.map((habit) =>
          isLetGo(habit) ? habit : queued.reduce((current, edit) => withDayEdit(current, edit), habit),
        ),
        routines: cached.routines ?? [],
        live: false,
      };
    }
  }

  private async run(userId: string, timeZone: string, secondMinutes: number, order: string[] | null): Promise<void> {
    const loaded = await this.load(userId, timeZone);
    if (!loaded || loaded === 'signedOut') return;
    const { habits, routines, live } = loaded;
    const now = Date.now();
    const today = localDate(now, timeZone);
    const queued = await this.store.edits(userId).catch(() => []);
    const day = widgetSnapshot(userId, timeZone, today, habits, routines, order);
    const next = widgetSnapshot(userId, timeZone, addDays(today, 1), habits, routines, order);
    await this.toWidget((widget) =>
      widget.update({
        ...day,
        held: heldRows(day, queued),
        next: {
          date: next.date,
          habits: next.habits,
          more: next.more,
          done: next.done,
          total: next.total,
          held: heldRows(next, queued),
        },
      }),
    );
    if (live && !localModeOn()) await this.handOffKey();
    const plugin = (await this.notifications()).LocalNotifications;
    await this.clearSettled(plugin, userId, habits, routines);
    const target: ReminderTarget = localModeOn()
      ? { mode: 'local' }
      : {
          mode: 'account',
          phone: await this.phone(
            plugin,
            live,
            () =>
              planReminders(habits, timeZone, now, routines, 0, {
                mode: 'account',
                phone: { id: '', enabled: true },
              }).length > 0,
          ),
        };
    const plan = planReminders(habits, timeZone, now, routines, secondMinutes, target);
    const names = reminderNames.get();
    const language = currentLanguage();
    if (this.channelLanguage !== language) {
      await plugin.createChannel({
        id: channelId,
        name: t('reminders.channelName'),
        description: t('reminders.channelDescription'),
        importance: 3,
      });
      await plugin.registerActionTypes({
        types: [
          { id: doneActionType, actions: [{ id: doneAction, title: t('reminders.done'), foreground: true }] },
        ],
      });
      this.channelLanguage = language;
    }
    if (!plan.length || !(await this.allowed(plugin))) {
      localStorage.removeItem(plannedKey);
      await this.cancelPending();
      return;
    }
    const iconColor = notificationColor();
    const exact = await this.exactAllowed();
    const habitName = new Map(habits.map((habit) => [habit.id, habit.name]));
    const routineName = new Map(routines.map((routine) => [routine.id, routine.name]));
    const notifications = plan.map((reminder) => {
      const name =
        reminder.habitId !== undefined
          ? habitName.get(reminder.habitId)
          : routineName.get(reminder.routineId ?? 0);
      const named = names && !!name;
      return {
        id: reminder.id,
        title: named ? name : 'Ember',
        body: reminder.letGo
          ? letGoReminderBody(named ? name : null)
          : reminder.second
            ? secondReminderBody()
            : reminderBody(),
        channelId,
        iconColor,
        actionTypeId: reminder.habitId !== undefined && !reminder.letGo ? doneActionType : undefined,
        extra: reminder.letGo
          ? { userId, habitId: reminder.habitId, date: reminder.date, letGo: true }
          : reminder.habitId !== undefined
            ? { userId, habitId: reminder.habitId, date: reminder.date }
            : { userId, routineId: reminder.routineId, date: reminder.date },
        schedule: { at: new Date(reminder.at), allowWhileIdle: true },
        isExactNotification: exact,
      };
    });
    const planned = await digest(notifications);
    if (localStorage.getItem(plannedKey) === planned) return;
    const recent = addDays(today, -2);
    const issued = new Map<string, Issued>();
    for (const entry of [
      ...readIssued().filter((entry) => entry.date >= recent && entry.date <= today),
      ...notifications
        .filter((n) => n.actionTypeId)
        .map((n) => ({ id: n.id, ...(n.extra as Omit<Issued, 'id'>) })),
    ]) {
      issued.set(`${entry.id}|${entry.userId}|${entry.habitId}|${entry.date}`, entry);
    }
    try {
      localStorage.setItem(issuedKey, JSON.stringify([...issued.values()]));
    } catch {
      localStorage.removeItem(issuedKey);
    }
    localStorage.removeItem(plannedKey);
    await this.cancelPending();
    await plugin.schedule({ notifications });
    try {
      localStorage.setItem(plannedKey, planned);
    } catch {}
  }

  /**
   * This phone's destination for an account-mode plan. Online and with notifications allowed (asked first while
   * Android has never asked and `wanted` says reminders would be planned), registers this phone and caches the
   * answer; otherwise, or when registering fails, the cached one (null when never registered).
   */
  private async phone(
    plugin: LocalNotificationsPlugin,
    live: boolean,
    wanted: () => boolean,
  ): Promise<ReminderPhone | null> {
    if (!live) return cachedPhone();
    const { display } = await plugin.checkPermissions();
    const granted =
      display === 'granted' ||
      (display === 'prompt' && wanted() && (await plugin.requestPermissions()).display === 'granted');
    if (!granted) return cachedPhone();
    try {
      return rememberPhone(await this.api.registerPhone(installId(), phoneLabel()));
    } catch {
      return cachedPhone();
    }
  }

  /** True when notifications may show; asks once while Android has never asked (a new phone or a reinstall). */
  private async allowed(plugin: LocalNotificationsPlugin): Promise<boolean> {
    const { display } = await plugin.checkPermissions();
    if (display === 'prompt') return (await plugin.requestPermissions()).display === 'granted';
    return display === 'granted';
  }

  /** True when Android lets Ember schedule exact alarms (Android 12+ "Alarms & reminders"; always below 12). */
  async exactAllowed(): Promise<boolean> {
    const plugin = (await this.notifications()).LocalNotifications;
    const status = await plugin.checkExactNotificationSetting().catch(() => null);
    return status?.exact_alarm === 'granted';
  }

  /** Opens Android's "Alarms & reminders" setting for Ember; true when exact alarms are allowed afterwards. */
  async allowExact(): Promise<boolean> {
    const plugin = (await this.notifications()).LocalNotifications;
    const status = await plugin.changeExactNotificationSetting().catch(() => null);
    return status?.exact_alarm === 'granted';
  }

  /**
   * Gives the widget a fresh key for its own check-ins once per app run and after every sign-out, and again whenever
   * it has none, so a key left from an ended session never outlives it (native builds that know `hasKey`, an absolute
   * `https://` API base only). Never throws.
   */
  private async handOffKey(): Promise<void> {
    if (!env.apiBase.startsWith('https://')) return;
    await this.toWidget(async (widget) => {
      if (typeof widget.hasKey !== 'function' || typeof widget.setKey !== 'function') return;
      if (this.keyHandedOff && (await widget.hasKey()).value) return;
      const { key } = await this.api.createWidgetKey();
      await widget.setKey({ key, apiBase: new URL(env.apiBase).origin });
      this.keyHandedOff = true;
    });
  }

  /**
   * Removes the delivered reminders of `userId` that have nothing left to remind of ({@link settled}), so a check-in
   * from anywhere clears that day's reminder. Android reports a delivered notification without its `extra`, so it is
   * read from the plugin's record of it (`getByIds`). Never throws.
   */
  private async clearSettled(
    plugin: LocalNotificationsPlugin,
    userId: string,
    habits: AnyHabit[],
    routines: Routine[],
  ): Promise<void> {
    try {
      const { notifications: delivered } = await plugin.getDeliveredNotifications();
      if (!delivered.length) return;
      const { notifications: records } = await plugin.getByIds({ ids: delivered.map(({ id }) => id) });
      const done = new Set(
        records.flatMap(({ id, extra }) => {
          const reminder = remindedOf(extra, userId);
          return reminder && settled(habits, routines, reminder) ? [id] : [];
        }),
      );
      const notifications = delivered.filter(({ id }) => done.has(id));
      if (notifications.length) await plugin.removeDeliveredNotifications({ notifications });
    } catch {}
  }

  private toWidget(call: (widget: EmberWidgetPlugin) => Promise<void>): Promise<void> {
    return this.widget()
      .then(({ EmberWidget }) => call(EmberWidget))
      .catch(() => undefined);
  }

  private async cancelPending(timerToo = false): Promise<void> {
    const plugin = (await this.notifications()).LocalNotifications;
    const pending = (await plugin.getPending()).notifications.filter(({ id }) => timerToo || id !== timerNotificationId);
    if (pending.length) await plugin.cancel({ notifications: pending.map(({ id }) => ({ id })) });
  }
}
