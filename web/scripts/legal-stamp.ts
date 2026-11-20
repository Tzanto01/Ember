import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { currentLegalStamps, type LegalStamp, legalStampsFile } from '../src/lib/legal-stamp';

const old: Record<string, LegalStamp> = existsSync(legalStampsFile)
  ? (JSON.parse(readFileSync(legalStampsFile, 'utf8')) as Record<string, LegalStamp>)
  : {};
const current = currentLegalStamps();
const undated = Object.entries(current).filter(
  ([name, stamp]) => old[name] && old[name].hash !== stamp.hash && old[name].stand === stamp.stand,
);

if (undated.length > 0) {
  for (const [name] of undated)
    console.error(
      `${name} changed but its **Stand:** is still ${old[name]?.stand}. Set it to today's date first.`,
    );
  process.exit(1);
}

writeFileSync(legalStampsFile, `${JSON.stringify(current, null, 2)}\n`);
process.stdout.write(`Stamped ${Object.keys(current).length} legal texts in ${legalStampsFile}\n`);
