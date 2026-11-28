import type { Page } from '@playwright/test';

/** The browser's fixed "now" for visual baselines: a Wednesday, noon UTC. */
export const visualNow = '2026-11-18T12:00:00Z';

const habit = {
  frequency: 'daily',
  weeklyTarget: null,
  days: null,
  timesPerDay: 1,
  reminderTime: null,
  reminderChannels: [],
  pausedFrom: null,
  pausedUntil: null,
  createdAt: '2026-09-01T08:00:00Z',
  routineId: null,
  routinePosition: null,
};

function dates(from: string, days: number[]): string[] {
  const start = Date.parse(`${from}T00:00:00Z`);
  return days.map((day) => new Date(start + day * 86_400_000).toISOString().slice(0, 10));
}

/** Device data for no-account mode: three habits with a month of check-ins up to the day before `visualNow`. */
function localData() {
  const walk = dates(
    '2026-10-01',
    [...Array(48).keys()].filter((day) => day % 7 !== 3 && day !== 40),
  );
  const read = dates(
    '2026-10-20',
    [...Array(29).keys()].filter((day) => day % 3 !== 0),
  );
  const stretch = dates('2026-11-10', [...Array(8).keys()]);
  const logs = (habitId: number, days: string[]) =>
    days.map((date) => ({ habitId, date, completed: true, count: 1 }));
  return {
    profile: {
      userId: '00000000-0000-4000-8000-000000000001',
      timezone: 'UTC',
      graceDaysPerWeek: 1,
      theme: null,
      showStreaks: true,
      tipsSeen: ['streaks', 'private-journal', 'routines', 'android', 'discord'],
      suggestPictures: false,
      createdAt: '2026-09-01T08:00:00Z',
      lastBackupAt: '2026-11-01T08:00:00Z',
      backupReminderSnoozedUntil: null,
      nextIds: { habit: 4, routine: 1, mood: 1 },
    },
    habits: [
      { ...habit, id: 1, name: 'Morning walk' },
      { ...habit, id: 2, name: 'Read ten pages', createdAt: '2026-10-20T08:00:00Z' },
      { ...habit, id: 3, name: 'Stretch', createdAt: '2026-11-10T08:00:00Z' },
    ],
    logs: [...logs(1, walk), ...logs(2, read), ...logs(3, stretch)],
    slips: [],
    lowDays: [],
    routines: [],
    moods: [],
    notes: [],
    habitNotes: [],
  };
}

/** Puts the page into no-account mode with the synthetic device data above; call before the first visit to the screen. */
export async function seedLocalMode(page: Page): Promise<void> {
  await page.goto('/legal/privacy');
  await page.evaluate(async (data) => {
    const db = await new Promise<IDBDatabase>((resolve, reject) => {
      const open = indexedDB.open('ember-local', 1);
      open.onupgradeneeded = () => open.result.createObjectStore('data');
      open.onsuccess = () => resolve(open.result);
      open.onerror = () => reject(open.error);
    });
    await new Promise<void>((resolve, reject) => {
      const transaction = db.transaction('data', 'readwrite');
      transaction.objectStore('data').put(data, 'data');
      transaction.oncomplete = () => resolve();
      transaction.onerror = () => reject(transaction.error);
    });
    db.close();
    localStorage.setItem('ember.mode', 'local');
  }, localData());
}

/** Waits for fonts and for the arrival animation and finite animations to end. */
export async function settled(page: Page): Promise<void> {
  await page.waitForFunction(() => !document.querySelector('main[data-arrive]'));
  await page.evaluate(async () => {
    await document.fonts.ready;
    await Promise.all(
      document
        .getAnimations()
        .filter((animation) =>
          Number.isFinite(animation.effect?.getComputedTiming().endTime as number),
        )
        .map((animation) => animation.finished.catch(() => undefined)),
    );
    await new Promise((done) => requestAnimationFrame(() => requestAnimationFrame(done)));
  });
}
