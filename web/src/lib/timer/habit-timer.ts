import { type ApiClient, unreachable } from '../api-client';
import { type DayEdit, type DeviceStore, withDayEdit } from '../device-store';
import { addDays, isLocalDate } from '../local-date';
import { limits } from '../local/limits';
import { type AnyHabit, type DayEntry, type Habit, isLetGo, type LocalDateString, type Me } from '../models';
import { localDate, pausedOn } from '../native/reminder-plan';
import type { OfflineQueue } from '../offline-queue';
import { type ApiError, asApiError } from '../problem';
import { Store } from '../store';
import { playChime, prepareChime, timerSound } from './timer-sound';

/** The device's one habit timer as stored; never sent to the server. Exactly one of `endsAt` and `remainingMs` is set. */
export interface TimerRecord {
  userId: string;
  habitId: number;
  name: string;
  minutes: number;
  /** The local day (account time zone) the timer started on. */
  date: LocalDateString;
  /** Epoch milliseconds of the end while running or ended, null while paused. */
  endsAt: number | null;
  /** What is left while paused, else null. */
  remainingMs: number | null;
  /** True once the end was told on this device. */
  told?: boolean;
}

export type TimerPhase = 'running' | 'paused' | 'ended';

export interface TimerView {
  record: TimerRecord;
  phase: TimerPhase;
}

/** Whether the ended timer may offer "Mark as done", and whether that would be for yesterday. */
export interface TimerOffer {
  canCheckIn: boolean;
  yesterday: boolean;
}

/** `done`: checked in (or queued offline). `unavailable`: nothing to check in any more. `failed`: the timer stays. */
export type TimerConfirm = { status: 'done' } | { status: 'unavailable' } | { status: 'failed'; error: ApiError };

/** Tells the end of a timer outside the app's own screen. Its methods never reject. */
export interface TimerNotifier {
  /** True when this device would show a notification at the end. */
  readonly allowed: Store<boolean>;
  /** Reads the notification permission again. */
  check(): Promise<void>;
  /** A timer runs until `record.endsAt`; `ask` may ask for the permission when the system never did. */
  running(record: TimerRecord, ask?: boolean): Promise<void>;
  /** The end was seen just now; true when the notifier told it, so the page plays no chime. */
  ended(record: TimerRecord): Promise<boolean>;
  /** Removes whatever is scheduled or shown. */
  clear(): Promise<void>;
}

export interface TimerDeps {
  api: Pick<ApiClient, 'listHabits' | 'getDays' | 'setDay' | 'setDayCount' | 'seen'>;
  device: Pick<DeviceStore, 'habits' | 'edits'>;
  queue: Pick<OfflineQueue, 'add'>;
  me: Store<Me | null>;
  notifier: TimerNotifier;
  /** Called after {@link HabitTimer.confirm} checked in, so screens reload. */
  onConfirmed: () => void;
}

const storageKey = 'ember.habitTimer';
const tellWithinMs = 2 * 60_000;
const clockSlackMs = 24 * 3_600_000;
const recheckMs = 30_000;

function positiveInt(value: unknown): value is number {
  return Number.isSafeInteger(value) && (value as number) > 0;
}

function parseRecord(value: unknown): TimerRecord | null {
  if (typeof value !== 'object' || value === null) return null;
  const { userId, habitId, name, minutes, date, endsAt, remainingMs, told } = value as Record<string, unknown>;
  if (typeof userId !== 'string' || !userId || typeof name !== 'string' || !name) return null;
  if (!positiveInt(habitId) || !positiveInt(minutes) || minutes > limits.timerMinutes) return null;
  if (typeof date !== 'string' || !isLocalDate(date)) return null;
  const length = minutes * 60_000;
  const base = { userId, habitId, name, minutes, date };
  if (endsAt === null && positiveInt(remainingMs) && remainingMs <= length) return { ...base, endsAt, remainingMs };
  if (remainingMs !== null || !positiveInt(endsAt) || endsAt > Date.now() + length + clockSlackMs) return null;
  return { ...base, endsAt, remainingMs, ...(told === true ? { told } : {}) };
}

