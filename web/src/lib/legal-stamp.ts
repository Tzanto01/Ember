import { createHash } from 'node:crypto';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

/** The folder holding the legal texts served at `/legal/*.md`. */
export const legalDir = join(here, '../../public/legal');

/** The file holding each legal text's last stamped `Stand` date and content hash. */
export const legalStampsFile = join(here, 'legal-stamps.json');

/** One legal text's `Stand` date and the sha256 of everything else in it. */
export interface LegalStamp {
  stand: string;
  hash: string;
}

const standLine = /^\*\*Stand:\*\* (.+)$/m;

/** The current stamp of every legal text, keyed by file name. Throws when a text has no `**Stand:**` line. */
export function currentLegalStamps(): Record<string, LegalStamp> {
  return Object.fromEntries(
    readdirSync(legalDir)
      .filter((name) => name.endsWith('.md'))
      .sort()
      .map((name) => {
        const text = readFileSync(join(legalDir, name), 'utf8').replace(/\r\n/g, '\n');
        const stand = standLine.exec(text)?.[1];
        if (!stand) throw new Error(`${name} has no **Stand:** line`);
        const hash = createHash('sha256').update(text.replace(standLine, '')).digest('hex');
        return [name, { stand, hash }];
      }),
  );
}
