const zone = 'Europe/Berlin';
const locale = 'en-GB';

/** "updated 5s ago" style age; future or invalid times count as 0 s. */
export function formatAgo(from: string | number, now: number): string {
  const at = typeof from === 'number' ? from : Date.parse(from);
  const s = Number.isNaN(at) ? 0 : Math.max(0, Math.floor((now - at) / 1000));
  if (s < 60) return `${s}s ago`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60);
  if (h < 48) return `${h}h ago`;
  return `${Math.floor(h / 24)}d ago`;
}

const dayFormat = new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short', timeZone: 'UTC' });
const dayYearFormat = new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
const stampFormat = new Intl.DateTimeFormat(locale, { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit', timeZone: zone });

/** A `YYYY-MM-DD` calendar date as "18 Nov" (no time zone shift). */
export const formatDay = (date: string) => dayFormat.format(new Date(`${date}T00:00:00Z`));
export const formatDayYear = (date: string) => dayYearFormat.format(new Date(`${date}T00:00:00Z`));
/** A UTC timestamp in Europe/Berlin local time. */
export const formatStamp = (ts: string) => stampFormat.format(new Date(ts));

export function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024;
    i++;
  }
  return `${value.toFixed(i === 0 || value >= 100 ? 0 : 1)} ${units[i]}`;
}

export const formatNumber = (n: number) => n.toLocaleString(locale);
export const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);
/** Badge tone for a disk's free space: `bad` below 5 %, `warn` below 10 %, otherwise (or with no size) none. */
export function diskTone(free: number, total: number): 'bad' | 'warn' | null {
  if (total <= 0) return null;
  const ratio = free / total;
  return ratio < 0.05 ? 'bad' : ratio < 0.1 ? 'warn' : null;
}

export const percent = (part: number, whole: number) => (whole > 0 ? `${Math.round((part / whole) * 100)}%` : '0%');
