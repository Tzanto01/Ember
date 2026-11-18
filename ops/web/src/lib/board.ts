import type { FeedbackItem, FeedbackStatus } from '@/api/types';

/** Server order within a column: position ascending, then newest first. */
export function compareCards(a: FeedbackItem, b: FeedbackItem): number {
  return a.position - b.position || Date.parse(b.createdAt) - Date.parse(a.createdAt);
}

export function columnOf(items: FeedbackItem[], status: FeedbackStatus): FeedbackItem[] {
  return items.filter((i) => i.status === status).sort(compareCards);
}

/** The position for a card dropped at `index` of `column` (ordered, without the moved card): halfway between its neighbours. */
export function positionAt(column: FeedbackItem[], index: number): number {
  const prev = column[index - 1]?.position;
  const next = column[index]?.position;
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
