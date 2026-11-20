import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { currentLegalStamps, type LegalStamp, legalStampsFile } from './legal-stamp';

describe('legal texts', () => {
  it('carry a new Stand date whenever their wording changes', () => {
    const stamped = JSON.parse(readFileSync(legalStampsFile, 'utf8')) as Record<string, LegalStamp>;
    const current = currentLegalStamps();
    const changed = Object.keys(current).filter(
      (name) =>
        stamped[name]?.hash !== current[name]?.hash ||
        stamped[name]?.stand !== current[name]?.stand,
    );
    expect(
      changed,
      "Changed legal texts: set each one's **Stand:** to today's date, then run `bun run legal:stamp`",
    ).toEqual([]);
  });
});
