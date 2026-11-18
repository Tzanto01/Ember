import { describe, expect, it } from 'vitest';
import { diskTone, formatAgo, formatDay, formatStamp } from './format';

describe('formatAgo', () => {
  const at = '2026-11-18T12:00:00Z';
  const t = Date.parse(at);

  it('counts seconds, then minutes, hours and days', () => {
    expect(formatAgo(at, t + 5_400)).toBe('5s ago');
    expect(formatAgo(at, t + 59_999)).toBe('59s ago');
    expect(formatAgo(at, t + 60_000)).toBe('1m ago');
    expect(formatAgo(at, t + 3 * 3600_000)).toBe('3h ago');
    expect(formatAgo(at, t + 3 * 86400_000)).toBe('3d ago');
  });

  it('never shows a negative age when the server clock is ahead', () => {
    expect(formatAgo(at, t - 4_000)).toBe('0s ago');
  });
});

describe('dates', () => {
  it('shows calendar dates without a time zone shift and timestamps in Berlin time', () => {
    expect(formatDay('2026-11-01')).toBe('1 Nov');
    expect(formatStamp('2026-11-18T23:30:00Z')).toContain('19 Nov 2026');
    expect(formatStamp('2026-07-01T10:00:00Z')).toContain('12:00');
  });
});

describe('diskTone', () => {
  it('warns below 10 % free and alarms below 5 %', () => {
    expect(diskTone(10, 100)).toBeNull();
    expect(diskTone(9, 100)).toBe('warn');
    expect(diskTone(5, 100)).toBe('warn');
    expect(diskTone(4, 100)).toBe('bad');
  });

  it('shows no tone for a disk without a size', () => {
    expect(diskTone(0, 0)).toBeNull();
  });
});
