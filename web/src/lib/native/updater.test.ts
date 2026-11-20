import { afterEach, describe, expect, it, vi } from 'vitest';
import { mockFetch } from '@/lib/test-support';
import { betaUpdate, checkForUpdate, installBetaUpdate, offerBetaUpdate, parseManifest } from './updater';

const plugin = vi.hoisted(() => ({
  canInstall: vi.fn(async () => ({ value: true })),
  downloadAndInstall: vi.fn(async () => undefined),
  addListener: vi.fn(async () => ({ remove: async () => undefined })),
}));

vi.mock('@capacitor/core', () => ({
  Capacitor: { isPluginAvailable: (name: string) => name === 'EmberUpdater' },
  registerPlugin: () => plugin,
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

describe('installBetaUpdate', () => {
  it('starts one download when pressed twice', async () => {
    offerBetaUpdate(manifest);

    await Promise.all([installBetaUpdate(), installBetaUpdate()]);

    expect(plugin.downloadAndInstall).toHaveBeenCalledTimes(1);
    expect(betaUpdate.get().phase).toBe('offer');
  });
});

describe('parseManifest', () => {
  const origin = 'https://beta.example.test';

  it('keeps only the known fields of a newer build and a size when it is a positive integer', () => {
    expect(parseManifest({ ...manifest, size: 1234, note: 'x' }, origin, 3)).toEqual({ ...manifest, size: 1234 });
    expect(parseManifest({ ...manifest, size: null }, origin, 3)).toEqual(manifest);
  });

  it.each([
    ['an http download', { url: 'http://beta.example.test/download/ember-beta.apk' }],
    ['a download from another origin', { url: 'https://evil.example.test/download/ember-beta.apk' }],
    ['a download from a look-alike host', { url: 'https://beta.example.test.evil.test/a.apk' }],
    ['a url that is not absolute', { url: '/download/ember-beta.apk' }],
    ['a fractional version code', { versionCode: 4.5 }],
    ['a zero size', { size: 0 }],
    ['a blank version name', { versionName: ' ' }],
    ['a short checksum', { sha256: 'ab'.repeat(31) }],
    ['a checksum that is not hex', { sha256: 'zz'.repeat(32) }],
  ])('refuses %s', (_name, change) => {
    expect(parseManifest({ ...manifest, ...change }, origin, 3)).toBeNull();
  });

  it.each([null, 'latest', undefined])('refuses a body that is %s', (body) => {
    expect(parseManifest(body, origin, 3)).toBeNull();
  });

  it('refuses a build that is not newer than the installed one', () => {
    expect(parseManifest(manifest, origin, 4)).toBeNull();
  });
});
