import { currentLanguage, t } from '@/lib/i18n';

/** What goes into a moment's reflection block; empty or missing parts are left out. */
export interface Reflection {
  /** `HH:MM` when the moment started. */
  time: string;
  /** The named tile, for example "Too much / overloaded". */
  tile?: string;
  /** Intensity word before the practice. */
  before?: string;
  /** Intensity word now. */
  after?: string;
  /** "What set it off", as typed; blank lines are left out. */
  trigger?: string;
  /** The practice title that helped, or "Nothing really". */
  helped?: string;
  /** Answers to the optional questions, each with the question as shown; empty answers and blank lines are left out. */
  answers?: { question: string; text: string }[];
}

/**
 * The contract block for `appendPrivate` in the current language: `Moment HH:MM · {tile} · {before} → {after}`, then
 * `What set it off: …`, `What helped: …` and one `{question} …` line per answer. Empty segments and lines are left out;
 * without `after` there is no arrow.
 */
export function reflectionBlock({
  time,
  tile,
  before,
  after,
  trigger,
  helped,
  answers = [],
}: Reflection): string {
  const intensity = after ? [before, `→ ${after}`].filter(Boolean).join(' ') : before;
  const heading = [t('moment.block.heading', { time }), tile, intensity]
    .filter(Boolean)
    .join(' · ');
  const lines = [heading];
  const setOff = withoutBlankLines(trigger ?? '');
  if (setOff) lines.push(t('moment.block.setItOff', { text: setOff }));
  if (helped) lines.push(t('moment.block.helped', { text: helped }));
  for (const { question, text } of answers) {
    const answer = withoutBlankLines(text);
    if (answer) lines.push(t('moment.block.answer', { question, text: answer }));
  }
  return lines.join('\n');
}

/** A blank line ends a block when it is read back, so the parts keep their lines but drop the blank ones. */
function withoutBlankLines(text: string): string {
  return text.trim().replace(/\n\s*\n/g, '\n');
}

/** `HH:MM` (24 h, current language) of `at` in the IANA `timeZone`, or in the device zone when it is missing or unknown. */
export function momentTime(at: Date, timeZone?: string): string {
  const options: Intl.DateTimeFormatOptions = {
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  };
  try {
    return new Intl.DateTimeFormat(currentLanguage(), { ...options, timeZone }).format(at);
  } catch {
    return new Intl.DateTimeFormat(currentLanguage(), options).format(at);
  }
}
