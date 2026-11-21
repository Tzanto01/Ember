import { Capacitor, registerPlugin } from '@capacitor/core';
import { env } from '../env';
import { requestTimeoutMs } from '../http';
import { toApiError } from '../problem';

/** One app start or lifecycle event, newest first; `event` uses Ember's fixed launch vocabulary. */
export interface DiagnosticsLaunch {
  at: string;
  event: string;
}

/** One recent exit of the app as Android recorded it: the reason and, for crashes and ANRs, frame names only. */
export interface DiagnosticsExit {
  at: string;
  reason: string;
  signal: string | null;
  exception: string[];
  frames: string[];
}

/** What `EmberDiagnostics.collect()` returns: this install's random id (rate limiting only) and the report. */
export interface DeviceDiagnostics {
  installation: string;
  version: string | null;
  os: string;
  device: string | null;
  launches: DiagnosticsLaunch[];
  exits: DiagnosticsExit[];
}

/** The report as Ember stores and forwards it, in its fixed property order. Never linked to an account. */
export interface DiagnosticsBody {
  ref: string;
  platform: 'android';
  version: string | null;
  os: string;
  device: string | null;
  createdAt: string;
  userRef: null;
  launches: DiagnosticsLaunch[];
  exits: DiagnosticsExit[];
}

/**
 * Why sending a report failed: 429, a 400 `clock` (a time outside the server's window, so the phone's clock is off),
 * any other 400 or 413, 503, no connection, anything else.
 */
export type DiagnosticsFailure = 'rateLimited' | 'clock' | 'refused' | 'busy' | 'unreachable' | 'failed';

/** A failed {@link sendDiagnostics}, with its {@link DiagnosticsFailure} kind. */
export class DiagnosticsError extends Error {
  constructor(readonly kind: DiagnosticsFailure) {
    super(`Diagnostics not sent: ${kind}`);
    this.name = 'DiagnosticsError';
  }
}

/** The custom native plugin (`EmberDiagnostics`, Android only). */
export interface EmberDiagnosticsPlugin {
  collect(): Promise<DeviceDiagnostics>;
}

const EmberDiagnostics = registerPlugin<EmberDiagnosticsPlugin>('EmberDiagnostics');

/** How many days of app starts and exits a report covers. */
export const diagnosticsWindowDays = 7;

/** How many days Ember keeps a sent report before it is deleted. */
export const diagnosticsKeepDays = 30;

const refPattern = /^EMB-[0-9A-HJKMNP-TV-Z]{8}$/;

/** True only in the Android app, where the `EmberDiagnostics` plugin can read the report. */
export const diagnosticsAvailable = (): boolean =>
  env.native && Capacitor.getPlatform() === 'android';

/** Reads this device's report from the native plugin. Hold the result in memory only. */
export function collectDiagnostics(): Promise<DeviceDiagnostics> {
  return EmberDiagnostics.collect();
}

/**
 * The report body for `device`, exactly as the server will store and forward it once it adds `ref` and `createdAt`.
 * The installation id is left out; `userRef` is always null.
 */
export function diagnosticsBody(
  device: DeviceDiagnostics,
  { ref, createdAt }: { ref: string; createdAt: string },
): DiagnosticsBody {
  return {
    ref,
    platform: 'android',
    version: device.version,
    os: device.os,
    device: device.device,
    createdAt,
    userRef: null,
    launches: device.launches.map(({ at, event }) => ({ at, event })),
    exits: device.exits.map(({ at, reason, signal, exception, frames }) => ({
      at,
      reason,
      signal,
      exception,
      frames,
    })),
  };
}

function failureOf(status: number, body: unknown): DiagnosticsFailure {
  if (status === 429) return 'rateLimited';
  if (status === 400 && toApiError(status, body).problem?.errors?.['clock']) return 'clock';
  if (status === 400 || status === 413) return 'refused';
  if (status === 503) return 'busy';
  return 'failed';
}

/**
 * Sends `device` to `POST /api/diagnostics` anonymously (no cookies, no bearer token, no XSRF token) and resolves the
 * report's code (`EMB-…`). Rejects with a {@link DiagnosticsError}.
 */
export async function sendDiagnostics(device: DeviceDiagnostics): Promise<string> {
  const { installation, version, os, launches, exits } = device;
  let response: Response;
  try {
    response = await fetch(`${env.apiBase}/api/diagnostics`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        installation,
        platform: 'android',
        version,
        os,
        device: device.device,
        launches,
        exits,
      }),
      credentials: 'omit',
      signal: AbortSignal.timeout(requestTimeoutMs),
    });
  } catch {
    throw new DiagnosticsError('unreachable');
  }
  const answer: unknown = await response.json().catch(() => null);
  if (response.status !== 201) throw new DiagnosticsError(failureOf(response.status, answer));
  const { ref } = (answer ?? {}) as { ref?: unknown };
  if (typeof ref !== 'string' || !refPattern.test(ref)) throw new DiagnosticsError('failed');
  return ref;
}
