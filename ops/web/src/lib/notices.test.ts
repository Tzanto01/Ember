import { describe, expect, it } from 'vitest';
import { codePoints, noticeId, toUtc } from './notices';

describe('noticeId', () => {
  const now = new Date(2026, 10, 8, 23, 30);

  it('uses the local date and a trimmed ascii slug of at most 32 chars', () => {
    expect(noticeId('  Wartung: Server-Update am Abende, kurz!  ', now, [])).toBe('20261108-wartung-server-update-am-abende');
    expect(noticeId('Ünïcödé 😀', now, [])).toBe('20261108-n-c-d');
  });

  it('falls back to notice when nothing ascii is left', () => {
    expect(noticeId('😀 !!', now, [])).toBe('20261108-notice');
  });

  it('appends the next free suffix when the id is taken', () => {
    expect(noticeId('Down', now, ['20261108-down'])).toBe('20261108-down-2');
    expect(noticeId('Down', now, ['20261108-down', '20261108-down-2'])).toBe('20261108-down-3');
  });
});

describe('toUtc', () => {
  it('turns local time into UTC seconds ending in Z without milliseconds', () => {
    const out = toUtc('2026-11-18T19:30');
    expect(out).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:00Z$/);
    expect(Date.parse(out!)).toBe(new Date(2026, 10, 18, 19, 30).getTime());
  });

  it('gives undefined for an empty field', () => {
    expect(toUtc('')).toBeUndefined();
  });
});

describe('codePoints', () => {
  it('counts an emoji as one, not as two UTF-16 units', () => {
    expect(codePoints('hi 😀')).toBe(4);
  });
});
