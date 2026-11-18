import { ArrowDown, ArrowUp, GripVertical, Image, MessageSquare, MoveRight } from 'lucide-react';
import type { DragEvent } from 'react';
import { type FeedbackItem, type FeedbackStatus, statuses } from '@/api/types';
import { Badge } from '@/components/ui';
import { capitalize, formatStamp } from '@/lib/format';

export type MoveAction = { kind: 'status'; status: FeedbackStatus } | { kind: 'up' } | { kind: 'down' };

interface Props {
  item: FeedbackItem;
  first: boolean;
  last: boolean;
  menuOpen: boolean;
  dragging: boolean;
  onToggleMenu: (open: boolean) => void;
  onMove: (action: MoveAction) => void;
  onOpen: () => void;
  onDragStart: (e: DragEvent) => void;
  onDragEnd: () => void;
}

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;

export function FeedbackCard({ item, first, last, menuOpen, dragging, onToggleMenu, onMove, onOpen, onDragStart, onDragEnd }: Props) {
  const titleId = `card-${item.id}-title`;
  const menuId = `card-${item.id}-move`;
  return (
    <li data-card-id={item.id} className="list-none">
      <article
        aria-labelledby={titleId}
        draggable
        onDragStart={onDragStart}
        onDragEnd={onDragEnd}
        className={`flex flex-col gap-2 rounded-md bg-card p-3 shadow-card ${dragging ? 'opacity-50' : ''}`}
      >
        <div className="flex items-start gap-1">
          <GripVertical aria-hidden="true" className="mt-1 size-4 shrink-0 cursor-grab text-muted-foreground" />
          <h3 className="m-0 min-w-0 flex-1 text-base">
            <button
              type="button"
              id={titleId}
              onClick={onOpen}
              className="w-full cursor-pointer rounded-sm text-left font-bold hover:underline"
            >
              {item.title}
            </button>
          </h3>
        </div>
        <div className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
          <Badge>{capitalize(item.platform)}</Badge>
          {item.version && <span>v{item.version}</span>}
          {item.anonymous && <Badge tone="warn">Anonymous</Badge>}
        </div>
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
          <time dateTime={item.createdAt}>{formatStamp(item.createdAt)}</time>
          {item.imageIds.length > 0 && (
            <span className="inline-flex items-center gap-1">
              <Image aria-hidden="true" className="size-3.5" />
              {plural(item.imageIds.length, 'image')}
            </span>
          )}
          <span className="inline-flex items-center gap-1">
            <MessageSquare aria-hidden="true" className="size-3.5" />
            {plural(item.commentCount, 'comment')}
          </span>
        </div>
        <div>
          <button
            type="button"
            className="btn btn-sm"
            aria-expanded={menuOpen}
            aria-controls={menuId}
            data-move="toggle"
            onClick={() => onToggleMenu(!menuOpen)}
          >
            <MoveRight aria-hidden="true" className="size-4" />
            Move
          </button>
          {menuOpen && (
            <div
              id={menuId}
              role="group"
              aria-label={`Move ${item.title}`}
              className="mt-2 flex flex-wrap gap-1.5"
              onKeyDown={(e) => {
                if (e.key !== 'Escape') return;
                e.stopPropagation();
                onToggleMenu(false);
              }}
            >
              <button type="button" className="btn btn-sm" data-move="up" disabled={first} onClick={() => onMove({ kind: 'up' })}>
                <ArrowUp aria-hidden="true" className="size-4" />
                Move up
              </button>
              <button type="button" className="btn btn-sm" data-move="down" disabled={last} onClick={() => onMove({ kind: 'down' })}>
                <ArrowDown aria-hidden="true" className="size-4" />
                Move down
              </button>
              {statuses
                .filter((s) => s !== item.status)
                .map((s) => (
                  <button key={s} type="button" className="btn btn-sm" onClick={() => onMove({ kind: 'status', status: s })}>
                    Move to {capitalize(s)}
                  </button>
                ))}
            </div>
          )}
        </div>
      </article>
    </li>
  );
}
