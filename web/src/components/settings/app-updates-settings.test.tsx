import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BetaUpdateBanner } from '@/components/beta-update';
import { betaUpdate } from '@/lib/native/updater';
import { mockFetch } from '@/test/test-support';
import { AppUpdatesSettings } from './app-updates-settings';

const native = vi.hoisted(() => ({
  resume: null as (() => void) | null,
  progress: null as ((progress: { received: number; total: number }) => void) | null,
  updater: {
    canInstall: vi.fn(),
    openInstallSettings: vi.fn(),
    downloadAndInstall: vi.fn(),
    addListener: vi.fn(),
  },
}));

vi.mock('@capacitor/core', () => ({
  Capacitor: { isPluginAvailable: (name: string) => name === 'EmberUpdater' },
  registerPlugin: () => native.updater,
}));

vi.mock('@capacitor/app', () => ({
  App: {
    getInfo: async () => ({ build: '3', version: '0.9.0-beta.2' }),
    addListener: async (_event: string, listener: () => void) => {
      native.resume = listener;
      return { remove: async () => undefined };
    },
  },
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

function settingsCard() {
  return within(screen.getByTestId('settings'));
}

describe('AppUpdatesSettings', () => {
  beforeEach(() => {
    native.resume = null;
    betaUpdate.set({ update: null, dismissed: 0, phase: 'offer', percent: null });
    native.updater.canInstall.mockResolvedValue({ value: true });
    native.updater.openInstallSettings.mockResolvedValue(undefined);
    native.updater.downloadAndInstall.mockResolvedValue(undefined);
    native.updater.addListener.mockImplementation(async (_event, listener) => {
      native.progress = listener;
      return { remove: async () => undefined };
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  async function renderBoth(firstManifest: unknown = { ...manifest, versionCode: 3 }) {
    const f = mockFetch();
    render(
      <>
        <BetaUpdateBanner />
        <div data-testid="settings">
          <AppUpdatesSettings />
        </div>
      </>,
    );
    expect(await screen.findByText('Installed: Ember Beta 0.9.0-beta.2')).toBeInTheDocument();
    await act(async () => (await f.next('GET', manifestUrl)).respond(200, firstManifest));
    return f;
  }

  async function checkWith(f: Awaited<ReturnType<typeof renderBoth>>, respond: (call: { respond: (status: number, body?: unknown) => void; fail: () => void }) => void) {
    await userEvent.click(screen.getByRole('button', { name: 'Check for updates' }));
    expect(screen.getByRole('button', { name: 'Checking…' })).toHaveAttribute('aria-disabled', 'true');
    await act(async () => respond(await f.next('GET', manifestUrl)));
  }

  it('brings a dismissed offer back and installs from Settings, showing the same progress in both places', async () => {
    let finish: () => void = () => undefined;
    native.updater.downloadAndInstall.mockImplementationOnce(() => new Promise<void>((resolve) => (finish = resolve)));
    const f = await renderBoth(manifest);
    await userEvent.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByRole('status', { name: 'App update' })).toBeNull();

    await checkWith(f, (call) => call.respond(200, manifest));
    expect(screen.getByRole('status', { name: 'App update' })).toHaveTextContent('Ember Beta 0.9.0-beta.3 is available.');
    expect(settingsCard().getByText('Ember Beta 0.9.0-beta.3 is available.')).toBeInTheDocument();

    await userEvent.click(settingsCard().getByRole('button', { name: 'Update now' }));
    await act(async () => native.progress?.({ received: 45, total: 100 }));

    expect(settingsCard().getByText('Downloading Ember Beta 0.9.0-beta.3…')).toBeInTheDocument();
    expect(settingsCard().getByRole('progressbar', { name: 'Download progress' })).toHaveAttribute('value', '45');
    expect(settingsCard().getByText('45%')).toBeInTheDocument();
    expect(screen.getAllByRole('progressbar')).toHaveLength(2);
    expect(screen.queryByRole('button', { name: 'Update now' })).toBeNull();
    expect(native.updater.downloadAndInstall).toHaveBeenCalledTimes(1);
    await act(async () => finish());
  });

  it('shows the permission step and a failure with Try again where the install started', async () => {
    native.updater.canInstall.mockResolvedValueOnce({ value: false });
    native.updater.downloadAndInstall.mockRejectedValueOnce(new Error('MISMATCH'));
    const f = await renderBoth();
    await checkWith(f, (call) => call.respond(200, manifest));

    await userEvent.click(settingsCard().getByRole('button', { name: 'Update now' }));
    expect(native.updater.openInstallSettings).toHaveBeenCalledTimes(1);
    expect(settingsCard().getByText('Allow Ember to install updates, then come back.')).toBeInTheDocument();

    await act(async () => native.resume?.());
    expect(
      await settingsCard().findByText("The update couldn't be downloaded. Try again in a moment."),
    ).toBeInTheDocument();
    await userEvent.click(settingsCard().getByRole('button', { name: 'Try again' }));
    expect(native.updater.downloadAndInstall).toHaveBeenCalledTimes(2);
  });

  it('says when the installed version is the latest', async () => {
    const f = await renderBoth();
    await checkWith(f, (call) => call.respond(200, { ...manifest, versionCode: 3 }));

    expect(screen.getByText('You have the latest version, Ember Beta 0.9.0-beta.2.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Update now' })).toBeNull();
  });

  it("says when it couldn't check", async () => {
    const f = await renderBoth();
    await checkWith(f, (call) => call.fail());

    expect(screen.getByText("Couldn't check for updates. Check your connection and try again.")).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Check for updates' })).not.toHaveAttribute('aria-disabled');
  });
});
