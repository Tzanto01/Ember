import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DeviceDiagnostics } from '@/lib/native/diagnostics';
import { mockFetch } from '@/test/test-support';
import { renderPage } from '@/test/test-utils';
import { DiagnosticsSheet } from './diagnostics-sheet';

const collect = vi.hoisted(() => vi.fn<() => Promise<DeviceDiagnostics>>());
vi.mock('@/lib/native/diagnostics', async (original) => ({
  ...(await original<typeof import('@/lib/native/diagnostics')>()),
  collectDiagnostics: () => collect(),
}));

const device: DeviceDiagnostics = {
  installation: 'Zk3vQ8xLm2Ty7pWa9cHn4g',
  version: '0.10.0-beta.4',
  os: 'Android 14 (API 34)',
  device: 'Google Pixel 7',
  launches: [{ at: '2026-11-20T18:40:00Z', event: 'launch cold=true source=user' }],
  exits: [
    {
      at: '2026-11-20T18:39:00Z',
      reason: 'CRASH',
      signal: null,
      exception: ['java.lang.IllegalStateException'],
      frames: ['app.ember.habits.MainActivity.onCreate(MainActivity.kt:42)'],
    },
  ],
};

const preview = `{
  "ref": "EMB-…",
  "platform": "android",
  "version": "0.10.0-beta.4",
  "os": "Android 14 (API 34)",
  "device": "Google Pixel 7",
  "createdAt": "…",
  "userRef": null,
  "launches": [
    {
      "at": "2026-11-20T18:40:00Z",
      "event": "launch cold=true source=user"
    }
  ],
  "exits": [
    {
      "at": "2026-11-20T18:39:00Z",
      "reason": "CRASH",
      "signal": null,
      "exception": [
        "java.lang.IllegalStateException"
      ],
      "frames": [
        "app.ember.habits.MainActivity.onCreate(MainActivity.kt:42)"
      ]
    }
  ]
}`;

function open() {
  const fetch = mockFetch();
  renderPage('/', () => <DiagnosticsSheet open onClose={() => undefined} />, '/');
  return fetch;
}

describe('DiagnosticsSheet', () => {
  beforeEach(() => collect.mockResolvedValue(device));
  afterEach(() => {
    collect.mockReset();
    vi.unstubAllGlobals();
  });

  it('previews exactly the body it sends, then shows the code to copy and where to post it', async () => {
    const user = userEvent.setup();
    const fetch = open();

    const dialog = await screen.findByRole('dialog', { name: 'Send a tech report' });
    const report = await within(dialog).findByRole('region', { name: 'The report' });
    expect(report.textContent).toBe(preview);
    expect(report).toHaveAttribute('tabindex', '0');
    expect(dialog).toHaveTextContent('Ember adds the code and the time when you send it.');
    expect(dialog).toHaveTextContent("Ember keeps the report for 30 days after it reaches the team. To have it deleted sooner, send its code in Ember's Discord and ask.");
    expect(dialog).toHaveTextContent('The app also sends a random install number with it, only so Ember can limit uploads. Ember never stores it.');
    const send = within(dialog).getByRole('button', { name: 'Send' });
    expect(send).toHaveAccessibleDescription('Tapping Send shares exactly this report with the Ember team.');

    await user.click(send);
    const call = await fetch.next('POST', '/api/diagnostics');
    expect(call.body).toEqual({ ...device, platform: 'android' });
    call.respond(201, { ref: 'EMB-7K3M9QX2' });

    expect(await within(dialog).findByText('EMB-7K3M9QX2')).toBeInTheDocument();
    expect(dialog).toHaveTextContent("It's on its way and reaches the Ember team shortly.");
    expect(within(dialog).queryByRole('region', { name: 'The report' })).toBeNull();
    expect(dialog).toHaveTextContent("Post this code in Ember's Discord, with a few words on what happened.");
    await user.click(within(dialog).getByRole('button', { name: 'Copy code' }));
    expect(await navigator.clipboard.readText()).toBe('EMB-7K3M9QX2');
    expect(within(dialog).getByRole('status')).toHaveTextContent('Copied.');
  });

  it('keeps the report when Ember is rate limited and says to try again later', async () => {
    const user = userEvent.setup();
    const fetch = open();

    await user.click(await screen.findByRole('button', { name: 'Send' }));
    (await fetch.next('POST', '/api/diagnostics')).respond(429, { type: 'rate_limited' }, { 'Retry-After': '3600' });

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Ember has had a lot of reports in a short time. Please try again later, maybe tomorrow.',
    );
    expect(screen.getByRole('region', { name: 'The report' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send' })).toBeInTheDocument();
  });

  it('still offers to send a report with no app starts and no exits', async () => {
    collect.mockResolvedValue({ ...device, launches: [], exits: [] });
    open();

    expect(await screen.findByText('Ember noticed no app starts or crashes lately. You can still send it.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Send' })).toBeInTheDocument();
  });

  it('says so when the device cannot put the report together, and tries again', async () => {
    const user = userEvent.setup();
    collect.mockRejectedValueOnce(new Error('plugin missing'));
    open();

    await user.click(await screen.findByRole('button', { name: 'Try again' }));

    expect(await screen.findByRole('region', { name: 'The report' })).toBeInTheDocument();
    expect(collect).toHaveBeenCalledTimes(2);
  });
});
