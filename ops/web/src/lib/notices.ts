/** Most code points allowed in each notice text. */
export const maxText = 280;
/** Most notices the live document may hold. */
export const maxNotices = 10;

/** Length in Unicode code points, as the server counts it. */
export const codePoints = (s: string) => [...s].length;

/** A `datetime-local` value (local time) as UTC `YYYY-MM-DDTHH:mm:ssZ`; empty or invalid input gives undefined. */
export function toUtc(local: string): string | undefined {
  if (!local) return undefined;
  const d = new Date(local);
  return Number.isNaN(d.getTime()) ? undefined : d.toISOString().replace(/\.\d{3}Z$/, 'Z');
}

/** Lowercase ASCII letters and digits from `text`, other runs as `-`, at most 32 chars; `notice` when nothing is left. */
export function slugify(text: string): string {
  const slug = text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 32)
    .replace(/-+$/, '');
  return slug || 'notice';
}

/** `yyyyMMdd-<slug>` from the local date and the English text, with `-2`, `-3`... appended while the id is taken. */
export function noticeId(en: string, now: Date, taken: readonly string[]): string {
  const date = `${now.getFullYear()}${String(now.getMonth() + 1).padStart(2, '0')}${String(now.getDate()).padStart(2, '0')}`;
  const base = `${date}-${slugify(en)}`;
  let id = base;
  for (let n = 2; taken.includes(id); n++) id = `${base}-${n}`;
  return id;
}

export interface NoticeDraft {
  en: string;
  de: string;
  /** `datetime-local` values, empty when unset. */
  from: string;
  until: string;
}

/** The first reason the draft can't be published, or null when it passes the client-side checks. */
export function draftProblem(draft: NoticeDraft, count: number): string | null {
  if (count >= maxNotices) return `There are already ${maxNotices} notices. Delete one first.`;
  if (!draft.en.trim()) return 'Write the English text.';
  if (!draft.de.trim()) return 'Write the German text.';
  if (codePoints(draft.en) > maxText) return `The English text is longer than ${maxText} characters.`;
  if (codePoints(draft.de) > maxText) return `The German text is longer than ${maxText} characters.`;
  const from = toUtc(draft.from);
  const until = toUtc(draft.until);
  if (draft.from && !from) return '"Show from" is not a valid time.';
  if (draft.until && !until) return '"Show until" is not a valid time.';
  if (from && until && from >= until) return '"Show until" must be after "Show from".';
  return null;
}
