import { screen } from '@testing-library/react';
import { vi } from 'vitest';
import { env } from '@/lib/env';
import { reminderNames } from '@/lib/native/reminder-names';
import { auth, localMode, push } from '@/lib/services';
import { applyCalm } from '@/lib/theme';
import SettingsPage from '@/pages/settings';
import { type FetchMock, me, mockFetch } from '@/test/test-support';
import { renderPage } from '@/test/test-utils';

let wide = false;

/** Test helper: lets the next renders of Settings see an `md` or wider screen, or not. */
export function setWide(value: boolean): void {
  wide = value;
}

/** Test helper for `beforeEach`: a phone-sized screen whose width query `setWide` can flip. */
export function stubSettingsMedia(): void {
  wide = false;
  window.matchMedia = (query: string) =>
    ({
      matches: query === '(min-width: 48rem)' && wide,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }) as unknown as MediaQueryList;
}

/** Test helper for `afterEach`: signs out and puts back what Settings tests change on the device. */
export function resetSettings(): void {
  vi.unstubAllGlobals();
  delete (navigator as { serviceWorker?: unknown }).serviceWorker;
  auth.clearSession();
  localStorage.clear();
  document.documentElement.removeAttribute('data-theme');
  applyCalm(false);
  env.native = false;
  reminderNames.set(false);
  localMode.mode.set('account');
  push.available.set(false);
  document.documentElement.classList.remove('dark');
}

/** Test helper: waits a moment for requests that should not be sent. */
export const settle = () => new Promise<void>((resolve) => setTimeout(resolve, 20));

/** Test helper: signs in as `me`, renders Settings at `url` and answers its `GET /api/me`. */
export async function openSettings(url: string) {
  auth.setMe(me);
  const fetch = mockFetch();
  const router = renderPage('/settings/:group?', SettingsPage, url);
  (await fetch.next('GET', '/api/me')).respond(200, me);
  await screen.findByRole('heading', { level: 1 });
  return { fetch, router };
}

/** Test helper: the router's path, query and hash. */
export function settingsPath(router: ReturnType<typeof renderPage>) {
  return router.state.location.pathname + router.state.location.search + router.state.location.hash;
}

/** Test helper: the `PATCH` requests still waiting for an answer. */
export function openPatches(fetch: FetchMock) {
  return fetch.pending.filter((call) => call.method === 'PATCH');
}
