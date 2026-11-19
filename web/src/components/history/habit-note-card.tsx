import { useQuery } from '@tanstack/react-query';
import { NotebookPen } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { JournalLocked } from '@/components/journal/day-journal';
import { useJournal } from '@/components/journal/journal-context';
import { habitNotesKey, NoteSheet } from '@/components/today/note-sheet';
import { Button } from '@/components/ui/button';
import { cardVariants } from '@/components/ui/card';
import { addDays } from '@/lib/local-date';
import type { LocalDateString } from '@/lib/models';
import { useStore } from '@/lib/store';

/** History: the selected habit's note on the open day, with "Write a note" or "Edit note" (the same sheet as on Today). */
export function HabitNoteCard({
  habit,
  date,
}: {
  habit: { id: number; name: string };
  date: LocalDateString;
}) {
  const { t } = useTranslation();
  const journal = useJournal();
  const state = useStore(journal.state);
  const changed = useStore(journal.changed);
  const [open, setOpen] = useState(false);
  const [session, setSession] = useState(0);
  const query = useQuery({
    queryKey: [...habitNotesKey(habit.id, date), changed],
    queryFn: () => journal.habitNotes(addDays(date, -60), date, habit.id),
    enabled: state !== 'locked',
  });
  const text = query.data?.notes.find((n) => n.date === date)?.text ?? null;
  const headingId = `habit-note-heading-${habit.id}`;

  return (
    <section aria-labelledby={headingId} className={cardVariants()}>
      <h3 id={headingId} className="text-[1.125rem] [overflow-wrap:anywhere]">
        {t('habitNote.history.heading', { name: habit.name })}
      </h3>
      {state === 'locked' ? (
        <JournalLocked>{t('habitNote.locked')}</JournalLocked>
      ) : (
        <>
          {query.isSuccess && (
            <p
              className={
                text ? 'whitespace-pre-line [overflow-wrap:anywhere]' : 'text-muted-foreground'
              }
            >
              {text ?? t('habitNote.history.none')}
            </p>
          )}
          <Button
            variant="secondary"
            className="self-start"
            onClick={() => {
              setSession((n) => n + 1);
              setOpen(true);
            }}
          >
            <NotebookPen aria-hidden />
            {text ? t('habitNote.history.edit') : t('habitNote.history.write')}
          </Button>
          <NoteSheet key={session} habit={habit} date={date} open={open} onOpenChange={setOpen} />
        </>
      )}
    </section>
  );
}
