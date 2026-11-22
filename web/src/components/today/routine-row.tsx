import { ChevronDown, Moon, Pencil } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { DayCell } from '@/components/day-cell';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import type { Habit, LocalDateString } from '@/lib/models';
import { memberSettled } from '@/lib/native/reminder-plan';
import { cn } from '@/lib/utils';

export interface RoutineRowProps {
  routineId: number;
  name: string;
  /** "{name} routine", for the Edit link's label. */
  title: string;
  /** The members For today lists, in routine order. */
  habits: readonly Habit[];
  date: LocalDateString;
  lowDay: boolean;
  open: boolean;
  onToggle: () => void;
  /** The id of the list of members this row opens. */
  controls: string;
  /** The row's "⋯" menu, next to Edit. */
  menu: ReactNode;
}

/** The DOM id of a routine's heading on Today, which also names its region. */
export const routineHeadingId = (routineId: number) => `routine-${routineId}`;

/**
 * A routine's row on Today: its name as a disclosure button over the whole card, the members settled out of those for
 * today (as `HabitRules.IsRoutineDone` counts them, so skipped and not due members count), Done today once all are, or
 * the low-day line on a low day, with Edit and the menu.
 */
export function RoutineRow({
  routineId,
  name,
  title,
  habits,
  date,
  lowDay,
  open,
  onToggle,
  controls,
  menu,
}: RoutineRowProps) {
  const { t } = useTranslation();
  const settled = habits.filter((habit) => memberSettled(habit, date)).length;
  const total = habits.length;
  const done = !lowDay && settled === total;
  const progressId = `routine-progress-${routineId}`;

  let meta: ReactNode;
  if (lowDay) {
    meta = (
      <span className="inline-flex items-start gap-1.5">
        <Moon aria-hidden className="mt-[3px] size-[14px] shrink-0" />
        {t('today.lowDayRow')}
      </span>
    );
  } else if (done) {
    meta = <span>{t('today.doneToday')}</span>;
  } else {
    meta = (
      <span className="inline-flex items-center gap-2">
        <span aria-hidden="true" className="inline-flex gap-1">
          {habits.map((habit) => (
            <DayCell
              key={habit.id}
              state={memberSettled(habit, date) ? 'done' : 'notdone'}
              size="pip"
            />
          ))}
        </span>
        <span aria-hidden="true" className="font-medium text-foreground tabular-nums">
          {t('today.routine.progress', { done: settled, total })}
        </span>
        <span className="sr-only">
          {t('today.routine.progressSpoken', { done: settled, total })}
        </span>
      </span>
    );
  }

  return (
    <Card
      className={cn(
        'relative isolate grid grid-cols-[minmax(0,1fr)_56px] items-center gap-x-3 gap-y-0 py-3 md:py-3',
        'has-[[aria-expanded]:hover]:bg-[color-mix(in_oklch,var(--accent)_45%,var(--card))]',
        done &&
          'bg-[color-mix(in_oklch,var(--done)_8%,var(--card))] after:pointer-events-none after:absolute after:inset-0 after:rounded-[inherit] after:shadow-[inset_3px_0_0_var(--done)]',
      )}
    >
      <div className="flex min-w-0 flex-col">
        <h3 id={routineHeadingId(routineId)} className="text-[1.125rem] break-words">
          <button
            type="button"
            aria-expanded={open}
            aria-controls={controls}
            aria-describedby={progressId}
            onClick={onToggle}
            className="text-left focus-visible:outline-none before:absolute before:inset-0 before:z-[1] before:rounded-xl focus-visible:before:[outline:3px_solid_var(--ring)] focus-visible:before:[outline-offset:2px]"
          >
            {name}
          </button>
        </h3>
        <div className="flex items-center justify-between gap-2">
          <p
            id={progressId}
            className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-[0.875rem] text-muted-foreground [overflow-wrap:anywhere]"
          >
            {meta}
          </p>
          <div className="relative z-10 flex shrink-0">
            <Button asChild variant="ghost" size="icon" className="text-muted-foreground">
              <Link
                to={`/routines/${routineId}/edit`}
                aria-label={t('today.edit', { name: title })}
              >
                <Pencil aria-hidden />
              </Link>
            </Button>
            {menu}
          </div>
        </div>
      </div>
      <span
        aria-hidden="true"
        className="pointer-events-none flex size-[56px] items-center justify-center text-muted-foreground"
      >
        <ChevronDown
          className={cn('size-[24px] motion-safe:transition-transform', open && 'rotate-180')}
        />
      </span>
    </Card>
  );
}