/** The day `entry` shows with the queued `edits` for `habit` applied. */
function withQueued(habit: Habit, entry: DayEntry, edits: Parameters<typeof withDayEdit>[1][]): DayEntry {
  return edits.reduce((current, edit) => withDayEdit(current, edit), { ...habit, today: entry }).today;
}

/**
 * The device's one habit timer: a wall-clock end time in localStorage (`ember.habitTimer`), so reloads, sleep, throttled
 * tabs and time changes never bend it, shared between this browser's tabs and bound to the signed-in user. It never
 * checks anything in by itself; {@link confirm} does on the user's tap. Nothing here throws.
 */
export class HabitTimer {
  /** The signed-in user's timer, or null when there is none. */
  readonly view = new Store<TimerView | null>(null);
  /** True when this device would show a notification at the end. */
  readonly notifies: Store<boolean>;
  private memory: TimerRecord | null = null;
  private wake: ReturnType<typeof setTimeout> | undefined;
  private ensured = '';
  private confirming: Promise<TimerConfirm> | null = null;

  constructor(private readonly deps: TimerDeps) {
    this.notifies = deps.notifier.allowed;
    deps.me.subscribe(() => this.evaluate());
  }

  /** Starts following the page, the other tabs and the stored timer; returns the function that stops it. */
  listen(): () => void {
    const evaluate = () => this.evaluate();
    const check = () => this.check();
    const stored = (event: StorageEvent) => {
      if (event.key === storageKey || event.key === null) this.evaluate();
    };
    document.addEventListener('visibilitychange', check);
    window.addEventListener('focus', evaluate);
    window.addEventListener('pageshow', evaluate);
    window.addEventListener('storage', stored);
    if (!this.read()) void this.deps.notifier.clear();
    this.check();
    return () => {
      clearTimeout(this.wake);
      document.removeEventListener('visibilitychange', check);
      window.removeEventListener('focus', evaluate);
      window.removeEventListener('pageshow', evaluate);
      window.removeEventListener('storage', stored);
    };
  }

  /** Re-reads the stored timer against the clock and the notification permission (app resume). */
  check(): void {
    void this.deps.notifier.check();
    this.evaluate();
  }

  /** Starts `habit`'s timer on today's local day, replacing any other one; does nothing without a length. */
  start(habit: Pick<Habit, 'id' | 'name' | 'timerMinutes'>): void {
    const me = this.user();
    const minutes = habit.timerMinutes;
    if (!me || !positiveInt(minutes) || minutes > limits.timerMinutes) return;
    prepareChime();
    const now = Date.now();
    this.run(
      {
        userId: me.id,
        habitId: habit.id,
        name: habit.name,
        minutes,
        date: localDate(now, me.timezone),
        endsAt: now + minutes * 60_000,
        remainingMs: null,
      },
      true,
    );
  }

  pause(): void {
    const record = this.own();
    if (!record || record.endsAt === null || record.endsAt <= Date.now()) return this.evaluate();
    this.write({ ...record, endsAt: null, remainingMs: Math.min(record.endsAt - Date.now(), record.minutes * 60_000) });
    void this.deps.notifier.clear();
    this.evaluate();
  }

  resume(): void {
    const record = this.own();
    if (!record || record.remainingMs === null) return this.evaluate();
    prepareChime();
    this.run({ ...record, endsAt: Date.now() + record.remainingMs, remainingMs: null }, false);
  }

  /** Stops or dismisses the timer and leaves nothing of it. */
  stop(): void {
    void this.drop();
  }

  /** What is left in milliseconds, 0 once it ended or without a timer. */
  remaining(): number {
    const record = this.view.get()?.record;
    if (!record) return 0;
    if (record.endsAt === null) return record.remainingMs ?? 0;
    return Math.min(Math.max(record.endsAt - Date.now(), 0), record.minutes * 60_000);
  }

  /** A check-in of `habitId` on `date`: an ended timer of that habit and day is over. A running one keeps running. */
  checkedIn(habitId: number, date: LocalDateString): void {
    const view = this.current();
    if (view?.phase === 'ended' && view.record.habitId === habitId && view.record.date === date) void this.drop();
  }

