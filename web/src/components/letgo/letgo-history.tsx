import { useQuery } from '@tanstack/react-query';
import { Feather, Pause } from 'lucide-react';
import { motion, useReducedMotion } from 'motion/react';
import { type ReactNode, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { isPausedOn } from '@/components/history/kept-days';
import { MonthCalendar } from '@/components/history/month-calendar';
import { DayJournal } from '@/components/journal/day-journal';
import { LiveRegion } from '@/components/live-region';
import { Spinner } from '@/components/spinner';
import { themeSeconds } from '@/components/today/reward';
import { Button } from '@/components/ui/button';
import { Card, cardVariants } from '@/components/ui/card';
import { Notice } from '@/components/ui/notice';
import { buildMonth, canGoNext, canGoPrevious, type CalendarDay, monthRange } from '@/lib/calendar';
import { t } from '@/lib/i18n';
import { addMonths, formatDate, monthLabel, startOfMonth } from '@/lib/local-date';
import type { LetGoDayState, LetGoHabit, LocalDateString } from '@/lib/models';
import { asApiError, problemToMessage } from '@/lib/problem';
import { api, auth } from '@/lib/services';
import { useStore } from '@/lib/store';
import { cn } from '@/lib/utils';
import { useLetGoUpdate } from './letgo-section';
import { daysFreeText } from './letgo-text';

const longDate: Intl.DateTimeFormatOptions = { weekday: 'long', day: 'numeric', month: 'long' };

const cellClass =
  'relative inline-flex shrink-0 flex-col items-center justify-center gap-0.5 rounded-cell text-[14px] leading-none transition-colors';

const stateClass: Record<LetGoDayState | 'future', string> = {
  free: 'border-[1.5px] border-empty text-foreground',
  slipped:
    'bg-lowday text-lowday-foreground shadow-[inset_0_0_0_1px_color-mix(in_oklch,var(--lowday-foreground)_35%,transparent)]',
  paused:
    'border-[1.5px] border-dashed border-paused bg-[repeating-linear-gradient(45deg,color-mix(in_oklch,var(--paused)_22%,transparent)_0_4px,transparent_4px_8px)] text-foreground',
  beforestart: 'text-beforestart',
  future: 'text-beforestart',
};

/** "free", "2 slips noted", "paused", "before this habit started" or "not yet". */
function dayWords(state: LetGoDayState | null, slips: number): string {
  if (state === null) return t('calendar.notYet');
  if (state === 'free') return t('letgo.day.free');
  if (state === 'slipped') return t('letgo.day.slipped', { count: slips });
  return t(`calendar.state.${state}`);
}

function cellName(day: CalendarDay, state: LetGoDayState | null, slips: number): string {
  const words = dayWords(state, slips);
  return t('calendar.cellName', {
    date: formatDate(day.date, longDate),
    words: day.isToday ? t('calendar.today', { words }) : words,
  });
}

function Cell({
  day,
  state,
  slips,
  expanded,
  onSelect,
}: {
  day: CalendarDay;
  state: LetGoDayState | null;
  slips: number;
  expanded: boolean;
  onSelect: () => void;
}) {
  const classes = cn(
    cellClass,
    'aspect-square w-full min-w-[40px] max-w-[56px] align-middle min-[360px]:min-w-[44px]',
    stateClass[state ?? 'future'],
  );
  const content = (
    <>
      <span aria-hidden="true" className={cn(day.isToday && 'underline decoration-2 underline-offset-[3px]')}>
        {Number(day.date.slice(8))}
      </span>
      {state === 'slipped' && <Feather aria-hidden className="size-[14px]" />}
      {state === 'paused' && <Pause aria-hidden className="hidden size-[14px] md:block" />}
    </>
  );
  if (!day.editable || state === null || state === 'beforestart') {
    return (
      <span data-state={state ?? 'future'} aria-current={day.isToday ? 'date' : undefined} className={classes}>
        {content}
        <span className="sr-only">{cellName(day, state, slips)}</span>
      </span>
    );
  }
  return (
    <button
      type="button"
      id={`day-${day.date}`}
      data-state={state}
      aria-label={cellName(day, state, slips)}
      aria-current={day.isToday ? 'date' : undefined}
      aria-expanded={expanded}
      aria-controls={expanded ? 'day-panel' : undefined}
      onClick={onSelect}
      className={cn(
        classes,
        'cursor-pointer focus-visible:z-10 aria-expanded:z-10 aria-expanded:outline-3 aria-expanded:outline-offset-2 aria-expanded:outline-ring aria-expanded:outline-solid',
      )}
    >
      {content}
    </button>
  );
}

function Legend() {
  const { t } = useTranslation();
  const items = [
    { state: 'free', label: t('letgo.legend.free') },
    { state: 'slipped', label: t('letgo.legend.slipped') },
    { state: 'paused', label: t('calendar.legend.paused') },
  ] as const;
  return (
    <ul aria-label={t('calendar.legendLabel')} className="grid grid-cols-2 gap-x-4 gap-y-2 text-[0.875rem] md:flex md:flex-wrap">
      {items.map((item) => (
        <li key={item.state} className="flex items-center gap-2">
          <span aria-hidden="true" className={cn(cellClass, 'size-[24px]', stateClass[item.state])} />
          {item.label}
        </li>
      ))}
    </ul>
  );
}

interface Tile {
  label: string;
  value: string;
}

/**
 * History for a let-go habit: the month calendar with slip days marked softly (paused days hatched), the day panel
 * to note a slip on a past day or remove that day's slips, the day's journal, and the tiles days free, longest stretch
 * and free days this month (also with streaks hidden).
 */
export function LetGoHistory({ habit, picker }: { habit: LetGoHabit; picker: ReactNode }) {
  const { t } = useTranslation();
  const today = useStore(auth.me)?.localToday ?? habit.today.date;
  const reduce = useReducedMotion();
  const update = useLetGoUpdate();
  const [month, setMonth] = useState(() => startOfMonth(today));
  const [openDate, setOpenDate] = useState<LocalDateString | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const heading = useRef<HTMLHeadingElement>(null);
  const focusDay = useRef<LocalDateString | null>(null);
  const range = monthRange(month, habit.startDate, today);

  const slipsQuery = useQuery({
    queryKey: ['history', 'slips', habit.id, range?.from, range?.to],
    queryFn: () => (range ? api.getSlips(habit.id, range.from, range.to) : Promise.resolve([])),
  });
  const slips = new Map((slipsQuery.data ?? []).map((slip) => [slip.date, slip.count]));
  const stateOf = (day: CalendarDay): LetGoDayState | null =>
    day.state === null
      ? null
      : day.date < habit.startDate
        ? 'beforestart'
        : slips.has(day.date)
          ? 'slipped'
          : isPausedOn(habit, day.date)
            ? 'paused'
            : 'free';
  const weeks = buildMonth(month, [], [], habit.startDate, today);
  const openDay = openDate ? (weeks.flat().find((d) => d.inMonth && d.editable && d.date === openDate) ?? null) : null;
  const openState = openDay ? stateOf(openDay) : null;
  const openWords = openDay ? dayWords(openState, slips.get(openDay.date) ?? 0) : '';
  const monthError = slipsQuery.isError && !slipsQuery.data && !slipsQuery.isFetching ? asApiError(slipsQuery.error) : null;

  useEffect(() => {
    if (openDate) heading.current?.focus();
  }, [openDate]);

  useEffect(() => {
    if (!focusDay.current) return;
    document.getElementById(`day-${focusDay.current}`)?.focus();
    focusDay.current = null;
  });

  function shiftMonth(step: 1 | -1) {
    if (!(step < 0 ? canGoPrevious(month, habit.startDate) : canGoNext(month, today))) return;
    setMonth(addMonths(month, step));
    setOpenDate(null);
    setProblem(null);
  }

  function close() {
    focusDay.current = openDate;
    setOpenDate(null);
    setProblem(null);
  }

  function toggle(date: LocalDateString) {
    if (date === openDate) {
      close();
      return;
    }
    setProblem(null);
    setOpenDate(date);
  }

  async function edit(add: boolean) {
    const date = openDate;
    if (!date || busy) return;
    setBusy(true);
    setProblem(null);
    try {
      update(add ? await api.addSlip(habit.id, date) : await api.clearSlips(habit.id, date));
      await slipsQuery.refetch();
      setAnnouncement(t(add ? 'letgo.announce.added' : 'letgo.announce.cleared', { date: formatDate(date, longDate) }));
    } catch (error) {
      const apiError = asApiError(error);
      if (!apiError.handled) setProblem(problemToMessage(apiError).message);
    } finally {
      setBusy(false);
    }
  }

  const { daysFree, longestStretch, freeDaysThisMonth } = habit.stats;
  const tiles: Tile[] = [{ label: t('letgo.stats.daysFree'), value: daysFreeText(daysFree) }];
  if (longestStretch > 0) tiles.push({ label: t('letgo.stats.longest'), value: t('stats.days', { count: longestStretch }) });
  if (freeDaysThisMonth > 0)
    tiles.push({ label: t('letgo.stats.month'), value: t('stats.days', { count: freeDaysThisMonth }) });

  return (
    <div className="flex flex-col gap-6 lg:grid lg:grid-cols-[minmax(0,1fr)_22rem] lg:items-start">
      <LiveRegion message={announcement} />
      <div className="flex min-w-0 flex-col gap-6 max-lg:contents">
        {picker}
        <MonthCalendar
          title={monthLabel(month)}
          weeks={weeks}
          loading={slipsQuery.isFetching && !busy}
          canPrevious={canGoPrevious(month, habit.startDate)}
          canNext={canGoNext(month, today)}
          onPrevious={() => shiftMonth(-1)}
          onNext={() => shiftMonth(1)}
          openDate={openDate}
          onSelect={toggle}
          reward={null}
          onRewardEnd={() => undefined}
          renderDay={(day) => (
            <Cell
              day={day}
              state={stateOf(day)}
              slips={slips.get(day.date) ?? 0}
              expanded={openDate === day.date}
              onSelect={() => toggle(day.date)}
            />
          )}
          problem={
            monthError && (
              <div className="px-4 md:px-0">
                <Notice tone="notice" heading={t('history.daysError')}>
                  {!monthError.handled && <p>{problemToMessage(monthError).message}</p>}
                  <Button variant="secondary" onClick={() => void slipsQuery.refetch()}>
                    {t('history.tryAgain')}
                  </Button>
                </Notice>
              </div>
            )
          }
        />
        <div className="max-lg:order-1">
          <Legend />
        </div>
      </div>
      <div className="flex flex-col gap-6 lg:sticky lg:top-[calc(56px+env(safe-area-inset-top)+1.5rem)]">
        {openDay && openState ? (
          <motion.section
            id="day-panel"
            aria-labelledby="day-panel-heading"
            className={cardVariants()}
            initial={reduce ? false : { opacity: 0, y: 8 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ duration: reduce ? 0 : themeSeconds('--ember-dur', 0.22) }}
            onKeyDown={(event) => {
              if (event.key === 'Escape') close();
            }}
          >
            <div className="flex flex-col gap-1">
              <h3 id="day-panel-heading" ref={heading} tabIndex={-1} className="text-[1.125rem] focus:outline-none">
                {formatDate(openDay.date, longDate)}
              </h3>
              <p className="text-muted-foreground">{openWords.charAt(0).toUpperCase() + openWords.slice(1)}</p>
            </div>
            <div className="flex flex-wrap gap-2">
              <Button variant="secondary" aria-disabled={busy || undefined} onClick={() => void edit(true)}>
                <Feather aria-hidden />
                {t('letgo.day.add')}
              </Button>
              {openState === 'slipped' && (
                <Button variant="secondary" aria-disabled={busy || undefined} onClick={() => void edit(false)}>
                  {t('letgo.day.clear')}
                </Button>
              )}
            </div>
            {busy && (
              <p className="flex items-center gap-2 text-[0.875rem] text-muted-foreground">
                <Spinner />
                {t('day.saving')}
              </p>
            )}
            {problem && <Notice tone="notice">{problem}</Notice>}
            <div>
              <Button variant="ghost" className="-ms-2" onClick={close}>
                {t('day.close')}
              </Button>
            </div>
          </motion.section>
        ) : (
          <Card className="hidden items-center text-center text-muted-foreground lg:flex">
            <p>{t('history.pickDay')}</p>
          </Card>
        )}
        {openDay && <DayJournal date={openDay.date} />}
        <dl
          className={cn(
            'grid gap-2',
            tiles.length === 3 ? 'grid-cols-[repeat(auto-fit,minmax(min(100%,6rem),1fr))]' : 'grid-cols-2',
          )}
        >
          {tiles.map((tile) => (
            <div key={tile.label} className={cn(cardVariants({ variant: 'stat' }), 'min-w-0')}>
              <dt className="text-[0.875rem]">{tile.label}</dt>
              <dd className="m-0 mt-auto font-semibold break-words">{tile.value}</dd>
            </div>
          ))}
        </dl>
      </div>
    </div>
  );
}
