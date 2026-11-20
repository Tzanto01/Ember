import { cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { env } from '@/lib/env';
import { applyGentleMotion, gentleMotion } from '@/lib/theme';
import { auth } from '@/lib/services';
import { me } from '@/test/test-support';
import { openSettings, resetSettings, setWide, stubSettingsMedia } from '@/test/settings-page';

vi.mock('@/lib/env', async (original) => ({ env: { ...(await original<typeof import('@/lib/env')>()).env } }));
const exactAlarms = vi.hoisted(() => ({ beta: false, allowed: false, allowExact: vi.fn() }));
const enablePhone = vi.hoisted(() => vi.fn());
vi.mock('@/lib/native/updater', async (original) => ({
  ...(await original<typeof import('@/lib/native/updater')>()),
  updaterAvailable: () => exactAlarms.beta,
}));
vi.mock('@/lib/services', async (original) => {
  const { Store } = await import('@/lib/store');
  return {
    ...(await original<typeof import('@/lib/services')>()),
    localMode: { mode: new Store('account'), evicted: new Store(false) },
    reminders: {
      exactAllowed: async () => exactAlarms.allowed,
      allowExact: (...args: unknown[]) => exactAlarms.allowExact(...args),
      enablePhone: () => enablePhone(),
    },
  };
});

const notificationPermission = vi.hoisted(() => ({ display: 'granted' }));
vi.mock('@capacitor/local-notifications', () => ({
  LocalNotifications: { checkPermissions: async () => ({ ...notificationPermission }) },
}));

describe('AppearanceCard', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  it('switches between light, dark and the system setting on this device', async () => {
    const user = userEvent.setup();
    await openSettings('/settings/appearance');

    expect(screen.getByRole('radio', { name: 'System' })).toBeChecked();
    await user.click(screen.getByRole('radio', { name: 'Dark' }));
    expect(document.documentElement).toHaveClass('dark');
    expect(localStorage.getItem('ember.colorScheme')).toBe('dark');

    await user.click(screen.getByRole('radio', { name: 'System' }));
    expect(document.documentElement).not.toHaveClass('dark');
    expect(localStorage.getItem('ember.colorScheme')).toBeNull();
  });

  it('turns calm mode on through PATCH /api/me from Appearance', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/appearance');
    const toggle = screen.getByRole('switch', { name: 'Calm mode' });
    expect(toggle).not.toBeChecked();
    expect(toggle).toHaveAccessibleDescription('No motion, softer colors and fewer decorations. Breathing shows text only.');

    await user.click(toggle);

    const sent = await fetch.next('PATCH', '/api/me');
    expect(sent.body).toEqual({ calmMode: true });
    sent.respond(200, { ...me, calmMode: true });
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect(toggle).toBeChecked();
  });

  it('still shows calm mode being saved after leaving Appearance and coming back', async () => {
    setWide(true);
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/appearance');

    await user.click(screen.getByRole('switch', { name: 'Calm mode' }));
    const sent = await fetch.next('PATCH', '/api/me');
    await user.click(screen.getByRole('link', { name: 'Habits & days' }));
    await user.click(screen.getByRole('link', { name: 'Appearance' }));

    expect(screen.getByRole('switch', { name: 'Calm mode' })).toBeChecked();
    sent.respond(200, { ...me, calmMode: true });
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
    expect(screen.getByRole('switch', { name: 'Calm mode' })).toBeChecked();
  });

  it('calms this device at once and takes it back when saving calm mode fails', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/appearance');
    const toggle = screen.getByRole('switch', { name: 'Calm mode' });

    await user.click(toggle);

    const sent = await fetch.next('PATCH', '/api/me');
    expect(document.documentElement).toHaveAttribute('data-calm');
    sent.respond(500, { type: 'internal', title: 'Server error', status: 500 });

    await waitFor(() => expect(toggle).not.toBeChecked());
    expect(document.documentElement).not.toHaveAttribute('data-calm');
  });

  it('in the app, "Gentle motion" is on by default and turns off on this device only; the web has no such switch', async () => {
    const user = userEvent.setup();
    await openSettings('/settings/appearance');
    expect(screen.queryByRole('switch', { name: 'Gentle motion' })).toBeNull();
    cleanup();

    env.native = true;
    const { fetch } = await openSettings('/settings/appearance');
    const toggle = screen.getByRole('switch', { name: 'Gentle motion' });
    expect(toggle).toBeChecked();
    expect(toggle).toHaveAccessibleDescription(/calm mode/);

    await user.click(toggle);

    expect(toggle).not.toBeChecked();
    expect(gentleMotion.get()).toBe(false);
    expect(localStorage.getItem('ember.gentleMotion')).toBe('0');
    expect(fetch.all.some((c) => c.method === 'PATCH')).toBe(false);
    applyGentleMotion(true);
  });

  it('applies a chosen theme at once and saves it to the account', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/appearance');

    await user.click(screen.getByRole('radio', { name: 'Fieldnotes' }));

    expect(document.documentElement.dataset['theme']).toBe('fieldnotes');
    expect(localStorage.getItem('ember.theme')).toBe('fieldnotes');
    const patch = await fetch.next('PATCH', '/api/me');
    expect(patch.body).toEqual({ theme: 'fieldnotes' });
    patch.respond(200, { ...me, theme: 'fieldnotes' });
    await waitFor(() => expect(auth.me.get()?.theme).toBe('fieldnotes'));
    expect(localStorage.getItem('ember.accountTheme')).toBe('fieldnotes');
    expect(screen.getByRole('radio', { name: 'Fieldnotes' })).toBeChecked();
  });

  it('keeps the chosen theme on this device and says so gently when saving it fails', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/appearance');

    await user.click(screen.getByRole('radio', { name: 'Tidepool' }));
    (await fetch.next('PATCH', '/api/me')).fail();

    const status = await screen.findByText(/couldn't save it to your account/);
    expect(status.closest('[role="status"]')).not.toBeNull();
    expect(document.documentElement.dataset['theme']).toBe('tidepool');
    expect(localStorage.getItem('ember.theme')).toBe('tidepool');
    expect(screen.getByRole('radio', { name: 'Tidepool' })).toBeChecked();
  });

  it('switches to German on picking Deutsch: saves it to the account, remembers it on the device and sets <html lang>', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/appearance');

    await user.click(screen.getByRole('radio', { name: 'Deutsch' }));

    const patch = await fetch.next('PATCH', '/api/me');
    expect(patch.body).toEqual({ language: 'de' });
    patch.respond(200, { ...me, language: 'de' });
    expect(await screen.findByRole('heading', { level: 1, name: 'Darstellung' })).toBeInTheDocument();
    expect(screen.getByText('Sprache')).toBeInTheDocument();
    expect(document.documentElement.lang).toBe('de');
    expect(localStorage.getItem('ember.language')).toBe('de');
    expect(screen.getByRole('radio', { name: 'Deutsch' })).toBeChecked();
  });

  it('goes back to English and says the language was not saved when saving it fails', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/appearance');

    await user.click(screen.getByRole('radio', { name: 'Deutsch' }));
    (await fetch.next('PATCH', '/api/me')).fail();

    const status = await screen.findByText("Your language couldn't be saved. Try again in a moment.");
    expect(status.closest('[role="status"]')).not.toBeNull();
    expect(screen.getByRole('radio', { name: 'English' })).toBeChecked();
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem('ember.language')).toBeNull();
  });
});
