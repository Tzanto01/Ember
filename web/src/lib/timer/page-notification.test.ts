import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Store } from '../store';
import type { TimerRecord } from './habit-timer';
import { PageNotification } from './page-notification';
import { timerSound } from './timer-sound';

const record: TimerRecord = {
  userId: 'u1',
  habitId: 7,
  name: 'Walk',
  minutes: 30,
  date: '2026-11-01',
  endsAt: 1,
  remainingMs: null,
};

describe('PageNotification', () => {
  const requestPermission = vi.fn();
  const showNotification = vi.fn(async (_title: string, _options: NotificationOptions) => undefined);
  const close = vi.fn();
  let names: Store<boolean>;
  let worker: ServiceWorkerRegistration | null;
  let page: PageNotification;

  function browser(permission: NotificationPermission, visibility: DocumentVisibilityState) {
    vi.stubGlobal('Notification', Object.assign(vi.fn(), { permission, requestPermission }));
    vi.spyOn(document, 'visibilityState', 'get').mockReturnValue(visibility);
    page = new PageNotification(async () => worker, names);
  }

  beforeEach(() => {
    vi.clearAllMocks();
    names = new Store(false);
    worker = {
      showNotification,
      getNotifications: async () => [{ close }],
    } as unknown as ServiceWorkerRegistration;
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    timerSound.set(false);
  });

  it.each<[NotificationPermission, DocumentVisibilityState]>([
    ['granted', 'visible'],
    ['default', 'hidden'],
    ['denied', 'hidden'],
  ])('shows and asks nothing with permission %s on a %s page', async (permission, visibility) => {
    browser(permission, visibility);
    await expect(page.ended(record)).resolves.toBe(false);
    expect(showNotification).not.toHaveBeenCalled();
    expect(requestPermission).not.toHaveBeenCalled();
    expect(page.allowed.get()).toBe(permission === 'granted');
  });

  it('shows a silent, nameless notification that opens Today at the habit', async () => {
    browser('granted', 'hidden');
    await expect(page.ended(record)).resolves.toBe(true);

    expect(showNotification).toHaveBeenCalledWith('Ember', expect.objectContaining({
      silent: true,
      data: { onActionClick: { default: expect.objectContaining({ url: '/today?habit=7' }) } },
    }));
    expect(JSON.stringify(showNotification.mock.calls)).not.toContain('Walk');
  });

  it('names the habit and may sound when the settings say so', async () => {
    names.set(true);
    timerSound.set(true);
    browser('granted', 'hidden');
    await page.ended(record);
    expect(showNotification).toHaveBeenCalledWith('Walk', expect.objectContaining({ silent: false }));
  });

  it('stays quiet when cleared before the worker answered, and closes one that was shown', async () => {
    browser('granted', 'hidden');
    const shown = page.ended(record);
    await page.clear();
    await shown;
    expect(showNotification).not.toHaveBeenCalled();

    await page.ended(record);
    await page.clear();
    expect(close).toHaveBeenCalled();
  });

  it('reports nothing shown when the browser refuses, so the page can chime', async () => {
    worker = null;
    browser('granted', 'hidden');
    vi.mocked(Notification as unknown as () => void).mockImplementation(() => {
      throw new TypeError('Illegal constructor');
    });
    await expect(page.ended(record)).resolves.toBe(false);
  });
});
