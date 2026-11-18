import { afterEach, describe, expect, it, vi } from 'vitest';
import { mockFetch } from '@/lib/test-support';
import { checkForUpdate } from './updater';

vi.mock('@capacitor/core', () => ({
  Capacitor: { isPluginAvailable: (name: string) => name === 'EmberUpdater' },
  registerPlugin: () => ({}),
}));

vi.mock('@capacitor/app', () => ({
  App: { getInfo: async () => ({ build: '3', version: '0.9.0-beta.2' }) },
}));

vi.mock('@/lib/env', () => ({
  env: { native: true, apiBase: 'https://beta.example.test', webOrigin: '' },
}));

const manifestUrl = 'https://beta.example.test/download/latest.json';
const manifest = {
  versionCode: 4,
  versionName: '0.9.0-beta.3',
  url: 'https://beta.example.test/download/ember-beta.apk',
  sha256: 'ab'.repeat(32),
};

async function checkWith(respond: (call: { respond: (status: number, body?: unknown) => void; fail: () => void }) => void) {
  const f = mockFetch();
  const result = checkForUpdate();
  respond(await f.next('GET', manifestUrl));
  return result;
}

describe('checkForUpdate', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('returns a newer build', async () => {
    expect(await checkWith((call) => call.respond(200, manifest))).toEqual({ status: 'update', update: manifest });
  });

  it.each([3, 2])('says the installed version is the latest when the manifest has versionCode %i', async (versionCode) => {
    expect(await checkWith((call) => call.respond(200, { ...manifest, versionCode }))).toEqual({
      status: 'current',
      versionName: '0.9.0-beta.2',
    });
  });

  it.each([
    ['the request fails', (call: { fail: () => void }) => call.fail()],
    ['the server errors', (call: { respond: (status: number) => void }) => call.respond(503)],
    ['the manifest is invalid', (call: { respond: (status: number, body: unknown) => void }) =>
      call.respond(200, { ...manifest, sha256: 'abc' })],
  ])('reports an error when %s', async (_name, respond) => {
    expect(await checkWith(respond as never)).toEqual({ status: 'error' });
  });
});
