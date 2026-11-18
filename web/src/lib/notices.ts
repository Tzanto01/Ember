import { env } from './env';
import type { Language } from './i18n';
import { Store } from './store';

/** One entry of `/notices.json`, published by the operator while the app keeps running. */
export interface LiveNotice {
  id: string;
  kind: 'maintenance' | 'info';
  /** ISO date-time; shown from then on when set. */
  showFrom?: string;
  /** ISO date-time; shown until then when set. */
  showUntil?: string;
  en: string;
  de?: string;
}

/** How often the app asks for notices while it stays open. */
export const noticeRefreshMs = 10 * 60 * 1000;

const dismissedKey = 'ember.dismissedNotices';
const maxShown = 280;
const unreachableGapMs = 60_000;
const foregroundGapMs = 30_000;

const text = (value: unknown): value is string =>
  typeof value === 'string' && value.trim().length > 0;
const when = (value: unknown): value is string | undefined =>
  value === undefined || (typeof value === 'string' && !Number.isNaN(Date.parse(value)));

/**
 * The valid entries of a `/notices.json` body, in its order; a wrong shape gives none, unknown fields are ignored and a
 * null `showFrom` or `showUntil` counts as absent.
 */
export function parseNotices(body: unknown): LiveNotice[] {
  const list = body && typeof body === 'object' ? (body as { notices?: unknown }).notices : null;
  if (!Array.isArray(list)) return [];
  return list.flatMap((entry: unknown): LiveNotice[] => {
    if (!entry || typeof entry !== 'object') return [];
    const { id, kind, en, de } = entry as Record<string, unknown>;
    const showFrom = (entry as Record<string, unknown>)['showFrom'] ?? undefined;
    const showUntil = (entry as Record<string, unknown>)['showUntil'] ?? undefined;
    if (
      !text(id) ||
      (kind !== 'maintenance' && kind !== 'info') ||
      !text(en) ||
      !when(showFrom) ||
      !when(showUntil)
    )
      return [];
    return [
      {
        id,
        kind,
        en,
        ...(text(de) ? { de } : {}),
        ...(showFrom ? { showFrom } : {}),
        ...(showUntil ? { showUntil } : {}),
      },
    ];
  });
}

/**
 * The first notice whose window (`showFrom`, `showUntil`, both optional) holds `now`, skipping the `dismissed` ids
 * and, when `kind` is set, every other kind; null when none is.
 */
export function activeNotice(
  notices: readonly LiveNotice[],
  {
    now = new Date(),
    dismissed = [],
    kind,
  }: { now?: Date; dismissed?: readonly string[]; kind?: LiveNotice['kind'] } = {},
): LiveNotice | null {
  const at = now.getTime();
  return (
    notices.find(
      (notice) =>
        (!kind || notice.kind === kind) &&
        !dismissed.includes(notice.id) &&
        (!notice.showFrom || Date.parse(notice.showFrom) <= at) &&
        (!notice.showUntil || at < Date.parse(notice.showUntil)),
    ) ?? null
  );
}

/** The notice's text in `language`, falling back to English, cut to its first 280 characters. */
export function noticeText(notice: LiveNotice, language: Language): string {
  return Array.from(notice[language] ?? notice.en)
    .slice(0, maxShown)
    .join('');
}

function readDismissed(): string[] {
  try {
    const ids: unknown = JSON.parse(localStorage.getItem(dismissedKey) ?? '[]');
    return Array.isArray(ids) ? ids.filter((id): id is string => typeof id === 'string') : [];
  } catch {
    return [];
  }
}

/** The notices from the last fetch; empty after a failed or invalid one. */
export const liveNotices = new Store<LiveNotice[]>([]);

/** The ids this device dismissed (kept in local storage). */
export const dismissedNotices = new Store<string[]>(readDismissed());

/** Hides the notice `id` on this device from now on; a failing storage keeps it hidden for this session only. */
export function dismissNotice(id: string): void {
  if (dismissedNotices.get().includes(id)) return;
  const ids = [...dismissedNotices.get(), id];
  dismissedNotices.set(ids);
  try {
    localStorage.setItem(dismissedKey, JSON.stringify(ids));
  } catch {}
}

let inFlight: Promise<void> | null = null;
let lastFetch = -Infinity;

/**
 * Fetches `/notices.json` from the API base into {@link liveNotices} (empty when that fails or is invalid). Joins a
 * fetch already running; skips when the last one started less than `minGapMs` ago (never after the clock went back).
 */
export function refreshNotices(minGapMs = 0): Promise<void> {
  if (inFlight) return inFlight;
  const since = Date.now() - lastFetch;
  if (since >= 0 && since < minGapMs) return Promise.resolve();
  lastFetch = Date.now();
  inFlight = fetch(`${env.apiBase}/notices.json`, {
    cache: 'no-cache',
    signal: AbortSignal.timeout(15_000),
  })
    .then((response) => (response.ok ? response.json() : null))
    .then(
      (body: unknown) => liveNotices.set(parseNotices(body)),
      () => liveNotices.set([]),
    )
    .finally(() => {
      inFlight = null;
    });
  return inFlight;
}

/** Asks again when the API turned out unreachable, at most once a minute (the file is cached that long anyway). */
export function refreshNoticesUnreachable(): void {
  void refreshNotices(unreachableGapMs);
}

/**
 * Fetches notices now, every {@link noticeRefreshMs}, and when the app comes back to the foreground (the Capacitor
 * `resume` in the Android app, `visibilitychange` on the web) at most every 30 seconds.
 */
export function startNotices(native: boolean): void {
  void refreshNotices();
  setInterval(() => void refreshNotices(), noticeRefreshMs);
  if (native) {
    void import('@capacitor/app')
      .then(({ App }) => App.addListener('resume', () => void refreshNotices(foregroundGapMs)))
      .catch(() => undefined);
    return;
  }
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') void refreshNotices(foregroundGapMs);
  });
}
