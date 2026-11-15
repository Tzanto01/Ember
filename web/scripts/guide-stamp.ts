import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { fingerprint, fingerprintsFile } from '../src/lib/guide/fingerprint';
import { guides } from '../src/lib/guide/registry';

const entries = Object.values(guides).flatMap((guide) => guide.entries);
const args = process.argv.slice(2);
const all = args.includes('--all');
const ids = args.filter((arg) => arg !== '--all');
const unknown = ids.filter((id) => !entries.some((entry) => entry.id === id));

if ((!all && ids.length === 0) || unknown.length > 0) {
  if (unknown.length > 0) console.error(`Unknown guide entries: ${unknown.join(', ')}`);
  console.error('Usage: bun run guide:stamp <entry id> [...]   (stamp only the entries whose answers you re-read)');
  console.error('       bun run guide:stamp --all            (initial setup only)');
  process.exit(1);
}

const old: Record<string, string> = existsSync(fingerprintsFile) ? JSON.parse(readFileSync(fingerprintsFile, 'utf8')) : {};
const stamps = Object.fromEntries(
  entries
    .map((entry) => [entry.id, all || ids.includes(entry.id) ? fingerprint(entry.covers) : old[entry.id]] as const)
    .filter(([, stamp]) => stamp !== undefined),
);
writeFileSync(fingerprintsFile, `${JSON.stringify(stamps, null, 2)}\n`);
console.log(`Stamped ${all ? entries.length : ids.length} guide entries in ${fingerprintsFile}`);
