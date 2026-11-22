import { useQuery, useQueryClient } from '@tanstack/react-query';
import { CalendarDays, ChevronDown, ListOrdered, Moon, Plus } from 'lucide-react';
import { useReducedMotion } from 'motion/react';
import { type ReactNode, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router';
import { TapLink } from '@/components/auth/busy-button';
import { EmptyState } from '@/components/empty-state';
import { useReminderReach, useReminderSettingsHash } from '@/components/habits/reminder-reach';
import { FiresideToday } from '@/components/fireside/fireside-today';
import { isPausedOn } from '@/components/history/kept-days';
import { DayJournal } from '@/components/journal/day-journal';
import { LetGoSection } from '@/components/letgo/letgo-section';
import { DeviceNotices } from '@/components/local/device-notices';
import { LiveRegion } from '@/components/live-region';
import { MomentDoor } from '@/components/moment/moment-door';
import { PageHeader } from '@/components/page-header';
import { routineTitle } from '@/components/routines/routine-title';
import { TipSlot } from '@/components/tips/tip-slot';
import { checkButtonId } from '@/components/today/check-button';
import { HabitRow, habitRowId } from '@/components/today/habit-row';
import { NoteSheet } from '@/components/today/note-sheet';
import { RoutineRow, routineHeadingId } from '@/components/today/routine-row';
import { ListMenu, RowMenu } from '@/components/today/row-menu';
import { useReorder } from '@/components/today/use-reorder';
import { HearthIllustration } from '@/components/today/hearth-illustration';
import { readReward, type RewardKeyword } from '@/components/today/reward';
import { TodaysEmber } from '@/components/today/todays-ember';
import { WhatsNewCard } from '@/components/today/whats-new-card';
import { Button } from '@/components/ui/button';
import { Card, cardVariants } from '@/components/ui/card';
import { Notice } from '@/components/ui/notice';
import { Skeleton } from '@/components/ui/skeleton';
import { isOffday } from '@/lib/calendar';
import { type DayEdit, withDayEdit } from '@/lib/device-store';
import { highlightMs } from '@/lib/guide/highlight';
import { t } from '@/lib/i18n';
import { addDays, formatDate } from '@/lib/local-date';
import { pulse } from '@/lib/moment/voice';
import {
  buildHabits,
  type Habit,
  isLetGo,
  type LocalDateString,
  type Routine,
  type WeeklyStats,
} from '@/lib/models';
import { asApiError, problemToMessage } from '@/lib/problem';
import { routinesKey, type TodayData, todayKey } from '@/lib/query-keys';
import { loadFreshMe } from '@/components/today/fresh-me';
import { api, auth, deviceSync } from '@/lib/services';
import { useStore } from '@/lib/store';
import { notTodayOpen, openRoutines, rememberNotTodayOpen, rememberRoutineOpen, useCalmMode } from '@/lib/theme';
import {
  habitKey,
  moveKey,
  type NotTodayReason,
  orderAfterMove,
  planToday,
  routineKey,
  routineOrderAfterMove,
  type TodayItem,
} from '@/lib/today-plan';
import { cn } from '@/lib/utils';

/** Today's body: one column, from `lg` the door, notices and habits left and the day's cards right. */
const columns =
  'flex flex-col gap-6 lg:grid lg:grid-cols-[minmax(0,1fr)_20rem] lg:items-start 2xl:grid-cols-[minmax(0,1.8fr)_minmax(24rem,1fr)] 2xl:gap-8';

interface Problem {
  heading: string;
  message: string;
}

/** True when nothing was checked in yesterday and it wasn't a low day; ask only when a habit was due yesterday. */
async function loadYesterdayOpen(yesterday: LocalDateString): Promise<boolean> {
  const [[stat], lowDays] = await Promise.all([
    api.dayStats(yesterday, yesterday),
    api.listLowDays(yesterday, yesterday),
  ]);
  return !!stat && !stat.showedUp && !lowDays.includes(yesterday);
}

/** The weekly card's first line: the count without a grade when some but not all check-ins happened. */
function weeklyHeadline(weekly: WeeklyStats): string {
  if (weekly.totalDone === 0 || weekly.totalDone >= weekly.totalPossible) return weekly.headline;
  return t('weekly.partial', { count: weekly.totalDone });
}

async function loadToday(): Promise<TodayData> {
  await loadFreshMe().catch(() => null);
  const today = auth.me.get()?.localToday ?? null;
  const [habits, lowDays, weekly, routines] = await Promise.allSettled([
    api.listHabits(),
    today ? api.listLowDays(today, today) : Promise.reject(new Error('no local day')),
    api.weeklyStats(),
    api.listRoutines(),
  ]);
  if (habits.status === 'rejected') throw habits.reason;
  return {
    habits: buildHabits(habits.value),
    letGo: habits.value.filter(isLetGo),
    lowDayToday: lowDays.status === 'fulfilled' && today ? lowDays.value.includes(today) : null,
    weekly: weekly.status === 'fulfilled' ? weekly.value : null,
    routines: routines.status === 'fulfilled' ? routines.value : null,
  };
}

/** `/today`: today's habits with check-ins, the low-day switch, Today's ember and the weekly summary. */
export default function TodayPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: todayKey, queryFn: loadToday, structuralSharing: false });
  const { data, refetch } = query;
  const me = useStore(auth.me);
  const today: LocalDateString | null = me?.localToday ?? null;
  const showStreaks = me?.showStreaks !== false;
  const synced = useStore(deviceSync.synced);
  const firstSynced = useRef(synced);
  const reduceMotion = useReducedMotion();
  const reach = useReminderReach();
  const reminderHash = useReminderSettingsHash();
  const yesterday = today ? addDays(today, -1) : null;
  const tracksYesterday =
    !!yesterday &&
    !!data?.habits.some(
      (h) =>
        h.frequency !== 'weekly' && h.startDate <= yesterday && !isOffday(h, yesterday) && !isPausedOn(h, yesterday),
    );
  const yesterdayQuery = useQuery({
    queryKey: [...todayKey, 'yesterday', yesterday],
    queryFn: () => loadYesterdayOpen(yesterday!),
    enabled: tracksYesterday,
  });
  const focusAfter = useRef<string | null>(null);

  const inFlight = useRef(new Set<number>());
  const [busy, setBusy] = useState<ReadonlySet<number>>(new Set());
  const [rewards, setRewards] = useState<ReadonlyMap<number, RewardKeyword>>(new Map());
  const [lowDayBusy, setLowDayBusy] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [searchParams, setSearchParams] = useSearchParams();
  const openHabit = searchParams.get('habit');
  const [highlighted, setHighlighted] = useState<number | null>(null);
  const calm = useCalmMode();
  const order = me?.todayOrder ?? null;
  const [laterOpen, setLaterOpen] = useState(notTodayOpen);
  const [routinesOpen, setRoutinesOpen] = useState(openRoutines);
  const [note, setNote] = useState<{ habit: { id: number; name: string }; afterCheckIn: boolean; session: number } | null>(null);
  const [noteOpen, setNoteOpen] = useState(false);
  const orderSeq = useRef(0);
  const orderChain = useRef<Promise<unknown>>(Promise.resolve());
  const ordersSaving = useRef(0);
  const confirmedOrder = useRef<string[] | null>(null);
  const routineChain = useRef<Promise<unknown>>(Promise.resolve());

  useEffect(() => {
    if (synced !== firstSynced.current) void refetch();
  }, [synced, refetch]);

  useEffect(() => {
    const target = focusAfter.current && document.getElementById(focusAfter.current);
    if (!target) return;
    focusAfter.current = null;
    target.focus();
  });

  useEffect(() => {
    if (!data || query.isFetching || openHabit === null) return;
    const target = data.habits.find((h) => String(h.id) === openHabit);
    const row = target && document.getElementById(habitRowId(target.id));
    const closed = row?.closest<HTMLElement>('ul[hidden]');
    if (closed) {
      const routineId = Number(closed.dataset['routineId']);
      // eslint-disable-next-line react-hooks/set-state-in-effect -- opens the routine or the Not for today list the linked habit sits in
      if (routineId) setRoutinesOpen((open) => new Set(open).add(routineId));
      else setLaterOpen(true);
      return;
    }
    setSearchParams(
      (params) => {
        params.delete('habit');
        return params;
      },
      { replace: true, preventScrollReset: true },
    );
    if (!target || !row) return;
    row.scrollIntoView({ block: 'center', behavior: reduceMotion || calm ? 'auto' : 'smooth' });
    setHighlighted(target.id);
  }, [data, query.isFetching, openHabit, setSearchParams, reduceMotion, calm, laterOpen, routinesOpen]);

  useEffect(() => {
    if (highlighted === null) return;
    const timer = setTimeout(() => setHighlighted(null), highlightMs);
    return () => clearTimeout(timer);
  }, [highlighted]);

  const nativeCheckIn = useStore(deviceSync.nativeCheckIn);
  useEffect(() => {
    if (nativeCheckIn) void refetch();
  }, [nativeCheckIn, refetch]);

  const setData = (fn: (data: TodayData) => TodayData) =>
    queryClient.setQueryData<TodayData>(todayKey, (current) => current && fn(current));
  const updateHabit = (id: number, fn: (current: Habit) => Habit) =>
    setData((d) => ({ ...d, habits: d.habits.map((h) => (h.id === id ? fn(h) : h)) }));
  const mark = (id: number, on: boolean) =>
    setBusy((set) => {
      const next = new Set(set);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  const setReward = (id: number, reward: RewardKeyword | null) =>
    setRewards((map) => {
      const next = new Map(map);
      if (reward) next.set(id, reward);
      else next.delete(id);
      return next;
    });

  async function toggle(habit: Habit, undoOne = false) {
    if (inFlight.current.has(habit.id) || !data) return;
    inFlight.current.add(habit.id);
    const target = habit.timesPerDay;
    const multi = target > 1;
    const current = Math.min(habit.today.count, target);
    if (undoOne && current === 1) focusAfter.current = checkButtonId(habit.id);
    const nextCount = !multi
      ? null
      : undoOne
        ? Math.max(current - 1, 0)
        : habit.today.state === 'done'
          ? 0
          : current + 1;
    const checkIn = nextCount === null ? habit.today.state !== 'done' : nextCount > current;
    const reaches = nextCount === null ? checkIn : nextCount >= target;
    const edit: DayEdit =
      nextCount === null
        ? { habitId: habit.id, date: habit.today.date, completed: checkIn ? true : null }
        : { habitId: habit.id, date: habit.today.date, completed: null, count: nextCount };
    const optimistic = withDayEdit(habit, edit, data.lowDayToday === true);
    mark(habit.id, true);
    setProblem(null);
    setReward(habit.id, checkIn ? readReward(reduceMotion) : null);
    const refetching = queryClient.isFetching({ queryKey: todayKey }) > 0;
    await queryClient.cancelQueries({ queryKey: todayKey });
    updateHabit(habit.id, () => optimistic);
    try {
      const updated =
        nextCount !== null
          ? await api.setDayCount(habit.id, habit.today.date, nextCount)
          : checkIn
            ? await api.setDay(habit.id, habit.today.date, true)
            : await api.clearDay(habit.id, habit.today.date);
      updateHabit(habit.id, () => updated);
      if (checkIn && reaches && auth.me.get()?.offerHabitNote) openNote(habit, true);
      setAnnouncement(
        nextCount === null
          ? checkIn
            ? t('today.announce.checkedIn', { name: habit.name })
            : t('today.announce.cleared', { name: habit.name })
          : nextCount === 0
            ? t('today.announce.allCleared', { name: habit.name })
            : t(reaches ? 'today.announce.countDone' : 'today.announce.count', {
                name: habit.name,
                n: nextCount,
                target,
              }),
      );
    } catch (error) {
      updateHabit(habit.id, (current) => (current === optimistic ? habit : current));
      setReward(habit.id, null);
      const apiError = asApiError(error);
      if (!apiError.handled) {
        setProblem({
          heading: checkIn
            ? t('today.problem.checkIn', { name: habit.name })
            : t('today.problem.change', { name: habit.name }),
          message: problemToMessage(apiError).message,
        });
      }
    } finally {
      inFlight.current.delete(habit.id);
      mark(habit.id, false);
      if (refetching) void queryClient.invalidateQueries({ queryKey: todayKey });
    }
  }

  function openNote(habit: { id: number; name: string }, afterCheckIn = false) {
    setNote((current) => ({ habit: { id: habit.id, name: habit.name }, afterCheckIn, session: (current?.session ?? 0) + 1 }));
    setNoteOpen(true);
  }

  function adoptOrder(next: string[] | null) {
    const current = auth.me.get();
    if (current) auth.setMe({ ...current, todayOrder: next });
  }

  /**
   * Saves the full account order (null: by time again) after showing it, one request at a time; a failure puts back the
   * last order the server confirmed.
   */
  function saveOrder(next: string[] | null, announcement: string) {
    const seq = ++orderSeq.current;
    if (ordersSaving.current++ === 0) confirmedOrder.current = auth.me.get()?.todayOrder ?? null;
    adoptOrder(next);
    setProblem(null);
    setAnnouncement(announcement);
    orderChain.current = orderChain.current.then(async () => {
      try {
        const kept = await api.setTodayOrder(next);
        confirmedOrder.current = kept;
        if (seq === orderSeq.current) adoptOrder(kept);
      } catch (error) {
        if (seq !== orderSeq.current) return;
        adoptOrder(confirmedOrder.current);
        const apiError = asApiError(error);
        if (!apiError.handled) setProblem({ heading: t('today.order.failed'), message: problemToMessage(apiError).message });
      } finally {
        ordersSaving.current--;
      }
    });
  }

  /**
   * Saves a routine's full `habitIds` after its shown members were rearranged, one request at a time, and hands the
   * saved routine to the Routines screens; a failure puts them back.
   */
  function saveRoutineOrder(routine: Routine, shown: number[], announcement: string) {
    const habitIds = routineOrderAfterMove(routine.habitIds, shown);
    const moved = { ...routine, habitIds };
    const put = (from: Routine, to: Routine) =>
      setData((d) => ({ ...d, routines: d.routines?.map((r) => (r.id === from.id && r === from ? to : r)) ?? null }));
    put(routine, moved);
    setProblem(null);
    setAnnouncement(announcement);
    routineChain.current = routineChain.current.then(async () => {
      try {
        const saved = await api.updateRoutine(routine.id, { habitIds });
        put(moved, saved);
        queryClient.setQueryData<Routine[]>(routinesKey, (list) => list?.map((r) => (r.id === saved.id ? saved : r)));
      } catch (error) {
        put(moved, routine);
        const apiError = asApiError(error);
        if (!apiError.handled) setProblem({ heading: t('today.order.failed'), message: problemToMessage(apiError).message });
      }
    });
  }

  async function toggleLowDay() {
    if (!today || !data || lowDayBusy) return;
    const marking = !data.lowDayToday;
    setLowDayBusy(true);
    setProblem(null);
    try {
      if (marking) await api.markLowDay(today);
      else await api.clearLowDay(today);
      focusAfter.current = marking ? 'clear-low-day' : 'mark-low-day';
      setData((d) => ({ ...d, lowDayToday: marking }));
      setAnnouncement(marking ? t('today.lowDay.marked') : t('today.lowDay.cleared'));
      const habits = await api.listHabits().catch(() => null);
      if (habits) setData((d) => ({ ...d, habits: buildHabits(habits), letGo: habits.filter(isLetGo) }));
    } catch (error) {
      const apiError = asApiError(error);
      if (!apiError.handled) {
        setProblem({
          heading: marking ? t('today.lowDay.markFailed') : t('today.lowDay.clearFailed'),
          message: problemToMessage(apiError).message,
        });
      }
    } finally {
      setLowDayBusy(false);
    }
  }

  const doneCount = data?.habits.filter((h) => h.today.state === 'done').length ?? 0;
  const checkedInCount = data?.habits.filter((h) => h.today.state === 'done' || h.today.state === 'partial').length ?? 0;
  const lowDay = data?.lowDayToday === true;
  const allDone =
    doneCount > 0 &&
    !!data?.habits.every(
      (h) =>
        ['done', 'offday', 'lowday', 'paused', 'beforestart'].includes(h.today.state) ||
        h.pausedToday ||
        (h.frequency === 'weekly' && h.today.state === 'pending'),
    );
  const oldestStart = data?.habits.reduce<LocalDateString | null>(
    (min, h) => (min === null || h.startDate < min ? h.startDate : min),
    null,
  );
  const showWeekly =
    !!data?.weekly && data.weekly.totalPossible > 0 && !!today && !!oldestStart && oldestStart <= addDays(today, -7);
  const remindersUnheard = reach === 'none' && !!data?.habits.some((h) => h.reminderTime);
  const ember = (className: string) => (
    <TodaysEmber
      count={checkedInCount}
      lowDay={lowDay}
      allDone={allDone}
      stepping={rewards.size > 0}
      className={className}
    />
  );
  const hasHabits = !!data && data.habits.length > 0;
  const planDate = today ?? data?.habits[0]?.today.date ?? data?.letGo[0]?.today.date ?? '';
  const plan = data ? planToday([...data.habits, ...data.letGo], data.routines, order, planDate) : null;
  const sections = plan
    ? [plan.today.map((item) => item.key), plan.letGo.map((h) => habitKey(h.id)), plan.notToday.map((item) => item.key)]
    : [[], [], []];
  const nameOf = (list: string, key: string): string => {
    if (key.startsWith('r:')) return routineTitle(data?.routines?.find((r) => routineKey(r.id) === key)?.name ?? '');
    const id = list.startsWith('routine-') ? Number(key) : Number(key.slice(2));
    return [...(data?.habits ?? []), ...(data?.letGo ?? [])].find((h) => h.id === id)?.name ?? '';
  };
  const moveSection = (index: 0 | 1, next: string[], announcement: string) =>
    saveOrder(orderAfterMove(order, sections, index, next), announcement);
  const reorder = useReorder({
    onLift(list, key) {
      pulse();
      setAnnouncement(t('today.order.moving', { name: nameOf(list, key) }));
    },
    onDrop(list, next) {
      const dropped = t('today.order.dropped');
      if (list === 'today') moveSection(0, next, dropped);
      else if (list === 'letGo') moveSection(1, next, dropped);
      else {
        const routine = data?.routines?.find((r) => `routine-${r.id}` === list);
        if (routine) saveRoutineOrder(routine, next.map(Number), dropped);
      }
    },
    onCancel: () => setAnnouncement(t('today.order.cancelled')),
  });
  const moving = reorder.moving;
  const liftClass = (list: string, key: string) =>
    moving?.list === list && moving.key === key
      ? cn(
          'relative z-10 rounded-xl ring-2 ring-primary shadow-[0_10px_28px_color-mix(in_oklch,var(--foreground)_20%,transparent)]',
          !reduceMotion && !calm && 'scale-[1.02] motion-safe:transition-transform motion-safe:duration-150',
        )
      : undefined;
  const listClass = 'flex select-none flex-col gap-3 [-webkit-touch-callout:none]';
  const movedText = (name: string, by: -1 | 1) =>
    t(by < 0 ? 'today.order.movedUp' : 'today.order.movedDown', { name });
  const sectionMove = (index: 0 | 1, keys: string[], key: string, name: string) => ({
    up: keys.indexOf(key) > 0,
    down: keys.indexOf(key) < keys.length - 1,
    onMove: (by: -1 | 1) => moveSection(index, moveKey(keys, key, by), movedText(name, by)),
  });
  const habitRow = (
    habit: Habit,
    headingLevel: 'h3' | 'h4',
    extras: { list?: string; reorderKey?: string; menu: ReactNode; notToday?: NotTodayReason; checkInPaused?: boolean },
  ) => (
    <HabitRow
      key={habit.id}
      habit={habit}
      headingLevel={headingLevel}
      showStreaks={showStreaks}
      busy={busy.has(habit.id)}
      highlight={highlighted === habit.id}
      reward={rewards.get(habit.id) ?? null}
      onToggle={() => void toggle(habit)}
      onUndoOne={() => void toggle(habit, true)}
      onRewardEnd={() => setReward(habit.id, null)}
      menu={extras.menu}
      notToday={extras.notToday}
      checkInPaused={extras.checkInPaused}
      reorderKey={extras.reorderKey}
      grip={!!extras.list && moving?.list === extras.list}
      className={extras.list && extras.reorderKey ? liftClass(extras.list, extras.reorderKey) : undefined}
    />
  );
  const toggleRoutine = (id: number, open: boolean) => {
    rememberRoutineOpen(id, open);
    setRoutinesOpen((current) => {
      const next = new Set(current);
      if (open) next.add(id);
      else next.delete(id);
      return next;
    });
  };
  const routineBlock = (item: Extract<TodayItem, { kind: 'routine' }>, keys: string[]) => {
    const { routine } = item;
    const list = `routine-${routine.id}`;
    const ids = item.habits.map((h) => String(h.id));
    const title = routineTitle(routine.name);
    const open = routinesOpen.has(routine.id);
    const membersId = `routine-habits-${routine.id}`;
    return (
      <li key={item.key} data-reorder-key={item.key} className={liftClass('today', item.key)}>
        <section aria-labelledby={routineHeadingId(routine.id)} className="flex flex-col gap-3">
          <RoutineRow
            routineId={routine.id}
            name={routine.name}
            title={title}
            habits={item.habits}
            date={planDate}
            lowDay={lowDay}
            open={open}
            onToggle={() => toggleRoutine(routine.id, !open)}
            controls={membersId}
            menu={<RowMenu name={title} move={sectionMove(0, keys, item.key, title)} onNote={null} />}
          />
          <ul
            id={membersId}
            role="list"
            hidden={!open}
            data-routine-id={routine.id}
            className={cn(listClass, 'ms-2 border-s-2 border-border ps-3')}
            {...reorder.listProps(list, ids)}
          >
            {reorder.orderOf(list, ids).map((id) => {
              const habit = item.habits.find((h) => String(h.id) === id)!;
              const onMove = (by: -1 | 1) =>
                saveRoutineOrder(routine, moveKey(ids, id, by).map(Number), movedText(habit.name, by));
              return habitRow(habit, 'h4', {
                list,
                reorderKey: id,
                menu: (
                  <RowMenu
                    name={habit.name}
                    move={{ up: ids.indexOf(id) > 0, down: ids.indexOf(id) < ids.length - 1, onMove }}
                    onNote={() => openNote(habit)}
                  />
                ),
              });
            })}
          </ul>
        </section>
      </li>
    );
  };

  const top = (
    <>
      <MomentDoor variant="today" />
      <LiveRegion message={announcement} />
      {problem && (
        <Notice tone="notice" heading={problem.heading}>
          <p>{problem.message}</p>
        </Notice>
      )}
      <DeviceNotices />
      <FiresideToday />
      <WhatsNewCard />
      <TipSlot place="top" habits={data?.habits} routines={data?.routines} />
    </>
  );

  let body: ReactNode;
  if (!data) {
    const loadProblem = query.isError && !query.isFetching ? asApiError(query.error) : null;
    body = (
      <div className={columns}>
        <div className="flex min-w-0 flex-col gap-6">
          {top}
          {loadProblem ? (
            !loadProblem.handled && (
              <Notice tone="notice" heading={t('today.loadFailed')}>
                <p>{problemToMessage(loadProblem).message}</p>
                <Button variant="secondary" onClick={() => void refetch()}>
                  {t('today.tryAgain')}
                </Button>
              </Notice>
            )
          ) : (
            <div aria-busy="true" className="flex flex-col gap-3">
              <span role="status" className="sr-only">
                {t('today.loading')}
              </span>
              {[1, 2, 3].map((i) => (
                <Skeleton key={i} className="h-[88px] rounded-xl" />
              ))}
            </div>
          )}
        </div>
      </div>
    );
  } else if (!hasHabits && !data.letGo.length) {
    body = (
      <div className={columns}>
        <div className="flex min-w-0 flex-col gap-6">
          {top}
          <Card className="items-center">
            <EmptyState
              illustration={<HearthIllustration />}
              title={t('today.empty.title')}
              action={
                <>
                  <Button asChild>
                    <Link to="/habits/new">{t('today.empty.create')}</Link>
                  </Button>
                  <p className="text-[0.875rem] text-muted-foreground">{t('today.empty.never')}</p>
                </>
              }
            >
              <p>{t('today.empty.body')}</p>
            </EmptyState>
          </Card>
        </div>
        <div className="flex flex-col gap-6">
          {today && <DayJournal date={today} />}
          <TipSlot place="journal" habits={data.habits} routines={data.routines} />
        </div>
      </div>
    );
  } else {
    body = (
      <div className={columns}>
        <div className="flex min-w-0 flex-col gap-6">
          {top}
          {lowDay && (
            <Notice tone="lowday" heading={t('today.lowDay.heading')}>
              <p>{t('today.lowDay.body')}</p>
              <Button
                id="clear-low-day"
                variant="ghost"
                aria-disabled={lowDayBusy || undefined}
                onClick={() => void toggleLowDay()}
              >
                {t('today.lowDay.clear')}
              </Button>
            </Notice>
          )}
          {yesterdayQuery.data === true && (
            <p className="flex flex-wrap items-center gap-x-2 text-muted-foreground">
              <CalendarDays aria-hidden className="size-[18px] shrink-0" />
              {t('today.yesterdayOpen')}
              <TapLink to="/history">{t('today.openHistory')}</TapLink>
            </p>
          )}
          {moving && (
            <p className="fixed inset-x-4 top-[calc(env(safe-area-inset-top)+0.5rem)] z-50 mx-auto max-w-[32rem] rounded-lg border-[length:max(1px,var(--border-width))] border-border bg-card px-4 py-3 text-card-foreground shadow-card">
              {t('today.order.moving', { name: nameOf(moving.list, moving.key) })}
            </p>
          )}
          {plan && hasHabits && (
            <section aria-labelledby="for-today-heading" className="flex flex-col gap-3">
              <div className="flex min-h-[44px] items-center justify-between gap-2">
                <h2 id="for-today-heading" tabIndex={-1} className="text-[1.25rem] focus:outline-none">
                  {t('today.forToday')}
                </h2>
                {order && (
                  <ListMenu
                    onSortByTime={() => {
                      focusAfter.current = 'for-today-heading';
                      saveOrder(null, t('today.order.byTime'));
                    }}
                  />
                )}
              </div>
              {plan.today.length ? (
                <ul role="list" className={listClass} {...reorder.listProps('today', sections[0])}>
                  {reorder.orderOf('today', sections[0]).map((key) => {
                    const item = plan.today.find((i) => i.key === key)!;
                    if (item.kind === 'routine') return routineBlock(item, sections[0]);
                    return habitRow(item.habit, 'h3', {
                      list: 'today',
                      reorderKey: key,
                      menu: (
                        <RowMenu
                          name={item.habit.name}
                          move={sectionMove(0, sections[0], key, item.habit.name)}
                          onNote={() => openNote(item.habit)}
                        />
                      ),
                    });
                  })}
                </ul>
              ) : (
                <p className="text-muted-foreground">{t('today.nothingToday')}</p>
              )}
            </section>
          )}
          {plan && (
            <LetGoSection
              habits={reorder.orderOf('letGo', sections[1]).map((key) => plan.letGo.find((h) => habitKey(h.id) === key)!)}
              listProps={{ ...reorder.listProps('letGo', sections[1]), className: listClass }}
              extras={(habit) => {
                const key = habitKey(habit.id);
                return {
                  reorderKey: key,
                  className: liftClass('letGo', key),
                  menu: (
                    <RowMenu
                      name={habit.name}
                      move={sectionMove(1, sections[1], key, habit.name)}
                      onNote={() => openNote(habit)}
                    />
                  ),
                };
              }}
            />
          )}
          {plan && plan.notToday.length > 0 && (
            <section aria-labelledby="not-today-heading" className="flex flex-col gap-3">
              <h2 id="not-today-heading" className="text-[1.25rem]">
                <button
                  type="button"
                  aria-expanded={laterOpen}
                  aria-controls="not-today-list"
                  onClick={() => {
                    rememberNotTodayOpen(!laterOpen);
                    setLaterOpen(!laterOpen);
                  }}
                  className="flex min-h-[48px] w-full items-center justify-between gap-2 rounded-md text-left text-muted-foreground hover:bg-accent hover:text-accent-foreground hover:shadow-[-0.5rem_0_var(--color-accent),0.5rem_0_var(--color-accent)]"
                >
                  {t('today.notToday', { count: plan.notToday.length })}
                  <ChevronDown
                    aria-hidden
                    className={cn('size-[20px] shrink-0 motion-safe:transition-transform', laterOpen && 'rotate-180')}
                  />
                </button>
              </h2>
              <ul id="not-today-list" role="list" hidden={!laterOpen} className="flex flex-col gap-3 opacity-85">
                {plan.notToday.map((item) =>
                  habitRow(item.habit, 'h3', {
                    checkInPaused: true,
                    notToday: item.reason,
                    menu: <RowMenu name={item.habit.name} move={null} onNote={() => openNote(item.habit)} />,
                  }),
                )}
              </ul>
            </section>
          )}
          {note && today && (
            <NoteSheet
              key={note.session}
              habit={note.habit}
              date={today}
              open={noteOpen}
              onOpenChange={setNoteOpen}
              afterCheckIn={note.afterCheckIn}
              onSaved={(saved) =>
                setAnnouncement(t(saved ? 'habitNote.saved' : 'habitNote.deleted', { name: note.habit.name }))
              }
            />
          )}
          {data.lowDayToday === false && (
            <Button
              id="mark-low-day"
              variant="ghost"
              className="self-start text-muted-foreground md:-ml-4"
              aria-disabled={lowDayBusy || undefined}
              onClick={() => void toggleLowDay()}
            >
              <Moon aria-hidden />
              {t('today.lowDay.mark')}
            </Button>
          )}
          {remindersUnheard && (
            <Notice tone="info" heading={t('today.unheard.heading')}>
              <p>{t('today.unheard.body')}</p>
              <TapLink to={{ pathname: '/settings', hash: reminderHash }}>{t('today.unheard.link')}</TapLink>
            </Notice>
          )}
          <TipSlot place="below" habits={data.habits} routines={data.routines} />
          <div className="flex flex-col gap-3 lg:flex-row lg:flex-wrap">
            <Button asChild variant="secondary" className="w-full lg:w-auto">
              <Link to="/habits/new">
                <Plus aria-hidden />
                {t('today.newHabit')}
              </Link>
            </Button>
            <Button asChild variant="ghost" className="w-full lg:w-auto">
              <Link to="/routines">
                <ListOrdered aria-hidden />
                {t('today.routines')}
              </Link>
            </Button>
          </div>
        </div>
        <div className="flex flex-col gap-6">
          <Card className="hidden lg:flex">
            {ember('')}
          </Card>
          {showWeekly && data.weekly && (
            <section aria-labelledby="weekly-heading" className={cardVariants()}>
              <h2 id="weekly-heading" className="text-[1.375rem]">
                {t('weekly.heading')}
              </h2>
              <p className="whitespace-pre-line">{weeklyHeadline(data.weekly)}</p>
              {data.weekly.footer && weeklyHeadline(data.weekly) === data.weekly.headline && (
                <p className="whitespace-pre-line text-[0.875rem] text-muted-foreground">{data.weekly.footer}</p>
              )}
            </section>
          )}
          {today && <DayJournal date={today} />}
          <TipSlot place="journal" habits={data.habits} routines={data.routines} />
        </div>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={t('today.title')}
        lede={
          today ? (
            <span className="sm:whitespace-nowrap">
              {formatDate(today, { weekday: 'long', day: 'numeric', month: 'long' })}
            </span>
          ) : undefined
        }
        aside={hasHabits ? ember('lg:hidden') : undefined}
      />
      {body}
    </div>
  );
}