  /** Follows a freshly loaded habit list: clears the timer of a habit that is gone or let-go, and takes over a new name. */
  reconcile(habits: readonly AnyHabit[]): void {
    const record = this.own();
    if (!record) return;
    const habit = habits.find(({ id }) => id === record.habitId);
    if (!habit || isLetGo(habit)) void this.drop();
    else if (habit.name !== record.name) {
      this.write({ ...record, name: habit.name });
      this.evaluate();
    }
  }

  /** Removes the timer and its notification, whoever it belongs to (consent withdrawn, backup restored). */
  clear(): Promise<void> {
    return this.drop();
  }

  /** {@link clear} at the end of a session, also turning "Sound when a timer ends" off. */
  endSession(): Promise<void> {
    timerSound.set(false);
    return this.drop();
  }

  /**
   * Whether the ended timer can offer its check-in: its day is today or yesterday in the account's zone, and the habit
   * exists, isn't paused and isn't done that day (offline by what the device knows, queued edits included).
   */
  async offer(): Promise<TimerOffer> {
    const view = this.current();
    if (view?.phase !== 'ended') return { canCheckIn: false, yesterday: false };
    const day = await this.openDay(view.record).catch(() => null);
    const today = this.today();
    return {
      canCheckIn: !!day && this.still(view.record),
      yesterday: !!today && view.record.date === addDays(today, -1),
    };
  }

  /** "Mark as done": checks the ended timer's day in, one count for a habit with several a day. Taps while it runs share one. */
  confirm(): Promise<TimerConfirm> {
    this.confirming ??= this.checkIn().finally(() => (this.confirming = null));
    return this.confirming;
  }

  private async checkIn(): Promise<TimerConfirm> {
    const view = this.current();
    if (view?.phase !== 'ended') return { status: 'unavailable' };
    const { record } = view;
    let day: { count: number; timesPerDay: number } | null;
    try {
      day = await this.openDay(record);
    } catch (error) {
      return { status: 'failed', error: asApiError(error) };
    }
    if (!day || !this.still(record)) return { status: 'unavailable' };
    const { habitId, date } = record;
    const count = day.timesPerDay > 1 ? day.count + 1 : null;
    const edit: DayEdit =
      count === null ? { habitId, date, completed: true } : { habitId, date, completed: null, count };
    try {
      await (count === null ? this.deps.api.setDay(habitId, date, true) : this.deps.api.setDayCount(habitId, date, count));
    } catch (error) {
      const queued = unreachable(error) && this.still(record) && (await this.deps.queue.add(edit));
      if (!queued) return { status: 'failed', error: asApiError(error) };
    }
    if (this.still(record)) void this.drop();
    this.deps.onConfirmed();
    return { status: 'done' };
  }

  /** The record's day while it is open and can still be checked in, else null. */
  private async openDay(record: TimerRecord): Promise<{ count: number; timesPerDay: number } | null> {
    const today = this.today();
    const { habitId, date } = record;
    if (!today || (date !== today && date !== addDays(today, -1))) return null;
    const habit = await this.habit(record);
    if (!habit) return null;
    if (date < habit.startDate || pausedOn(habit, date)) return null;
    const queued = await this.deps.device.edits(record.userId).catch(() => []);
    let entry: DayEntry | undefined = habit.today.date === date ? habit.today : undefined;
    if (!entry) {
      const [day] = await this.deps.api.getDays(habitId, date, date).catch((error: unknown) => {
        if (unreachable(error)) return [];
        throw error;
      });
      entry = day;
    }
    if (!entry && queued.some((edit) => edit.kind !== 'note' && edit.habitId === habitId && edit.date === date))
      entry = { date, state: 'pending', completed: null, count: 0, skipped: false };
    if (!entry) return null;
    const { state, count } = withQueued(habit, entry, queued);
    const timesPerDay = habit.timesPerDay ?? 1;
    if (state === 'paused' || state === 'beforestart' || state === 'done' || count >= timesPerDay) return null;
    return { count, timesPerDay };
  }

