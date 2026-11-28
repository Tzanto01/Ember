import type { HabitPicture } from '../models';

const keywords: readonly (readonly [HabitPicture, readonly string[]])[] = [
  ['bottle', ['water', 'drink', 'hydrate', 'wasser', 'trinken']],
  ['pills', ['pill', 'tablet', 'medication', 'meds', 'vitamin', 'tablette', 'medikament']],
  ['shelf', ['read', 'book', 'lesen', 'buch']],
  ['trail', ['walk', 'run', 'steps', 'hike', 'spazieren', 'laufen', 'gehen']],
];

const endings = ['s', 'es', 'ed', 'er', 'ers', 'ing'];

function inflects(word: string, keyword: string): boolean {
  if (word === keyword) return true;
  const stems = [keyword, keyword + keyword.slice(-1)];
  if (keyword.endsWith('e')) stems.push(keyword.slice(0, -1));
  return endings.some((ending) =>
    stems.some((stem, i) => (i === 0 || /^[ei]/.test(ending)) && word === stem + ending),
  );
}

function matches(word: string, keyword: string): boolean {
  return keyword.length <= 4 ? inflects(word, keyword) : word.includes(keyword);
}

/**
 * The picture a habit name suggests, on device and in English and German, or null. A keyword of four letters or fewer
 * matches a whole word, alone or with an ending (s, es, ed, er, ers, ing), also after a doubled last letter
 * ("running") or a dropped final e ("hiking"), so "ready" and "pillow" don't match; a longer one matches anywhere in
 * a word ("spazierengehen"). Bottle, pills, shelf and trail are checked in that order; kindling is never suggested.
 */
export function matchPicture(name: string): HabitPicture | null {
  const words = name
    .toLowerCase()
    .split(/[^\p{L}]+/u)
    .filter(Boolean);
  for (const [picture, list] of keywords) {
    if (list.some((keyword) => words.some((word) => matches(word, keyword)))) return picture;
  }
  return null;
}
