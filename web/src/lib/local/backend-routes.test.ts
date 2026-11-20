import 'fake-indexeddb/auto';
import { describe, expect, it } from 'vitest';
import { ApiClient } from '../api-client';
import { DeviceStore } from '../device-store';
import type { Http, Method, RequestOptions } from '../http';
import { OfflineQueue } from '../offline-queue';
import { LocalBackend } from './backend';
import { LocalStore } from './store';

interface Sent {
  method: Method;
  path: string;
}

/**
 * Argument lists for the methods that take one object instead of an id and dates, or whose branches send to different
 * endpoints; every other method is called once with an id and two dates.
 */
const args: Record<string, unknown[][]> = {
  sendDayEdit: [[{ habitId: 1, date: '2026-11-01', completed: true }]],
  dayEdit: [[{ habitId: 1, date: '2026-11-01', completed: true }]],
  sendNoteEdit: [
    [{ kind: 'note', date: '2026-11-01', body: null }],
    [{ kind: 'note', date: '2026-11-01', body: { text: 'Synthetic' } }],
  ],
  saveHabitNote: [
    [1, '2026-11-01', { text: 'Synthetic' }],
    [1, '2026-11-01', null],
  ],
};

/**
 * Every request the {@link ApiClient} prototype methods make to Ember's own API in no-account mode (requests marked
 * `server` go to the server in that mode too and are left out). Methods defined as class fields are not seen.
 */
async function clientRequests(): Promise<{ sent: Sent[]; silent: string[] }> {
  const sent: Sent[] = [];
  const silent: string[] = [];
  let made = 0;
  const record = (method: Method, url: string, options: RequestOptions = {}) => {
    made++;
    if (!options.server) sent.push({ method, path: url.split('?')[0] });
    return Promise.reject(new Error('recorded'));
  };
  const http = { request: record, download: (url: string) => record('GET', url) } as unknown as Http;
  const api = new ApiClient(http, new OfflineQueue(new DeviceStore()));
  const client = api as unknown as Record<string, (...args: unknown[]) => unknown>;
  for (const name of Object.getOwnPropertyNames(ApiClient.prototype)) {
    if (name === 'constructor') continue;
    const before = made;
    for (const list of args[name] ?? [[1, '2026-11-01', '2026-11-02']]) {
      try {
        await client[name].call(api, ...list);
      } catch {
        // Every call fails once its request is recorded.
      }
    }
    if (made === before) silent.push(name);
  }
  return { sent, silent };
}

/** ApiClient methods that send nothing themselves (URL builders for full-page navigation, the habit cache). */
const helpers = ['discordLinkUrl', 'discordLoginUrl', 'remember'];

/**
 * Endpoints only the server has; no-account mode answers them 404 (see {@link LocalBackend}). A new endpoint lands here
 * only when no-account mode can't reach it; otherwise it needs a local route.
 */
const serverOnly = [
  // Sign-in, sign-up and the email account.
  'POST /api/auth/confirm-email-code',
  'POST /api/auth/confirmEmail',
  'POST /api/auth/forgotPassword',
  'POST /api/auth/login',
  'POST /api/auth/logout-everywhere',
  'POST /api/auth/register',
  'POST /api/auth/resendConfirmationEmail',
  'POST /api/auth/resetPassword',
  'POST /api/me/delete',
  'POST /api/me/consent/decline',
  'POST /api/me/consent/withdraw',
  // Two-factor sign-in.
  'GET /api/me/2fa',
  'POST /api/me/2fa/disable',
  'POST /api/me/2fa/enable',
  'POST /api/me/2fa/recovery-codes',
  'POST /api/me/2fa/setup',
  // Discord and merging accounts.
  'DELETE /api/account/merge',
  'DELETE /api/me/discord',
  'GET /api/account/merge',
  'POST /api/account/merge',
  'POST /api/auth/discord/2fa',
  'POST /api/auth/discord/link/start',
  // Import into an account.
  'POST /api/me/import',
  // Private journal and its sealed helps.
  'DELETE /api/me/journal-key',
  'GET /api/me/helps',
  'GET /api/me/journal-key',
  'POST /api/me/journal-key/turn-off',
  'PUT /api/me/helps',
  'PUT /api/me/journal-key',
  // Server reminder destinations, the phone's registration and the widget key.
  'GET /api/me/reminder-destinations',
  'PATCH /api/me/reminder-destinations/1',
  'POST /api/me/phones',
  'POST /api/me/widget-key',
].sort();

describe('the no-account backend', () => {
  it('answers every request the API client makes, apart from the server-only endpoints', async () => {
    const { sent, silent } = await clientRequests();
    expect(silent.sort()).toEqual(helpers);

    const backend = new LocalBackend(new LocalStore(), { native: false });
    const missing: string[] = [];
    for (const { method, path } of sent) {
      const response = await backend.handle(method, path, null);
      if (response.status === 404) missing.push(`${method} ${path}`);
    }
    expect([...new Set(missing)].sort()).toEqual(serverOnly);
  });
});
