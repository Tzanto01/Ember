import { existsSync, readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { de, en } from '@/lib/i18n';
import { fingerprint, fingerprintsFile, repoRoot } from './fingerprint';
import { guideFor, guides } from './registry';

const english = en as Record<string, string>;
const german = de as Record<string, string>;
const entries = Object.values(guides).flatMap((guide) => guide.entries);
const placeholders = (text: string) => [...text.matchAll(/\{\{\s*(\w+)/g)].map((m) => m[1]!).sort();

describe('the guide registry', () => {
  it('gives every screen a name, a lede and 3 to 5 questions (Today and Settings 6) with unique ids', () => {
    for (const [screen, guide] of Object.entries(guides)) {
      expect(english[guide.nameKey]).toBeTruthy();
      expect(english[guide.ledeKey]).toBeTruthy();
      expect(guide.entries.length, screen).toBeGreaterThanOrEqual(3);
      expect(guide.entries.length, screen).toBeLessThanOrEqual(screen === 'settings' || screen === 'today' ? 6 : 5);
    }
    expect(new Set(entries.map((entry) => entry.id)).size).toBe(entries.length);
  });

  it('has every question and answer in English and German', () => {
    const missing = entries
      .flatMap((entry) => [entry.questionKey, entry.answerKey])
      .filter((key) => !english[key]?.trim() || !german[key]?.trim());
    expect(missing).toEqual([]);
  });

  it('takes every number in an answer from the values the code passes, in both languages', () => {
    const mismatched = entries.filter((entry) => {
      const given = Object.keys(entry.values ?? {}).sort().join();
      return placeholders(english[entry.answerKey]!).join() !== given || placeholders(german[entry.answerKey]!).join() !== given;
    });
    expect(mismatched.map((entry) => entry.id)).toEqual([]);
    const literal = (text: string) => /\d/.test(text.replace(/\{\{\w+\}\}|1\/2/g, ''));
    expect(entries.filter((e) => literal(english[e.answerKey]!) || literal(german[e.answerKey]!)).map((e) => e.id)).toEqual([]);
  });

  it('points every entry at 1 to 3 repository files that exist', () => {
    const broken = entries.filter(
      (entry) =>
        entry.covers.length < 1 || entry.covers.length > 3 || entry.covers.some((path) => !existsSync(repoRoot + path)),
    );
    expect(broken.map((entry) => `${entry.id}: ${entry.covers.join(', ')}`)).toEqual([]);
  });

  it('still matches the files each answer describes', () => {
    const stamps = JSON.parse(readFileSync(fingerprintsFile, 'utf8')) as Record<string, string>;
    const stale = entries.filter((entry) => stamps[entry.id] !== fingerprint(entry.covers));
    expect(
      stale.map((entry) => entry.id),
      stale
        .map(
          (entry) =>
            `Guide entry "${entry.id}" describes ${entry.covers.join(', ')}, which changed. Re-read its answer ` +
            `(${entry.answerKey} in en.json and de.json) against the change, fix the text if needed, then run ` +
            `\`bun run guide:stamp ${entry.id}\`. Stamp only the entries you re-read.`,
        )
        .join('\n'),
    ).toEqual([]);
    expect(Object.keys(stamps).sort()).toEqual(entries.map((entry) => entry.id).sort());
  });

  it('gives a guide to the app screens it explains and none elsewhere', () => {
    expect(guideFor('/today')).toBe(guides.today);
    expect(guideFor('/history/7')).toBe(guides.history);
    expect(guideFor('/habits/new')).toBe(guides.habit);
    expect(guideFor('/habits/7/edit')).toBe(guides.habit);
    expect(guideFor('/settings')).toBe(guides.settings);
    expect(guideFor('/settings/account')).toBe(guides.settings);
    expect(guideFor('/settings/privacy')).toBe(guides.privacy);
    expect(guideFor('/settings/language')).toBe(guides.languageTime);
    expect(guideFor('/settings/journal')).toBe(guides.journal);
    expect(guideFor('/settings/support')).toBe(guides.settings);
    for (const path of ['/history/year', '/routines/new', '/settings/help', '/settings/feedback', '/feedback', '/help', '/moments', '/moment']) {
      expect(guideFor(path), path).toBeNull();
    }
  });
});
