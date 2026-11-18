import type { FeedbackItem, FeedbackStatus } from '@/api/types';

/** Server order within a column: position ascending (nulls last), then newest first. */
export function compareCards(a: FeedbackItem, b: FeedbackItem): number {
  if (a.position !== null && b.position !== null && a.position !== b.position) return a.position - b.position;
  if (a.position === null && b.position !== null) return 1;
  if (a.position !== null && b.position === null) return -1;
  return Date.parse(b.createdAt) - Date.parse(a.createdAt);
}

export function columnOf(items: FeedbackItem[], status: FeedbackStatus): FeedbackItem[] {
  return items.filter((i) => i.status === status).sort(compareCards);
}

/**
 * The position for a card dropped at `index` of `column` (ordered, without the moved card): halfway between its
 * neighbours. Cards without a position count as following the last positioned one, so a card placed among them
 * lands right after the positioned cards, which is where the server will sort it.
 */
export function positionAt(column: FeedbackItem[], index: number): number {
  let last = 0;
  let k = 0;
  const effective = column.map((c) => {
    if (c.position !== null) return (last = c.position);
    return last + ++k;
  });
  const prev = effective[index - 1];
  const next = effective[index];
  if (prev === undefined && next === undefined) return 0;
  if (prev === undefined) return next! - 1;
  if (next === undefined) return prev + 1;
  return (prev + next) / 2;
}

/**
 * Maps a drop slot `slot` among the cards a filter leaves visible to an index in the full column (both without the
 * moved card): before the visible card at that slot, or right after the last visible card.
 */
export function indexAmong(full: FeedbackItem[], visible: FeedbackItem[], slot: number): number {
  if (visible.length === 0) return 0;
  if (slot < visible.length) return full.indexOf(visible[slot]!);
  return full.indexOf(visible[visible.length - 1]!) + 1;
}

/** The board after moving card `id` to `status` at `index` (in that column without the card). */
export function applyMove(items: FeedbackItem[], id: number, status: FeedbackStatus, index: number) {
  const card = items.find((i) => i.id === id);
  if (!card) return null;
  const target = columnOf(items, status).filter((i) => i.id !== id);
  const position = positionAt(target, Math.max(0, Math.min(index, target.length)));
  const resolved = status === 'released' || status === 'declined';
  const moved: FeedbackItem = {
    ...card,
    status,
    position,
    resolvedAt: resolved ? (card.resolvedAt ?? new Date().toISOString()) : null,
  };
  return { position, items: items.map((i) => (i.id === id ? moved : i)) };
}
