import { t } from '@/lib/i18n';

/** "5 days free", or a gentle line instead of "0 days free". */
export function daysFreeText(daysFree: number): string {
  return daysFree > 0 ? t('letgo.daysFree', { count: daysFree }) : t('letgo.daysFreeZero');
}

/** The line after a slip is saved: names the longest stretch, never a broken streak. */
export function notedText(longestStretch: number): string {
  return longestStretch > 0 ? t('letgo.noted', { count: longestStretch }) : t('letgo.notedFirst');
}
