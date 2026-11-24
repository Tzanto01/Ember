import { describe, expect, it } from 'vitest';
import { safeReturnUrl, skipsConsent } from './return-url';

describe('safeReturnUrl', () => {
  it.each(['/today', '/history?x=1'])('keeps the local path %s', (url) => {
    expect(safeReturnUrl(url)).toBe(url);
  });

  it.each([
    '//evil.com',
    '/\\evil.com',
    '/\t/evil.com',
    '/\n/evil.com',
    'https://evil.com',
    'javascript:alert(1)',
    'https:/delete-account',
    '//evil.com/delete-account',
    ' /delete-account',
    'today',
    '',
    null,
    undefined,
  ])('falls back for %s', (url) => {
    expect(safeReturnUrl(url)).toBe('/today');
  });

  it('uses the given fallback', () => {
    expect(safeReturnUrl('//evil.com', '/home')).toBe('/home');
  });
});

describe('skipsConsent', () => {
  it('holds only for the account-deletion page', () => {
    expect(skipsConsent('/delete-account')).toBe(true);
    expect(skipsConsent('/delete-account?from=play')).toBe(true);
    for (const url of ['/today', '/delete-account/x', '/delete-accounts', '/today?returnUrl=/delete-account']) {
      expect(skipsConsent(url), url).toBe(false);
    }
  });
});
