import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Capacitor } from '@capacitor/core';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { mockFetch } from '@/test/test-support';
import { env } from '../env';
import {
  type DeviceDiagnostics,
  DiagnosticsError,
  diagnosticsAvailable,
  diagnosticsBody,
  sendDiagnostics,
} from './diagnostics';

vi.mock('../env', () => ({ env: { native: false, apiBase: '' } }));

const fixtures = join(dirname(fileURLToPath(import.meta.url)), '../../../../tests/fixtures/diagnostics');
const fixture = (name: string) => readFileSync(join(fixtures, name), 'utf8');
const requestOf = (name: string) => {
  const { platform: _platform, ...device } = JSON.parse(fixture(name)) as DeviceDiagnostics & { platform: string };
  return device;
};
const request = JSON.parse(fixture('request.json')) as DeviceDiagnostics & { platform: string };
const device = requestOf('request.json');

describe('diagnosticsBody', () => {
  it.each([
    ['request.json', 'body.json'],
    ['request-escapes.json', 'body-escapes.json'],
  ])('builds from %s the bytes of %s', (requestFile, bodyFile) => {
    const body = diagnosticsBody(requestOf(requestFile), {
      ref: 'EMB-7K3M9QX2',
      createdAt: '2026-11-20T18:45:12.345Z',
    });

    expect(JSON.stringify(body)).toBe(fixture(bodyFile));
  });
});

describe('diagnosticsAvailable', () => {
  afterEach(() => {
    env.native = false;
    vi.restoreAllMocks();
  });

  it('is true in the Android app only', () => {
    const platform = vi.spyOn(Capacitor, 'getPlatform');
    env.native = true;
    platform.mockReturnValue('android');
    expect(diagnosticsAvailable()).toBe(true);
    platform.mockReturnValue('ios');
    expect(diagnosticsAvailable()).toBe(false);
    env.native = false;
    platform.mockReturnValue('android');
    expect(diagnosticsAvailable()).toBe(false);
  });
});

describe('sendDiagnostics', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('posts the request with the installation, anonymously, and resolves the code', async () => {
    document.cookie = 'XSRF-TOKEN=synthetic-xsrf';
    const fetch = mockFetch();

    const sent = sendDiagnostics(device);
    const call = await fetch.next('POST', '/api/diagnostics');
    expect(call.body).toEqual(request);
    expect(call.credentials).toBe('omit');
    expect(call.headers.get('Authorization')).toBeNull();
    expect(call.headers.get('X-XSRF-TOKEN')).toBeNull();
    call.respond(201, { ref: 'EMB-7K3M9QX2' });

    await expect(sent).resolves.toBe('EMB-7K3M9QX2');
    document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT';
  });

  it.each([
    [429, 'rateLimited'],
    [413, 'refused'],
    [400, 'refused'],
    [503, 'busy'],
    [500, 'failed'],
  ])('turns a %i into a %s failure', async (status, kind) => {
    const fetch = mockFetch();

    const sent = sendDiagnostics(device);
    (await fetch.next('POST', '/api/diagnostics')).respond(status, { type: 'problem' });

    await expect(sent).rejects.toEqual(new DiagnosticsError(kind as DiagnosticsError['kind']));
  });

  it.each([
    [{ clock: ['Every at within 7 days before and 1 hour after the server\'s time.'] }, 'clock'],
    [{ 'exits[0].frames': ['At most 40 Java or native stack frames.'] }, 'refused'],
  ])('turns a 400 with errors %j into a %s failure', async (errors, kind) => {
    const fetch = mockFetch();

    const sent = sendDiagnostics(device);
    (await fetch.next('POST', '/api/diagnostics')).respond(400, { type: 'validation', errors });

    await expect(sent).rejects.toEqual(new DiagnosticsError(kind as DiagnosticsError['kind']));
  });

  it('turns a network failure into an unreachable failure', async () => {
    const fetch = mockFetch();

    const sent = sendDiagnostics(device);
    (await fetch.next('POST', '/api/diagnostics')).fail();

    await expect(sent).rejects.toMatchObject({ kind: 'unreachable' });
  });
});
