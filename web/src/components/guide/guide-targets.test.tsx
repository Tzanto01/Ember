import 'fake-indexeddb/auto';
import { IDBFactory } from 'fake-indexeddb';
import { waitFor } from '@testing-library/react';
import type { ComponentType } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { findGuideTarget } from '@/lib/guide/highlight';
import { type GuideScreen, guides } from '@/lib/guide/registry';
import { api, auth, localMode } from '@/lib/services';
import { me, mockFetch } from '@/test/test-support';
import FiresidePage from '@/pages/fireside';
import HabitEditPage from '@/pages/habit-edit';
import HistoryPage from '@/pages/history';
import RoutinesPage from '@/pages/routines';
import SettingsPage from '@/pages/settings';
import TodayPage from '@/pages/today';
import { renderPage } from '@/test/test-utils';

interface Screen {
  /** No-account mode with one daily habit with a timer length checked in today, or an account with nothing loaded beyond `me`. */
  mode: 'local' | 'account';
  path: string;
  url: (habitId: number) => string;
  Page: ComponentType;
}

const screens: Record<GuideScreen, Screen> = {
  today: { mode: 'local', path: '/today', url: () => '/today', Page: TodayPage },
  history: { mode: 'local', path: '/history', url: () => '/history', Page: HistoryPage },
  habit: { mode: 'local', path: '/habits/:id/edit', url: (id) => `/habits/${id}/edit`, Page: HabitEditPage },
  routines: { mode: 'local', path: '/routines', url: () => '/routines', Page: RoutinesPage },
  settings: { mode: 'account', path: '/settings/:group?', url: () => '/settings', Page: SettingsPage },
  reminders: { mode: 'account', path: '/settings/:group?', url: () => '/settings/reminders', Page: SettingsPage },
  habitsDays: { mode: 'account', path: '/settings/:group?', url: () => '/settings/habits', Page: SettingsPage },
  languageTime: { mode: 'account', path: '/settings/:group?', url: () => '/settings/language', Page: SettingsPage },
  journal: { mode: 'account', path: '/settings/:group?', url: () => '/settings/journal', Page: SettingsPage },
  appearance: { mode: 'account', path: '/settings/:group?', url: () => '/settings/appearance', Page: SettingsPage },
  privacy: { mode: 'account', path: '/settings/:group?', url: () => '/settings/privacy', Page: SettingsPage },
  fireside: { mode: 'account', path: '/fireside', url: () => '/fireside', Page: FiresidePage },
};

describe('guide "Show me" targets', { timeout: 15_000 }, () => {
  beforeEach(() => {
    (globalThis as unknown as { indexedDB: unknown }).indexedDB = new IDBFactory();
    localStorage.clear();
    window.matchMedia = () =>
      ({ matches: false, addEventListener: () => undefined, removeEventListener: () => undefined }) as unknown as MediaQueryList;
  });

  afterEach(() => {
    localMode.finishUpgrade();
    auth.clearSession();
    vi.unstubAllGlobals();
  });

  it.each(Object.entries(screens) as [GuideScreen, Screen][])('all exist on the %s screen', async (screen, { mode, path, url, Page }) => {
    let habitId = 0;
    if (mode === 'local') {
      await localMode.start();
      habitId = (await api.createHabit({ name: 'Walk', frequency: 'daily', timerMinutes: 30 })).id;
      await api.setDay(habitId, auth.me.get()!.localToday, true);
    } else {
      mockFetch();
      auth.setMe(me);
    }

    renderPage(path, Page, url(habitId));

    for (const { id, target } of guides[screen].entries) {
      if (!target) continue;
      await waitFor(() => expect(findGuideTarget(target), `${id} shows "${target}"`).not.toBeNull());
    }
  });
});
