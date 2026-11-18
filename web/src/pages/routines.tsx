import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ArrowDown, ArrowUp, Bell, BellOff, Plus } from 'lucide-react';
import { type ReactNode, useId, useState } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { EmptyState } from '@/components/empty-state';
import { BackLink } from '@/components/back-link';
import { LiveRegion } from '@/components/live-region';
import { PageHeader } from '@/components/page-header';
import { routineHabitsKey, routinesKey } from '@/components/routines/routine-sections';
import { routineTitle } from '@/components/routines/routine-title';
import { HearthIllustration } from '@/components/today/hearth-illustration';
import { Button } from '@/components/ui/button';
import { Card, cardVariants } from '@/components/ui/card';
import { Notice } from '@/components/ui/notice';
import { Skeleton } from '@/components/ui/skeleton';
import { buildHabits, type Routine } from '@/lib/models';
import { asApiError, problemToMessage } from '@/lib/problem';
import { api } from '@/lib/services';

const routineLimit = 10;

/**
 * `/routines`: the user's routines in their order with Move up and Move down (saved right away, reloaded from the server when saving fails),
 * their habits in order and reminder, links to edit, and "New routine".
 */
export default function RoutinesPage() {
  const { t } = useTranslation();
  const id = useId();
  const queryClient = useQueryClient();
  const [announcement, setAnnouncement] = useState('');
  const [orderProblem, setOrderProblem] = useState<string | null>(null);
  const routines = useQuery({ queryKey: routinesKey, queryFn: () => api.listRoutines() });
  const habits = useQuery({ queryKey: routineHabitsKey, queryFn: async () => buildHabits(await api.listHabits()) });
  const reorder = useMutation({
    mutationFn: (ids: number[]) => api.setRoutineOrder(ids),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['today'] }),
  });
  const names = new Map((habits.data ?? []).map((h) => [h.id, h.name]));
  const list = routines.data;
  const atLimit = (list?.length ?? 0) >= routineLimit;

  function move(current: Routine[], index: number, step: -1 | 1): void {
    const target = index + step;
    if (target < 0 || target >= current.length) return;
    const next = [...current];
    [next[index], next[target]] = [next[target], next[index]];
    const routine = current[index];
    flushSync(() => {
      queryClient.setQueryData<Routine[]>(routinesKey, next);
      setOrderProblem(null);
      setAnnouncement(t('routine.habits.moved', { name: routineTitle(routine.name), n: target + 1, total: current.length }));
    });
    document.getElementById(`${id}-${step < 0 ? 'up' : 'down'}-${routine.id}`)?.focus();
    reorder.mutate(
      next.map((r) => r.id),
      {
        onError: (error) => {
          void queryClient.invalidateQueries({ queryKey: routinesKey });
          const apiError = asApiError(error);
          if (!apiError.handled) setOrderProblem(problemToMessage(apiError).message);
        },
      },
    );
  }

  const newButton = atLimit ? (
    <p className="text-[0.875rem] text-muted-foreground">
      {t('routines.limit')}
    </p>
  ) : (
    <Button asChild variant={list?.length ? 'secondary' : 'default'} className={list?.length ? 'w-full sm:w-auto sm:self-start' : 'w-full sm:w-auto'}>
      <Link to="/routines/new">
        <Plus aria-hidden />
        {t('routines.new')}
      </Link>
    </Button>
  );

  let content: ReactNode;
  if (!list) {
    const error = routines.isError && !routines.isFetching ? asApiError(routines.error) : null;
    content = error ? (
      !error.handled && (
        <Notice tone="notice" heading={t('routines.loadFailed')}>
          <p>{problemToMessage(error).message}</p>
          <Button variant="secondary" onClick={() => void routines.refetch()}>
            {t('routines.tryAgain')}
          </Button>
        </Notice>
      )
    ) : (
      <div aria-busy="true" className="flex flex-col gap-3">
        <span role="status" className="sr-only">
          {t('routines.loading')}
        </span>
        {[1, 2].map((i) => (
          <Skeleton key={i} className="h-[112px] rounded-xl" />
        ))}
      </div>
    );
  } else if (list.length === 0) {
    content = (
      <Card className="items-center">
        <EmptyState
          illustration={<HearthIllustration />}
          title={t('routines.empty.title')}
          action={newButton}
        >
          <p>{t('routines.empty.body')}</p>
        </EmptyState>
      </Card>
    );
  } else {
    content = (
      <>
        <LiveRegion message={announcement} />
        {orderProblem && (
          <Notice tone="notice" heading={t('routines.orderFailed')}>
            <p>{orderProblem}</p>
          </Notice>
        )}
        <ul role="list" className="flex flex-col gap-3">
          {list.map((routine, index) => {
            const members = routine.habitIds.flatMap((id) => names.get(id) ?? []);
            return (
              <li key={routine.id} className={cardVariants({ className: 'gap-2' })}>
                <div className="flex items-start justify-between gap-3">
                  <h2 className="min-w-0 text-[1.25rem] break-words">{routine.name}</h2>
                  <div className="flex shrink-0 items-center gap-1">
                    <Button
                      id={`${id}-up-${routine.id}`}
                      type="button"
                      variant="ghost"
                      size="icon"
                      aria-label={t('routine.habits.up', { name: routineTitle(routine.name) })}
                      aria-disabled={index === 0 || undefined}
                      onClick={() => move(list, index, -1)}
                    >
                      <ArrowUp aria-hidden />
                    </Button>
                    <Button
                      id={`${id}-down-${routine.id}`}
                      type="button"
                      variant="ghost"
                      size="icon"
                      aria-label={t('routine.habits.down', { name: routineTitle(routine.name) })}
                      aria-disabled={index === list.length - 1 || undefined}
                      onClick={() => move(list, index, 1)}
                    >
                      <ArrowDown aria-hidden />
                    </Button>
                    <Button asChild variant="secondary">
                      <Link to={`/routines/${routine.id}/edit`} aria-label={t('today.edit', { name: routineTitle(routine.name) })}>
                        {t('routines.edit')}
                      </Link>
                    </Button>
                  </div>
                </div>
                <p className="inline-flex items-center gap-1.5 text-[0.875rem] text-muted-foreground">
                  {routine.reminderTime ? (
                    <>
                      <Bell aria-hidden className="size-[14px] shrink-0" />
                      {t('routines.reminderAt', { time: routine.reminderTime })}
                    </>
                  ) : (
                    <>
                      <BellOff aria-hidden className="size-[14px] shrink-0" />
                      {t('routines.noReminder')}
                    </>
                  )}
                </p>
                {routine.habitIds.length === 0 ? (
                  <p className="text-[0.875rem] text-muted-foreground">{t('routines.noHabits')}</p>
                ) : members.length > 0 ? (
                  <ol aria-label={t('routines.habitsIn', { name: routine.name })} className="flex flex-wrap gap-2">
                    {members.map((name, i) => (
                      <li key={`${i}-${name}`} className="min-w-0 rounded-full bg-secondary px-3 py-1 text-[0.875rem] text-secondary-foreground [overflow-wrap:anywhere]">
                        {name}
                      </li>
                    ))}
                  </ol>
                ) : (
                  <p className="text-[0.875rem] text-muted-foreground">
                    {t('routines.habitCount', { count: routine.habitIds.length })}
                  </p>
                )}
              </li>
            );
          })}
        </ul>
        {newButton}
      </>
    );
  }

  return (
    <div className="flex max-w-[40rem] flex-col gap-6 2xl:mx-auto 2xl:w-full">
      <BackLink to="/today" label={t('title.today')} name={t('habitEdit.backToToday')} />
      <PageHeader
        title={t('routines.title')}
        lede={t('routines.lede')}
      />
      {content}
    </div>
  );
}
