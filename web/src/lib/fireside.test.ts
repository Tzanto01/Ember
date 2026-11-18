import { describe, expect, it } from 'vitest';
import { type Fireside, firesideAvailable, formatCode, isAsking, normalizeCode } from './fireside';
import { me } from './test-support';

const base: Fireside = { enabled: true, name: 'Nia', notify: false, askingUntil: null, code: null, friends: [], warmth: [] };

describe('fireside helpers', () => {
  it('shows a typed code as upper-case XXXX XXXX and sends it without spaces', () => {
    expect(formatCode('k7m2q9xa')).toBe('K7M2 Q9XA');
    expect(normalizeCode(' k7m2 q9xa ')).toBe('K7M2Q9XA');
  });

  it('is available only with an account and current consent', () => {
    expect(firesideAvailable(me, 'account')).toBe(true);
    expect(firesideAvailable(me, 'local')).toBe(false);
    expect(firesideAvailable(null, 'account')).toBe(false);
    expect(firesideAvailable({ ...me, consent: { given: true, policyVersion: '1', currentPolicyVersion: '2' } }, 'account')).toBe(false);
  });

  it('asks only until askingUntil', () => {
    const now = Date.parse('2026-11-18T12:00:00Z');
    expect(isAsking({ ...base, askingUntil: '2026-11-18T23:00:00Z' }, now)).toBe(true);
    expect(isAsking({ ...base, askingUntil: '2026-11-18T11:00:00Z' }, now)).toBe(false);
    expect(isAsking(base, now)).toBe(false);
  });
});
