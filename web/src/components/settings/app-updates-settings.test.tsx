import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { BetaUpdateBanner } from '@/components/beta-update';
import { mockFetch } from '@/lib/test-support';
import { AppUpdatesSettings } from './app-updates-settings';

const updater = vi.hoisted(() => ({
  canInstall: vi.fn(),
  openInstallSettings: vi.fn(),
  downloadAndInstall: vi.fn(),
  addListener: vi.fn(),
}));

vi.mock('@capacitor/core', () => ({
  Capacitor: { isPluginAvailable: (name: string) => name === 'EmberUpdater' },
  registerPlugin: () => updater,
}));

vi.mock('@capacitor/app', () => ({
  App: {
    getInfo: async () => ({ build: '3', version: '0.9.0-beta.2' }),
    addListener: async () => ({ remove: async () => undefined }),
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

describe('AppUpdatesSettings', () => {
  beforeEach(() => {
    updater.canInstall.mockResolvedValue({ value: true });
    updater.downloadAndInstall.mockResolvedValue(undefined);
    updater.addListener.mockResolvedValue({ remove: async () => undefined });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  async function renderBoth() {
    const f = mockFetch();
    render(
      <>
        <BetaUpdateBanner />
        <AppUpdatesSettings />
      </>,
    );
    expect(await screen.findByText('Installed: Ember Beta 0.9.0-beta.2')).toBeInTheDocument();
    return f;
  }

  it('brings a dismissed offer back and moves focus to its Update button', async () => {
    const user = userEvent.setup();
    const f = await renderBoth();
    await act(async () => (await f.next('GET', manifestUrl)).respond(200, manifest));
    await user.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByRole('status', { name: 'App update' })).toBeNull();

    await user.click(screen.getByRole('button', { name: 'Check for updates' }));
    expect(screen.getByRole('button', { name: 'Checking…' })).toHaveAttribute('aria-disabled', 'true');
    await act(async () => (await f.next('GET', manifestUrl)).respond(200, manifest));

    expect(screen.getByRole('status', { name: 'App update' })).toHaveTextContent(
      'Update available: Ember Beta 0.9.0-beta.3',
    );
    expect(screen.getByText('Ember Beta 0.9.0-beta.3 is available.')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Show update' }));
    expect(screen.getByRole('button', { name: 'Update' })).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(updater.downloadAndInstall).toHaveBeenCalledTimes(1);
  });

  it('says when the installed version is the latest', async () => {
    const user = userEvent.setup();
    const f = await renderBoth();
    await act(async () => (await f.next('GET', manifestUrl)).respond(200, { ...manifest, versionCode: 3 }));

    await user.click(screen.getByRole('button', { name: 'Check for updates' }));
    await act(async () => (await f.next('GET', manifestUrl)).respond(200, { ...manifest, versionCode: 3 }));

    expect(screen.getByText('You have the latest version, Ember Beta 0.9.0-beta.2.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Show update' })).toBeNull();
  });

  it("says when it couldn't check", async () => {
    const user = userEvent.setup();
    const f = await renderBoth();
    await act(async () => (await f.next('GET', manifestUrl)).respond(200, { ...manifest, versionCode: 3 }));

    await user.click(screen.getByRole('button', { name: 'Check for updates' }));
    await act(async () => (await f.next('GET', manifestUrl)).fail());

    expect(
      screen.getByText("Couldn't check for updates. Check your connection and try again."),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Check for updates' })).not.toHaveAttribute('aria-disabled');
  });
});
