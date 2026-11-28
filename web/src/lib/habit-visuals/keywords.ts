import type { HabitPicture } from '../models';

const keywords: readonly (readonly [HabitPicture, readonly string[]])[] = [
  ['bottle', ['water', 'drink', 'hydrate', 'wasser', 'trinken']],
  ['pills', ['pill', 'tablet', 'medication', 'meds', 'vitamin', 'tablette', 'medikament']],
  ['shelf', ['read', 'book', 'lesen', 'buch']],
  ['trail', ['walk', 'run', 'steps', 'hike', 'spazieren', 'laufen', 'gehen']],
];

function matches(word: string, keyword: string): boolean {
  return keyword.length <= 4 ? word.startsWith(keyword) : word.includes(keyword);
}

/**
 * The picture a habit name suggests, on device and in English and German, or null. A keyword of four letters or fewer
 * matches the start of a word ("run" in "running", not "brunch"), a longer one anywhere in a word ("spazierengehen").
 * Bottle, pills, shelf and trail are checked in that order; kindling is never suggested.
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
