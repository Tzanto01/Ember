import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { localMode } from '@/lib/services';
import { timerSound } from '@/lib/timer/timer-sound';
import { openSettings, resetSettings, stubSettingsMedia } from '@/test/settings-page';

vi.mock('@/lib/services', async (original) => {
  const { Store } = await import('@/lib/store');
  return {
    ...(await original<typeof import('@/lib/services')>()),
    localMode: { mode: new Store('account'), evicted: new Store(false) },
  };
});

describe('TimerRow', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    timerSound.set(false);
    resetSettings();
  });

  it.each(['account', 'local'] as const)('turns the timer sound on for this device, off by default (%s)', async (mode) => {
    localMode.mode.set(mode);
    const user = userEvent.setup();
    await openSettings('/settings/reminders');
    const sound = screen.getByRole('switch', { name: 'Sound when a timer ends' });
    expect(sound).not.toBeChecked();
    expect(sound).toHaveAccessibleDescription(/silent in calm mode.*This device/);

    await user.click(sound);

    expect(sound).toBeChecked();
    expect(localStorage.getItem('ember.timerSound')).toBe('1');
  });
});
