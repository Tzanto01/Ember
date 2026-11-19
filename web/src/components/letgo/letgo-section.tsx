import { useQueryClient } from '@tanstack/react-query';
import { ChevronDown, Feather, Pause, Pencil } from 'lucide-react';
import { type HTMLAttributes, type ReactNode, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { formatDate } from '@/lib/local-date';
import type { LetGoHabit } from '@/lib/models';
import { daysFreeText } from './letgo-text';
import { SlipSheet } from './slip-sheet';
import { TryInstead } from './try-instead';

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

/** What Today adds to each let-go row: its order key, the lifted look, and the "⋯" menu. */
export interface LetGoRowExtras {
  reorderKey: string;
  className?: string;
  menu: ReactNode;
}

function LetGoRow({ habit, extras }: { habit: LetGoHabit; extras?: LetGoRowExtras }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);
  const [session, setSession] = useState(0);
  const update = useLetGoUpdate();
  const paused = habit.today.state === 'paused';
  const notStarted = habit.today.state === 'beforestart';

  return (
    <li data-reorder-key={extras?.reorderKey} className={extras?.className}>
      <Card className="flex-row flex-wrap items-center justify-between gap-x-3 gap-y-2 py-3 md:py-3">
        <div className="flex min-w-0 grow basis-32 flex-col">
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
        <div className="flex min-w-0 flex-wrap items-center gap-1">
          <Button asChild variant="ghost" size="icon" className="text-muted-foreground">
            <Link to={`/habits/${habit.id}/edit`} aria-label={t('today.edit', { name: habit.name })}>
              <Pencil aria-hidden />
            </Link>
          </Button>
          {extras?.menu}
          {!paused && !notStarted && (
            <Button
              variant="secondary"
              aria-label={t('letgo.slippedName', { name: habit.name })}
              onClick={() => {
                setSession((n) => n + 1);
                setOpen(true);
              }}
            >
              <Feather aria-hidden />
              {t('letgo.slipped')}
            </Button>
          )}
        </div>
      </Card>
      <SlipSheet key={session} habit={habit} open={open} onOpenChange={setOpen} onChange={update} />
    </li>
  );
}

/**
 * Today's "Letting go" section: one row per let-go habit (in the given order) with its days free and "I slipped".
 * Renders nothing without any. `listProps` and `extras` let Today rearrange the rows.
 */
export function LetGoSection({
  habits,
  listProps,
  extras,
}: {
  habits: LetGoHabit[];
  listProps?: HTMLAttributes<HTMLUListElement>;
  extras?: (habit: LetGoHabit) => LetGoRowExtras;
}) {
  const { t } = useTranslation();
  const [ideasOpen, setIdeasOpen] = useState(false);
  if (!habits.length) return null;
  return (
    <section aria-labelledby="letgo-heading" className="flex flex-col gap-3">
      <h2 id="letgo-heading" className="text-[1.25rem]">
        {t('letgo.section.heading')}
      </h2>
      <ul role="list" className="flex flex-col gap-3" {...listProps}>
        {habits.map((habit) => (
          <LetGoRow key={habit.id} habit={habit} extras={extras?.(habit)} />
        ))}
      </ul>
      <div className="flex flex-col gap-2">
        <Button
          variant="link"
          className="self-start px-0"
          aria-expanded={ideasOpen}
          aria-controls="try-instead-list"
          onClick={() => setIdeasOpen(!ideasOpen)}
        >
          {t('letgo.tryInstead.heading')}
          <ChevronDown aria-hidden className={ideasOpen ? 'rotate-180' : undefined} />
        </Button>
        <div id="try-instead-list" hidden={!ideasOpen}>
          {ideasOpen && <TryInstead heading={false} />}
        </div>
      </div>
    </section>
  );
}
