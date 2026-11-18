import { useQueryClient } from '@tanstack/react-query';
import { Feather, Pause, Pencil } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { formatDate } from '@/lib/local-date';
import type { LetGoHabit } from '@/lib/models';
import { daysFreeText } from './letgo-text';
import { SlipSheet } from './slip-sheet';

const shortDate: Intl.DateTimeFormatOptions = { day: 'numeric', month: 'long' };

/** Puts a let-go habit the server returned into Today's data and refreshes the History caches. */
export function useLetGoUpdate(): (habit: LetGoHabit) => void {
  const queryClient = useQueryClient();
  return (habit) => {
    queryClient.setQueryData<{ letGo?: LetGoHabit[] }>(['today'], (data) =>
      data?.letGo ? { ...data, letGo: data.letGo.map((h) => (h.id === habit.id ? habit : h)) } : data,
    );
    void queryClient.invalidateQueries({ queryKey: ['history'] });
    void queryClient.invalidateQueries({ queryKey: ['habit', habit.id] });
  };
}

function LetGoRow({ habit }: { habit: LetGoHabit }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const update = useLetGoUpdate();
  const paused = habit.today.state === 'paused';
  const notStarted = habit.today.state === 'beforestart';

  return (
    <li>
      <Card className="flex-row flex-wrap items-center justify-between gap-x-3 gap-y-2 py-3 md:py-3">
        <div className="flex min-w-0 flex-1 flex-col">
          <h3 className="text-[1.125rem] break-words">{habit.name}</h3>
          <p className="text-[0.875rem] text-muted-foreground [overflow-wrap:anywhere]">
            {paused ? (
              <span className="inline-flex items-start gap-1.5">
                <Pause aria-hidden className="mt-[3px] size-[14px] shrink-0" />
                {habit.pausedUntil
                  ? t('today.pausedUntil', { date: formatDate(habit.pausedUntil, shortDate) })
                  : t('today.paused')}
              </span>
            ) : notStarted ? (
              t('today.starts', { date: formatDate(habit.startDate, shortDate) })
            ) : (
              daysFreeText(habit.stats.daysFree)
            )}
          </p>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          <Button asChild variant="ghost" size="icon" className="text-muted-foreground">
            <Link to={`/habits/${habit.id}/edit`} aria-label={t('today.edit', { name: habit.name })}>
              <Pencil aria-hidden />
            </Link>
          </Button>
          {!paused && !notStarted && (
            <Button
              variant="secondary"
              aria-label={t('letgo.slippedName', { name: habit.name })}
              onClick={() => setOpen(true)}
            >
              <Feather aria-hidden />
              {t('letgo.slipped')}
            </Button>
          )}
        </div>
      </Card>
      <SlipSheet habit={habit} open={open} onOpenChange={setOpen} onChange={update} />
    </li>
  );
}

/** Today's "Letting go" section: one row per let-go habit with its days free and "I slipped". Renders nothing without any. */
export function LetGoSection({ habits }: { habits: LetGoHabit[] }) {
  const { t } = useTranslation();
  if (!habits.length) return null;
  return (
    <section aria-labelledby="letgo-heading" className="flex flex-col gap-3">
      <h2 id="letgo-heading" className="text-[1.25rem]">
        {t('letgo.section.heading')}
      </h2>
      <ul role="list" className="flex flex-col gap-3">
        {habits.map((habit) => (
          <LetGoRow key={habit.id} habit={habit} />
        ))}
      </ul>
    </section>
  );
}