  /** The record's build habit: from the server, else as the app last saw it, else as cached on this device. */
  private async habit({ userId, habitId }: TimerRecord): Promise<Habit | null> {
    let habits: AnyHabit[];
    try {
      habits = await this.deps.api.listHabits();
    } catch (error) {
      const { status } = asApiError(error);
      if (status === 401 || status === 403) return null;
      const seen = this.deps.api.seen(habitId);
      habits = seen ? [seen] : ((await this.deps.device.habits(userId).catch(() => null))?.habits ?? []);
    }
    const habit = habits.find(({ id }) => id === habitId);
    return habit && !isLetGo(habit) ? habit : null;
  }

  private today(): LocalDateString | null {
    const me = this.deps.me.get();
    return me ? localDate(Date.now(), me.timezone) : null;
  }

  /** The signed-in user who may have a timer: none while the health-data consent is withdrawn. */
  private user(): Me | null {
    const me = this.deps.me.get();
    return me && !me.consent?.withdrawnAt ? me : null;
  }

  private own(): TimerRecord | null {
    const record = this.read();
    return record && record.userId === this.user()?.id ? record : null;
  }

  private current(): TimerView | null {
    const record = this.own();
    if (!record) return null;
    return { record, phase: record.endsAt === null ? 'paused' : record.endsAt > Date.now() ? 'running' : 'ended' };
  }

  /** True while `record` is still the signed-in user's timer, the same run of it. */
  private still(record: TimerRecord): boolean {
    const own = this.own();
    return !!own && own.userId === record.userId && own.habitId === record.habitId && own.endsAt === record.endsAt;
  }

  private run(record: TimerRecord, ask: boolean): void {
    this.write(record);
    this.ensured = this.runKey(record);
    void this.deps.notifier.running(record, ask);
    this.evaluate();
  }

  private runKey(record: TimerRecord): string {
    return JSON.stringify([record.userId, record.habitId, record.name, record.endsAt]);
  }

  private evaluate(): void {
    clearTimeout(this.wake);
    const me = this.deps.me.get();
    const stored = this.read();
    if (me && (stored ? !this.own() : this.view.get())) {
      void this.drop();
      return;
    }
    let view = this.current();
    const telling = view?.phase === 'ended' && !view.record.told;
    if (view && telling) {
      view = { ...view, record: { ...view.record, told: true } };
      this.write(view.record);
    }
    if (JSON.stringify(view) !== JSON.stringify(this.view.get())) this.view.set(view);
    if (view && telling) this.tell(view.record);
    if (view?.phase !== 'running' || view.record.endsAt === null) return;
    this.wake = setTimeout(() => this.evaluate(), Math.min(view.record.endsAt - Date.now(), recheckMs));
    if (this.ensured === this.runKey(view.record)) return;
    this.ensured = this.runKey(view.record);
    void this.deps.notifier.running(view.record);
  }

  private tell(record: TimerRecord): void {
    if (Date.now() - (record.endsAt ?? 0) > tellWithinMs) return;
    void this.deps.notifier.ended(record).then((told) => {
      if (!told && this.still(record)) playChime();
    });
  }

  private drop(): Promise<void> {
    clearTimeout(this.wake);
    this.memory = null;
    this.ensured = '';
    try {
      localStorage.removeItem(storageKey);
    } catch {}
    this.view.set(null);
    return this.deps.notifier.clear();
  }

  private write(record: TimerRecord): void {
    try {
      localStorage.setItem(storageKey, JSON.stringify(record));
      this.memory = null;
    } catch {
      this.memory = record;
    }
  }

  private read(): TimerRecord | null {
    if (this.memory) return this.memory;
    try {
      const raw = localStorage.getItem(storageKey);
      if (raw === null) return null;
      let record: TimerRecord | null = null;
      try {
        record = parseRecord(JSON.parse(raw));
      } catch {
        record = null;
      }
      if (!record) localStorage.removeItem(storageKey);
      return record;
    } catch {
      return null;
    }
  }
}
