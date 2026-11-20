import { useQuery } from '@tanstack/react-query';
import { X } from 'lucide-react';
import { Dialog } from 'radix-ui';
import { type SubmitEvent, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { JournalLocked } from '@/components/journal/day-journal';
import { useJournal } from '@/components/journal/journal-context';
import { Button } from '@/components/ui/button';
import { controlClass } from '@/components/ui/input';
import { Notice } from '@/components/ui/notice';
import { maxHabitNote } from '@/lib/journal/journal';
import { JournalLockedError } from '@/lib/journal/types';
import { addDays, formatDate } from '@/lib/local-date';
import type { LocalDateString } from '@/lib/models';
import { asApiError } from '@/lib/problem';
import { journalKey } from '@/lib/query-keys';
import { auth } from '@/lib/services';
import { useStore } from '@/lib/store';
import { cn } from '@/lib/utils';

/** How far back the sheet looks for earlier notes on the habit, and how many it shows. */
const earlierDays = 60;
const earlierShown = 3;
const earlierDate: Intl.DateTimeFormatOptions = {
  weekday: 'short',
  day: 'numeric',
  month: 'short',
};

/** Query key of one habit's notes around `date`, so History and Today read the same cache. */
export const habitNotesKey = (habitId: number, date: LocalDateString) =>
  [...journalKey, 'habit', habitId, date] as const;

export interface NoteSheetProps {
  habit: { id: number; name: string };
  date: LocalDateString;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Opened right after a check-in ("Offer a note after check-in"): adds the gentle "you can skip this" line. */
  afterCheckIn?: boolean;
  /** Called after the note was saved (`saved: true`) or deleted. */
  onSaved?: (saved: boolean) => void;
}

/**
 * The short note on one habit's day: up to 280 characters with a counter, "Save note" and "Not now", and the habit's
 * earlier notes. Online only; when the save fails the text stays. Encrypted like day notes while the private journal
 * is on; while it is locked on this device the sheet says so and shows nothing. Mount it with a new `key` each time it
 * opens so it starts from the stored note.
 */
export function NoteSheet({
  habit,
  date,
  open,
  onOpenChange,
  afterCheckIn = false,
  onSaved,
}: NoteSheetProps) {
  const { t } = useTranslation();
  const journal = useJournal();
  const state = useStore(journal.state);
  const changed = useStore(journal.changed);
  const [draft, setDraft] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const query = useQuery({
    queryKey: [...habitNotesKey(habit.id, date), changed],
    queryFn: () => journal.habitNotes(addDays(date, -earlierDays), date, habit.id),
    enabled: open && state !== 'locked',
  });
  const notes = query.data?.notes ?? [];
  const stored = notes.find((n) => n.date === date)?.text ?? '';
  const earlier = notes
    .filter((n) => n.date < date)
    .reverse()
    .slice(0, earlierShown);
  const text = draft ?? stored;
  const length = text.trim().length;
  const counterId = `habit-note-${habit.id}-count`;
  const locked = state === 'locked';
  const canSave = query.isSuccess && draft !== null && draft.trim() !== stored;
  const isToday = useStore(auth.me)?.localToday === date;

  function change(next: boolean): void {
    if (!busy) onOpenChange(next);
  }

  async function save(event: SubmitEvent): Promise<void> {
    event.preventDefault();
    if (busy || !canSave) return;
    setBusy(true);
    setProblem(null);
    try {
      await journal.saveHabitNote(habit.id, date, text);
      onSaved?.(length > 0);
      onOpenChange(false);
    } catch (error) {
      if (error instanceof JournalLockedError || error instanceof RangeError)
        setProblem(error.message);
      else if (!asApiError(error).handled) setProblem(t('habitNote.saveFailed'));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog.Root open={open} onOpenChange={change}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-[color-mix(in_oklch,var(--foreground)_35%,transparent)]" />
        <Dialog.Content
          className={cn(
            'fixed z-40 flex flex-col gap-4 overflow-y-auto border-border bg-card text-card-foreground shadow-card focus:outline-none',
            'motion-safe:animate-[ember-arrive_var(--ember-dur)_var(--ember-ease)_both]',
            'inset-x-0 bottom-0 max-h-[85dvh] rounded-t-2xl border-t p-5 pb-[calc(1.25rem+env(safe-area-inset-bottom))]',
            'sm:inset-x-auto sm:top-1/2 sm:bottom-auto sm:left-1/2 sm:w-[28rem] sm:-translate-x-1/2 sm:-translate-y-1/2 sm:rounded-2xl sm:border sm:p-6',
          )}
        >
          <div className="flex items-start justify-between gap-3">
            <div className="flex min-w-0 flex-col gap-1 pt-2">
              <Dialog.Title className="text-[1.375rem] [overflow-wrap:anywhere]">
                {habit.name}
              </Dialog.Title>
              <Dialog.Description className="text-muted-foreground">
                {afterCheckIn
                  ? t('habitNote.offer')
                  : formatDate(date, { weekday: 'long', day: 'numeric', month: 'long' })}
              </Dialog.Description>
            </div>
            <Dialog.Close asChild>
              <Button
                variant="ghost"
                size="icon"
                className="-mr-2 shrink-0 rounded-full"
                aria-label={t('habitNote.close')}
                aria-disabled={busy || undefined}
              >
                <X aria-hidden />
              </Button>
            </Dialog.Close>
          </div>
          {locked ? (
            <JournalLocked>{t('habitNote.locked')}</JournalLocked>
          ) : (
            <form className="flex flex-col gap-4" onSubmit={(event) => void save(event)}>
              <div className="flex flex-col gap-2">
                <label htmlFor={`habit-note-${habit.id}`} className="font-medium">
                  {isToday ? t('habitNote.label') : t('habitNote.labelDay')}
                </label>
                <textarea
                  id={`habit-note-${habit.id}`}
                  aria-describedby={`${counterId} habit-note-privacy`}
                  value={text}
                  maxLength={maxHabitNote}
                  rows={3}
                  onChange={(event) => {
                    setDraft(event.target.value);
                    setProblem(null);
                  }}
                  className={cn(controlClass, 'min-h-[6rem] resize-y leading-relaxed')}
                />
                <p className="flex justify-between gap-3 text-[0.875rem] text-muted-foreground">
                  <span id="habit-note-privacy">{t('habitNote.privacy')}</span>
                  <span aria-hidden="true" className="shrink-0 tabular-nums">
                    {t('habitNote.counter', { count: length, max: maxHabitNote })}
                  </span>
                  <span id={counterId} className="sr-only">
                    {t('journal.counter', { count: length, max: maxHabitNote })}
                  </span>
                </p>
              </div>
              {query.isError && (
                <Notice tone="notice" heading={t('habitNote.loadFailed')}>
                  <Button type="button" variant="secondary" onClick={() => void query.refetch()}>
                    {t('today.tryAgain')}
                  </Button>
                </Notice>
              )}
              {problem && (
                <Notice tone="notice" heading={t('habitNote.notSaved')}>
                  <p>{problem}</p>
                </Notice>
              )}
              <div className="flex flex-col gap-3 sm:flex-row-reverse">
                <BusyButton
                  type="submit"
                  block
                  className="sm:w-auto"
                  busy={busy}
                  busyLabel={t('habitNote.saving')}
                  aria-disabled={!canSave || undefined}
                >
                  {t('habitNote.save')}
                </BusyButton>
                <Dialog.Close asChild>
                  <Button
                    type="button"
                    variant="ghost"
                    block
                    className="sm:w-auto"
                    aria-disabled={busy || undefined}
                  >
                    {t('habitNote.notNow')}
                  </Button>
                </Dialog.Close>
              </div>
              {earlier.length > 0 && (
                <section
                  aria-labelledby={`habit-note-${habit.id}-earlier`}
                  className="flex flex-col gap-2"
                >
                  <h3 id={`habit-note-${habit.id}-earlier`} className="text-[1rem]">
                    {t('habitNote.earlier', { name: habit.name })}
                  </h3>
                  <ul role="list" className="flex flex-col gap-1.5 text-[0.875rem]">
                    {earlier.map((note) => (
                      <li key={note.date} className="[overflow-wrap:anywhere]">
                        <span className="font-medium">{formatDate(note.date, earlierDate)}</span>
                        <span className="text-muted-foreground"> · {note.text}</span>
                      </li>
                    ))}
                  </ul>
                </section>
              )}
            </form>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
