import {
  type MouseEvent as ReactMouseEvent,
  type PointerEvent as ReactPointerEvent,
  useEffect,
  useRef,
  useState,
} from 'react';

/** How long a row is held before it lifts, and how far the pointer may wander before that. */
export const holdMs = 500;
const slop = 8;
const edge = 64;

/** The row being moved: its list, its key and the order the list would get if it were let go now. */
export interface Moving {
  list: string;
  key: string;
  order: string[];
}

interface Pending {
  pointerId: number;
  x: number;
  y: number;
  list: string;
  key: string;
  keys: string[];
  container: HTMLElement;
  timer: ReturnType<typeof setTimeout>;
}

interface Lifted {
  pointerId: number;
  list: string;
  key: string;
  keys: string[];
  container: HTMLElement;
  /** Page-y midpoints of the other rows, in their original order. */
  others: { key: string; mid: number }[];
  order: string[];
}

export interface ReorderOptions {
  /** Called once a row lifts (after the hold): buzz, announce. */
  onLift: (list: string, key: string) => void;
  /** Called on let go with the list's new order, only when it changed. */
  onDrop: (list: string, order: string[]) => void;
  /** Called when a move ends without a drop (Escape, the system took the pointer). */
  onCancel?: () => void;
}

/** `order` narrowed to the list's current `keys`, with keys that came up meanwhile at the end. */
function fit(order: readonly string[], keys: readonly string[]): string[] {
  const kept = order.filter((key) => keys.includes(key));
  return [...kept, ...keys.filter((key) => !kept.includes(key))];
}

const swallowClick = (event: Event) => {
  event.stopPropagation();
  event.preventDefault();
};

/**
 * Hold-and-drag rearranging for Today's lists. Spread `listProps(list, keys)` on each `<ul>` (rows are its direct
 * children with `data-reorder-key`); a nested list handles its own rows. A row lifts after {@link holdMs} without
 * moving, then follows the pointer through the list as a preview (`orderOf`) and lands where it is let go. While a row
 * is lifted the page doesn't scroll by touch (it scrolls near the screen edges instead), Escape cancels, and the click
 * that ends the gesture is swallowed.
 */
