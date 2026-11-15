import { writeFileSync } from 'node:fs';
import { fingerprint, fingerprintsFile } from '../src/lib/guide/fingerprint';
import { guides } from '../src/lib/guide/registry';

const stamps = Object.fromEntries(
  Object.values(guides).flatMap((guide) => guide.entries.map((entry) => [entry.id, fingerprint(entry.covers)])),
);
writeFileSync(fingerprintsFile, `${JSON.stringify(stamps, null, 2)}\n`);
console.log(`Stamped ${Object.keys(stamps).length} guide entries in ${fingerprintsFile}`);
