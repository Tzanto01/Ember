import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { type DragEvent, Fragment, useEffect, useRef, useState } from 'react';
import { feedbackQuery, moveFeedback } from '@/api/client';
import { type FeedbackItem, type FeedbackList, type FeedbackStatus, statuses } from '@/api/types';
import { FeedbackCard, type MoveAction } from '@/components/feedback-card';
import { FeedbackDialog } from '@/components/feedback-dialog';
import { Loaded, PageHeader } from '@/components/ui';
import { applyMove, columnOf, indexAmong } from '@/lib/board';
import { capitalize } from '@/lib/format';

const platforms = ['web', 'android', 'discord'] as const;

interface MoveVars {
  id: number;
  status: FeedbackStatus;
  position: number;
  previous: FeedbackList | undefined;
}

export function FeedbackPage() {
  const query = useQuery(feedbackQuery);
  const queryClient = useQueryClient();
  const [hidden, setHidden] = useState<ReadonlySet<FeedbackStatus>>(new Set());
  const [platform, setPlatform] = useState('all');
  const [search, setSearch] = useState('');
  const [openId, setOpenId] = useState<number | null>(null);
  const [menuId, setMenuId] = useState<number | null>(null);
  const [dragId, setDragId] = useState<number | null>(null);
  const [drop, setDrop] = useState<{ status: FeedbackStatus; slot: number } | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [moveError, setMoveError] = useState<string | null>(null);
  const focusAfter = useRef<{ id: number; control: string; status: FeedbackStatus; position: number } | null>(null);

  const move = useMutation({
    mutationFn: ({ id, status, position }: MoveVars) => moveFeedback(id, { status, position }),
    onError: (error, { previous }) => {
      if (previous) queryClient.setQueryData(feedbackQuery.queryKey, previous);
      setMoveError(`Could not move the card, it is back where it was: ${error.message}`);
      setAnnouncement('Move failed, the card is back where it was.');
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: feedbackQuery.queryKey }),
  });

  useEffect(() => {
    const target = focusAfter.current;
    const moved = query.data?.items.find((i) => i.id === target?.id);
    if (!target || moved?.status !== target.status || moved.position !== target.position) return;
    focusAfter.current = null;
    const card = document.querySelector(`[data-card-id="${target.id}"]`);
    const control = card?.querySelector<HTMLButtonElement>(`[data-move="${target.control}"]`);
    (control && !control.disabled ? control : card?.querySelector<HTMLButtonElement>('[data-move="toggle"]'))?.focus();
  });

  const items = query.data?.items ?? [];
  const needle = search.trim().toLowerCase();
  const matches = (i: FeedbackItem) =>
    (platform === 'all' || i.platform.toLowerCase() === platform) &&
    (!needle || i.title.toLowerCase().includes(needle) || i.text.toLowerCase().includes(needle));
  const visible = (status: FeedbackStatus) => columnOf(items, status).filter(matches);

  function moveTo(item: FeedbackItem, status: FeedbackStatus, slot: number, control: string) {
    const full = columnOf(items, status).filter((c) => c.id !== item.id);
    const shown = visible(status).filter((c) => c.id !== item.id);
    const index = indexAmong(full, shown, slot);
    if (status === item.status && columnOf(items, status).indexOf(item) === index) return;
    const result = applyMove(items, item.id, status, index);
    if (!result) return;
    focusAfter.current = { id: item.id, control, status, position: result.position };
    const column = columnOf(result.items, status).filter(matches);
    setAnnouncement(
      `Moved ${item.title} to ${capitalize(status)}, position ${column.findIndex((c) => c.id === item.id) + 1} of ${column.length}.`,
    );
    setMoveError(null);
    const previous = queryClient.getQueryData<FeedbackList>(feedbackQuery.queryKey);
    void queryClient.cancelQueries({ queryKey: feedbackQuery.queryKey });
    queryClient.setQueryData<FeedbackList>(feedbackQuery.queryKey, { items: result.items });
    move.mutate({ id: item.id, status, position: result.position, previous });
  }

  function onMove(item: FeedbackItem, action: MoveAction) {
    const slot = visible(item.status).indexOf(item);
    if (action.kind === 'up') moveTo(item, item.status, slot - 1, 'up');
    else if (action.kind === 'down') moveTo(item, item.status, slot + 1, 'down');
    else {
      setMenuId(null);
      moveTo(item, action.status, 0, 'toggle');
    }
  }

  function onDragOver(e: DragEvent<HTMLElement>, status: FeedbackStatus) {
    if (dragId === null) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    const cards = [...e.currentTarget.querySelectorAll<HTMLElement>('[data-card-id]')].filter(
      (el) => Number(el.dataset['cardId']) !== dragId,
    );
    let slot = cards.findIndex((el) => {
      const r = el.getBoundingClientRect();
      return e.clientY < r.top + r.height / 2;
    });
    if (slot === -1) slot = cards.length;
    if (drop?.status !== status || drop.slot !== slot) setDrop({ status, slot });
  }

  function onDrop(e: DragEvent<HTMLElement>, status: FeedbackStatus) {
    e.preventDefault();
    const item = items.find((i) => i.id === dragId);
    if (item && drop) moveTo(item, status, drop.slot, 'toggle');
    setDragId(null);
    setDrop(null);
  }

  const openItem = items.find((i) => i.id === openId);
  const shownStatuses = statuses.filter((s) => !hidden.has(s));

  return (
    <Loaded query={query} title="Feedback">
      {() => (
        <>
          <PageHeader title="Feedback" at={query.dataUpdatedAt} fetching={query.isFetching} stale={query.isRefetchError} />
          <div className="mb-4 flex flex-wrap items-end gap-x-6 gap-y-3 rounded-lg bg-card p-4 shadow-card">
            <fieldset className="m-0 border-0 p-0">
              <legend className="mb-1 p-0 font-bold">Columns</legend>
              <div className="flex flex-wrap gap-x-4 gap-y-1">
                {statuses.map((s) => (
                  <label key={s} className="inline-flex min-h-10 items-center gap-2">
                    <input
                      type="checkbox"
                      checked={!hidden.has(s)}
                      onChange={(e) => {
                        const next = new Set(hidden);
                        if (e.target.checked) next.delete(s);
                        else next.add(s);
                        setHidden(next);
                      }}
                    />
                    {capitalize(s)}
                  </label>
                ))}
              </div>
            </fieldset>
            <label className="flex flex-col gap-1 font-bold">
              Platform
              <select value={platform} onChange={(e) => setPlatform(e.target.value)} className="font-normal">
                <option value="all">All platforms</option>
                {platforms.map((p) => (
                  <option key={p} value={p}>
                    {capitalize(p)}
                  </option>
                ))}
              </select>
            </label>
            <label className="flex min-w-0 flex-1 basis-56 flex-col gap-1 font-bold">
              Search
              <input type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Title or description" className="font-normal" />
            </label>
          </div>
          <p aria-live="polite" className="sr-only">
            {announcement}
          </p>
          {moveError && (
            <div role="alert" className="mb-4 flex flex-wrap items-center gap-3 rounded-lg bg-notice p-3 text-notice-foreground ring-1 ring-notice-border">
              <span className="flex-1">{moveError}</span>
              <button type="button" className="btn btn-sm" onClick={() => setMoveError(null)}>
                Dismiss
              </button>
            </div>
          )}
          {shownStatuses.length === 0 ? (
            <p className="text-muted-foreground">All columns are hidden.</p>
          ) : (
            <div className="grid grid-cols-[repeat(auto-fit,minmax(15rem,1fr))] items-start gap-3">
              {shownStatuses.map((status) => {
                const cards = visible(status);
                const total = columnOf(items, status).length;
                const isDrop = drop?.status === status;
                let others = 0;
                return (
                  <section
                    key={status}
                    aria-label={capitalize(status)}
                    onDragOver={(e) => onDragOver(e, status)}
                    onDragLeave={(e) => {
                      if (!e.currentTarget.contains(e.relatedTarget as Node | null) && isDrop) setDrop(null);
                    }}
                    onDrop={(e) => onDrop(e, status)}
                    className={`flex min-h-40 flex-col gap-2 rounded-lg bg-muted p-2 ${isDrop ? 'ring-2 ring-primary' : ''}`}
                  >
                    <h2 className="m-0 flex items-baseline justify-between px-1 pt-1 text-base">
                      {capitalize(status)}
                      <span className="text-sm font-semibold text-muted-foreground tabular-nums">
                        {cards.length === total ? total : `${cards.length} of ${total}`}
                      </span>
                    </h2>
                    <ol className="m-0 flex flex-col gap-2 p-0">
                      {cards.map((item) => {
                        const slot = item.id === dragId ? -1 : others++;
                        const index = cards.indexOf(item);
                        return (
                          <Fragment key={item.id}>
                            {isDrop && slot === drop.slot && <li aria-hidden="true" className="h-1 list-none rounded-full bg-primary" />}
                            <FeedbackCard
                              item={item}
                              first={index === 0}
                              last={index === cards.length - 1}
                              menuOpen={menuId === item.id}
                              dragging={dragId === item.id}
                              onToggleMenu={(open) => setMenuId(open ? item.id : null)}
                              onMove={(action) => onMove(item, action)}
                              onOpen={() => setOpenId(item.id)}
                              onDragStart={(e) => {
                                e.dataTransfer.effectAllowed = 'move';
                                e.dataTransfer.setData('text/plain', String(item.id));
                                setDragId(item.id);
                              }}
                              onDragEnd={() => {
                                setDragId(null);
                                setDrop(null);
                              }}
                            />
                          </Fragment>
                        );
                      })}
                      {isDrop && drop.slot === others && <li aria-hidden="true" className="h-1 list-none rounded-full bg-primary" />}
                      {cards.length === 0 && <li className="list-none px-1 text-sm text-muted-foreground">No cards</li>}
                    </ol>
                  </section>
                );
              })}
            </div>
          )}
          {openItem && <FeedbackDialog key={openItem.id} item={openItem} onClose={() => setOpenId(null)} />}
        </>
      )}
    </Loaded>
  );
}
