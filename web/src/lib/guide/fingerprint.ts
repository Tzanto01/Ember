import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

/** The repository root, which `covers` paths start from. */
export const repoRoot = join(here, '../../../../');

/** The file holding one sha256 per guide entry id. */
export const fingerprintsFile = join(here, 'guide-fingerprints.json');

/** sha256 over the `covers` files: each path and its content, line endings as LF. */
export function fingerprint(covers: readonly string[]): string {
  const hash = createHash('sha256');
  for (const path of covers) {
    hash.update(`${path}\n`);
    hash.update(readFileSync(repoRoot + path, 'utf8').replace(/\r\n/g, '\n'));
    hash.update('\0');
  }
  return hash.digest('hex');
}