export function useReorder({ onLift, onDrop, onCancel }: ReorderOptions) {
  const [moving, setMoving] = useState<Moving | null>(null);
  const pending = useRef<Pending | null>(null);
  const lifted = useRef<Lifted | null>(null);
  const latest = useRef(new Map<string, string[]>());
  const callbacks = useRef({ onLift, onDrop, onCancel });
  callbacks.current = { onLift, onDrop, onCancel };

  useEffect(() => {
    const keep = (event: TouchEvent) => {
      if (lifted.current && event.cancelable) event.preventDefault();
    };
    document.addEventListener('touchmove', keep, { passive: false });
    return () => document.removeEventListener('touchmove', keep);
  }, []);

  useEffect(() => {
    if (!moving) return;
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      end(false, false);
    };
    window.addEventListener('keydown', escape);
    return () => window.removeEventListener('keydown', escape);
  }, [moving !== null]);

  useEffect(() => () => clearPending(), []);

  function clearPending(): void {
    if (pending.current) clearTimeout(pending.current.timer);
    pending.current = null;
  }

  function lift(): void {
    const start = pending.current;
    pending.current = null;
    if (!start) return;
    const rows = [...start.container.children].filter(
      (el): el is HTMLElement => el instanceof HTMLElement && !!el.dataset['reorderKey'],
    );
    const others = rows
      .filter((row) => row.dataset['reorderKey'] !== start.key)
      .map((row) => {
        const rect = row.getBoundingClientRect();
        return {
          key: row.dataset['reorderKey']!,
          mid: rect.top + rect.height / 2 + window.scrollY,
        };
      });
    try {
      start.container.setPointerCapture(start.pointerId);
    } catch {}
    start.container.style.touchAction = 'none';
    lifted.current = {
      pointerId: start.pointerId,
      list: start.list,
      key: start.key,
      keys: start.keys,
      container: start.container,
      others,
      order: start.keys,
    };
    setMoving({ list: start.list, key: start.key, order: start.keys });
    callbacks.current.onLift(start.list, start.key);
  }

  /** Ends a move; `released` is false while the pointer is still down (Escape), so the click guard waits for it. */
  function end(drop: boolean, released: boolean): void {
    const current = lifted.current;
    lifted.current = null;
    if (!current) return;
    try {
      current.container.releasePointerCapture(current.pointerId);
    } catch {}
    current.container.style.touchAction = '';
    setMoving(null);
    window.addEventListener('click', swallowClick, { capture: true, once: true });
    const unguard = () => setTimeout(() => window.removeEventListener('click', swallowClick, { capture: true }), 400);
    if (released) unguard();
    else window.addEventListener('pointerup', unguard, { capture: true, once: true });
    const keys = latest.current.get(current.list) ?? current.keys;
    const order = fit(current.order, keys);
    if (!drop) callbacks.current.onCancel?.();
    else if (order.some((key, i) => key !== keys[i])) callbacks.current.onDrop(current.list, order);
  }

  function listProps(list: string, keys: string[]) {
    latest.current.set(list, keys);
    return {
      onPointerDown(event: ReactPointerEvent<HTMLElement>) {
        if (event.button !== 0 || lifted.current) return;
        const row = (event.target as Element).closest<HTMLElement>('[data-reorder-key]');
        if (!row || row.parentElement !== event.currentTarget) return;
        clearPending();
        pending.current = {
          pointerId: event.pointerId,
          x: event.clientX,
          y: event.clientY,
          list,
          key: row.dataset['reorderKey']!,
          keys,
          container: event.currentTarget,
          timer: setTimeout(lift, holdMs),
        };
      },
      onPointerMove(event: ReactPointerEvent<HTMLElement>) {
        const start = pending.current;
        if (
          start &&
          start.pointerId === event.pointerId &&
          Math.hypot(event.clientX - start.x, event.clientY - start.y) > slop
        ) {
          clearPending();
          return;
        }
        const current = lifted.current;
        if (!current || current.pointerId !== event.pointerId || current.list !== list) return;
        if (event.clientY < edge) window.scrollBy(0, -12);
        else if (event.clientY > window.innerHeight - edge) window.scrollBy(0, 12);
        const y = event.clientY + window.scrollY;
        const index = current.others.filter((other) => other.mid < y).length;
        const order = current.others.map((other) => other.key);
        order.splice(index, 0, current.key);
        current.order = order;
        setMoving((state) =>
          state && state.order.join() !== order.join() ? { ...state, order } : state,
        );
      },
      onPointerUp(event: ReactPointerEvent<HTMLElement>) {
        if (pending.current?.pointerId === event.pointerId) clearPending();
        if (lifted.current?.pointerId === event.pointerId && lifted.current.list === list)
          end(true, true);
      },
      onPointerCancel(event: ReactPointerEvent<HTMLElement>) {
        if (pending.current?.pointerId === event.pointerId) clearPending();
        if (lifted.current?.pointerId === event.pointerId && lifted.current.list === list)
          end(false, true);
      },
      onLostPointerCapture(event: ReactPointerEvent<HTMLElement>) {
        if (lifted.current?.pointerId === event.pointerId && lifted.current.list === list)
          end(false, true);
      },
      onContextMenu(event: ReactMouseEvent<HTMLElement>) {
        if (pending.current || lifted.current) event.preventDefault();
      },
    };
  }

  /** `keys` in the order to show now: the preview while a row of `list` is moving. */
  function orderOf(list: string, keys: string[]): string[] {
    return moving?.list === list ? fit(moving.order, keys) : keys;
  }

  return { moving, listProps, orderOf };
}
