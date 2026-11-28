import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { openSettings, resetSettings, stubSettingsMedia } from '@/test/settings-page';
import { me } from '@/test/test-support';

vi.mock('@/lib/services', async (original) => {
  const { Store } = await import('@/lib/store');
  return {
    ...(await original<typeof import('@/lib/services')>()),
    localMode: { mode: new Store('account'), evicted: new Store(false) },
  };
});

describe('LanguageCard', { timeout: 15_000 }, () => {
  beforeEach(() => {
    stubSettingsMedia();
  });

  afterEach(() => {
    resetSettings();
  });

  it('switches to German on picking Deutsch: saves it to the account, remembers it on the device and sets <html lang>', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/language');

    expect(screen.getByRole('radiogroup', { name: 'Language' })).toHaveAccessibleDescription(
      "Ember uses this language everywhere you're signed in, in emails and in Discord.",
    );
    await user.click(screen.getByRole('radio', { name: 'Deutsch' }));

    const patch = await fetch.next('PATCH', '/api/me');
    expect(patch.body).toEqual({ language: 'de' });
    patch.respond(200, { ...me, language: 'de' });
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Sprache & Zeit' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('radiogroup', { name: 'Sprache' })).toBeInTheDocument();
    expect(document.documentElement.lang).toBe('de');
    expect(localStorage.getItem('ember.language')).toBe('de');
    expect(screen.getByRole('radio', { name: 'Deutsch' })).toBeChecked();
  });

  it('goes back to English and says so in the language card when saving it fails', async () => {
    const user = userEvent.setup();
    const { fetch } = await openSettings('/settings/language');

    await user.click(screen.getByRole('radio', { name: 'Deutsch' }));
    (await fetch.next('PATCH', '/api/me')).fail();

    const status = await screen.findByText(
      "Your language couldn't be saved. Try again in a moment.",
    );
    expect(status.closest('[role="status"]')).not.toBeNull();
    expect(status.closest('section')).toHaveAccessibleName('Language');
    expect(screen.getByRole('radio', { name: 'English' })).toBeChecked();
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem('ember.language')).toBeNull();
  });
});
