import { Capacitor, type PluginListenerHandle, registerPlugin } from '@capacitor/core';
import { env } from '../env';
import { type LocalizedNotes, loadReleaseNotes } from '../release-notes';
import { Store } from '../store';

/** A newer sideloaded beta build, from `latest.json` next to the APK. */
export interface BetaUpdate {
  versionCode: number;
  versionName: string;
  url: string;
  sha256: string;
  size?: number;
}

/** The custom native plugin (`EmberUpdater`, beta Android build only). */
export interface EmberUpdaterPlugin {
  canInstall(): Promise<{ value: boolean }>;
  openInstallSettings(): Promise<void>;
  /** Downloads, checks SHA-256 and size natively, then opens Android's installer (which asks the user). */
  downloadAndInstall(options: { url: string; sha256: string; size?: number }): Promise<void>;
  addListener(
    event: 'downloadProgress',
    listener: (progress: { received: number; total: number }) => void,
  ): Promise<PluginListenerHandle>;
}

export const EmberUpdater = registerPlugin<EmberUpdaterPlugin>('EmberUpdater');

/** True only in the sideloaded beta app: native with the `EmberUpdater` plugin (never on the web or from Play). */
export const updaterAvailable = (): boolean =>
  env.native && Capacitor.isPluginAvailable('EmberUpdater');

const positiveInt = (value: unknown): value is number =>
  Number.isSafeInteger(value) && (value as number) > 0;

/**
 * The update `body` describes when it is a valid manifest, its `url` is https on `origin` and its `versionCode` is
 * above `installed`; otherwise null.
 */
export function parseManifest(body: unknown, origin: string, installed: number): BetaUpdate | null {
  if (!body || typeof body !== 'object') return null;
  const { versionCode, versionName, url, sha256, size } = body as Record<string, unknown>;
  if (!positiveInt(versionCode) || versionCode <= installed) return null;
  if (typeof versionName !== 'string' || !versionName.trim()) return null;
  if (typeof sha256 !== 'string' || !/^[0-9a-f]{64}$/i.test(sha256)) return null;
  if (size !== undefined && size !== null && !positiveInt(size)) return null;
  if (typeof url !== 'string') return null;
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  if (parsed.protocol !== 'https:' || parsed.origin !== origin) return null;
  return { versionCode, versionName, url, sha256, ...(positiveInt(size) ? { size } : {}) };
}

/** What a manifest check found: a newer build, the installed build (`versionName`) is the latest, or no answer. */
export type UpdateCheck =
  | { status: 'update'; update: BetaUpdate }
  | { status: 'current'; versionName: string }
  | { status: 'error' };

/**
 * Reads `{apiBase}/download/latest.json` and compares it with the installed build. `error` outside the beta app and
 * whenever the manifest is unreachable, broken or invalid.
 */
export async function checkForUpdate(): Promise<UpdateCheck> {
  if (!updaterAvailable()) return { status: 'error' };
  try {
    const origin = new URL(env.apiBase).origin;
    const { App } = await import('@capacitor/app');
    const info = await App.getInfo();
    const installed = Number(info.build);
    if (!Number.isSafeInteger(installed)) return { status: 'error' };
    const response = await fetch(`${env.apiBase}/download/latest.json`, { cache: 'no-store' });
    if (!response.ok) return { status: 'error' };
    const latest = parseManifest(await response.json(), origin, 0);
    if (!latest) return { status: 'error' };
    return latest.versionCode > installed
      ? { status: 'update', update: latest }
      : { status: 'current', versionName: info.version };
  } catch {
    return { status: 'error' };
  }
}

/**
 * Reads `{apiBase}/download/latest.json` and returns a build newer than the installed one. Null outside the beta
 * app and whenever the manifest is missing, broken or invalid.
 */
export async function findUpdate(): Promise<BetaUpdate | null> {
  const result = await checkForUpdate();
  return result.status === 'update' ? result.update : null;
}

/** The notes for `versionName` from the server's `{apiBase}/release-notes.json`; null when they are missing or unreachable. */
export async function findReleaseNotes(versionName: string): Promise<LocalizedNotes | null> {
  try {
    const entries = await loadReleaseNotes(`${env.apiBase}/release-notes.json`);
    return entries.find((entry) => entry.version === versionName)?.notes ?? null;
  } catch {
    return null;
  }
}

/** Where the offered update's install is: offered, waiting for the install permission, downloading, or failed. */
export type BetaInstallPhase = 'offer' | 'permission' | 'downloading' | 'failed';

/** The offered beta update and its install, shared by the update banner and Settings. */
export interface BetaUpdateState {
  update: BetaUpdate | null;
  /** The banner hides offers up to this versionCode. */
  dismissed: number;
  phase: BetaInstallPhase;
  /** Download progress, null while unknown. */
  percent: number | null;
}

export const betaUpdate = new Store<BetaUpdateState>({ update: null, dismissed: 0, phase: 'offer', percent: null });

let installing = false;

/** Offers `update` (keeping its install phase when it is already offered); `undismiss` shows it in the banner again. Ignored while an install runs. */
export function offerBetaUpdate(update: BetaUpdate, undismiss = false): void {
  if (installing) return;
  betaUpdate.update((state) => ({
    update,
    dismissed: undismiss ? 0 : state.dismissed,
    phase: state.update?.versionCode === update.versionCode ? state.phase : 'offer',
    percent: null,
  }));
}

function showPhase(phase: BetaInstallPhase): void {
  betaUpdate.update((state) => ({ ...state, phase, percent: null }));
}

/**
 * Installs the offered update: asks for the "Install unknown apps" permission first when needed, then downloads it
 * (checked natively) and opens Android's installer. Does nothing while an install already runs.
 */
export async function installBetaUpdate(): Promise<void> {
  const target = betaUpdate.get().update;
  if (!target || installing) return;
  installing = true;
  try {
    if (!(await EmberUpdater.canInstall()).value) {
      showPhase('permission');
      await EmberUpdater.openInstallSettings();
      return;
    }
    showPhase('downloading');
    const progress = await EmberUpdater.addListener('downloadProgress', ({ received, total }) => {
      if (total > 0)
        betaUpdate.update((state) => ({ ...state, percent: Math.min(100, Math.floor((received * 100) / total)) }));
    });
    try {
      await EmberUpdater.downloadAndInstall({ url: target.url, sha256: target.sha256, size: target.size });
      showPhase('offer');
    } finally {
      void progress.remove();
    }
  } catch {
    showPhase('failed');
  } finally {
    installing = false;
  }
}

/** Continues an install that waited for the install permission, once the app is back and has it. */
export async function resumeBetaInstall(): Promise<void> {
  if (
    betaUpdate.get().phase === 'permission' &&
    (await EmberUpdater.canInstall().catch(() => ({ value: false }))).value
  ) {
    await installBetaUpdate();
  }
}
