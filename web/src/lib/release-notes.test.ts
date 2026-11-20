import { afterEach, describe, expect, it, vi } from 'vitest';
import { markVersionSeen, notesIn, parseReleaseNotes, readSeenVersion } from './release-notes';

const entry = { version: '0.10.0', date: '2026-11-01', notes: { en: ['Calmer Today'] } };

describe('parseReleaseNotes', () => {
  it('keeps the entries in order, with German notes only where they are given', () => {
    const body = [
      { ...entry, notes: { en: ['A'], de: ['B'] }, extra: 1 },
      { ...entry, version: '0.9.0' },
    ];

    expect(parseReleaseNotes(body)).toEqual([
      { version: '0.10.0', date: '2026-11-01', notes: { en: ['A'], de: ['B'] } },
      { version: '0.9.0', date: '2026-11-01', notes: { en: ['Calmer Today'] } },
    ]);
  });

  it.each([
    ['a non-list', {}],
    ['an empty list', []],
    ['an entry that is not an object', [entry, 'x']],
    ['a blank version', [{ ...entry, version: '  ' }]],
    ['a date in another format', [{ ...entry, date: '01.11.2026' }]],
    ['no notes', [{ version: '1', date: '2026-11-01' }]],
    ['empty English notes', [{ ...entry, notes: { en: [] } }]],
    ['a blank English note', [{ ...entry, notes: { en: ['A', ' '] } }]],
    ['German notes that are not a list', [{ ...entry, notes: { en: ['A'], de: 'B' } }]],
  ])('rejects the whole file for %s', (_name, body) => {
    expect(parseReleaseNotes(body)).toBeNull();
  });
});

describe('notesIn', () => {
  it('falls back to English when the language has no notes', () => {
    expect(notesIn({ en: ['A'], de: ['B'] }, 'de')).toEqual(['B']);
    expect(notesIn({ en: ['A'] }, 'de')).toEqual(['A']);
  });
});

describe('the seen version', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.clear();
  });

  it('is null until a version is marked, then holds the last one', () => {
    expect(readSeenVersion()).toBeNull();

    markVersionSeen('0.9.0');
    markVersionSeen('0.10.0');

    expect(readSeenVersion()).toBe('0.10.0');
  });

  it('reads as undefined and ignores marking when storage fails', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('denied');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('denied');
    });

    expect(readSeenVersion()).toBeUndefined();
    expect(() => markVersionSeen('1')).not.toThrow();
  });
});
