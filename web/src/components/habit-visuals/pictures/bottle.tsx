import { type KeyboardEvent, type PointerEvent, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { holdMs } from '@/components/today/use-reorder';
import { Button } from '@/components/ui/button';
import { clampCount, holdChoices } from '@/lib/habit-visuals/progress';
import {
  amountText,
  PictureButton,
  type PictureProps,
  ProgressText,
  tapHandler,
  useAmounts,
  usePictureText,
  useSvgId,
} from './shared';

const body =
  'M28 20h24v8c0 6 14 10 14 24v74c0 6-4 10-10 10H24c-6 0-10-4-10-10V52c0-14 14-18 14-24z';
const slop = 8;

/** The bottle drawing at `count` of `target`; the water rises one tick per check-in. */
function BottleDrawing({ count, target }: { count: number; target: number }) {
  const clip = useSvgId();
  const step = 102 / target;
  return (
    <svg viewBox="0 0 80 140" aria-hidden="true" className="h-16 w-auto">
      <defs>
        <clipPath id={clip}>
          <path d={body} />
        </clipPath>
      </defs>
      <path d={body} fill="var(--picture-glass)" />
      <g clipPath={`url(#${clip})`}>
        <g
          className="picture-move"
          style={{ transform: `translateY(${(target - count) * step}px)` }}
        >
          <rect x="0" y="34" width="80" height="110" fill="var(--picture-water)" />
          <rect x="0" y="34" width="80" height="5" fill="var(--picture-water-top)" />
        </g>
      </g>
      {Array.from({ length: target - 1 }, (_, i) => (
        <line
          key={i}
          x1="56"
          x2="64"
          y1={136 - (i + 1) * step}
          y2={136 - (i + 1) * step}
          stroke="var(--picture-water)"
          strokeWidth="3"
          strokeLinecap="round"
        />
      ))}
      <path
        d={body}
        fill="none"
        stroke="var(--picture-water)"
        strokeWidth="5"
        strokeLinejoin="round"
      />
      <rect x="29" y="5" width="22" height="13" rx="3" fill="var(--primary)" />
    </svg>
  );
}

/**
 * The bottle: a tap adds one portion; pressing and holding the bottle (about 500 ms) or the "More" button next to it
 * opens a small picker of +1, +2 and +3 portions that still fit today. Escape or Close shuts it and returns focus.
 */
export function Bottle(props: PictureProps) {
  const { habit, count, busy, onSetCount, animate } = props;
  const { t } = useTranslation();
  const amounts = useAmounts();
  const { progress, action, full } = usePictureText(habit, count);
  const target = habit.timesPerDay;
  const [opener, setOpener] = useState<'bottle' | 'more' | null>(null);
  const bottle = useRef<HTMLButtonElement>(null);
  const more = useRef<HTMLButtonElement>(null);
  const first = useRef<HTMLButtonElement>(null);
  const hold = useRef<{ timer: ReturnType<typeof setTimeout>; x: number; y: number } | null>(null);
  const held = useRef(false);
  const choices = holdChoices(count, target);
  const tap = tapHandler(props);
  const label = t('habitVisuals.today.bottle.label', {
    name: habit.name,
    done: count,
    count: target,
    action,
  });

  useEffect(() => {
    if (opener) first.current?.focus();
  }, [opener]);
  useEffect(() => () => clearTimeout(hold.current?.timer), []);

  function openPicker(from: 'bottle' | 'more'): void {
    if (busy || full) return;
    setOpener(from);
  }

  function close(): void {
    const from = opener;
    setOpener(null);
    (from === 'more' ? more : bottle).current?.focus();
  }

  function choose(taps: number): void {
    close();
    if (!busy) onSetCount(clampCount(count + taps, target));
  }

  function cancelHold(): void {
    if (hold.current) clearTimeout(hold.current.timer);
    hold.current = null;
  }

  function pointerDown(event: PointerEvent<HTMLButtonElement>): void {
    if (event.button !== 0) return;
    event.stopPropagation();
    cancelHold();
    held.current = false;
    hold.current = {
      x: event.clientX,
      y: event.clientY,
      timer: setTimeout(() => {
        hold.current = null;
        held.current = true;
        openPicker('bottle');
      }, holdMs),
    };
  }

  function pointerMove(event: PointerEvent<HTMLButtonElement>): void {
    const start = hold.current;
    if (start && Math.hypot(event.clientX - start.x, event.clientY - start.y) > slop) cancelHold();
  }

  function click(): void {
    if (held.current) {
      held.current = false;
      return;
    }
    tap();
  }

  function keyDown(event: KeyboardEvent<HTMLDivElement>): void {
    if (event.key !== 'Escape') return;
    event.stopPropagation();
    close();
  }

  return (
    <div
      className="flex flex-wrap items-center gap-x-2 gap-y-2"
      data-picture-motion={animate ? '' : undefined}
    >
      <PictureButton
        ref={bottle}
        label={label}
        busy={busy}
        onClick={click}
        onPointerDown={pointerDown}
        onPointerMove={pointerMove}
        onPointerUp={cancelHold}
        onPointerLeave={cancelHold}
        onPointerCancel={cancelHold}
        onContextMenu={(event) => event.preventDefault()}
      >
        <BottleDrawing count={count} target={target} />
      </PictureButton>
      <ProgressText text={progress} />
      <Button
        ref={more}
        type="button"
        variant="ghost"
        className="px-3 text-[0.875rem] text-muted-foreground"
        aria-label={t('habitVisuals.today.bottle.moreFor', { name: habit.name })}
        aria-expanded={opener !== null}
        aria-disabled={busy || full || undefined}
        onClick={() => (opener ? close() : openPicker('more'))}
      >
        {t('habitVisuals.today.bottle.more')}
      </Button>
      {opener && (
        <div
          role="group"
          aria-label={t('habitVisuals.today.bottle.choices', { name: habit.name })}
          className="flex basis-full flex-wrap items-center gap-2"
          onKeyDown={keyDown}
        >
          {choices.map((taps, index) => {
            const amount = amounts.times(habit, taps);
            return (
              <Button
                key={taps}
                ref={index === 0 ? first : undefined}
                type="button"
                variant="secondary"
                onClick={() => choose(taps)}
              >
                {t('habitVisuals.today.bottle.choice', {
                  amount: amount ? amountText(t, amount) : taps,
                })}
              </Button>
            );
          })}
          <Button
            ref={choices.length === 0 ? first : undefined}
            type="button"
            variant="ghost"
            onClick={close}
          >
            {t('habitVisuals.today.bottle.close')}
          </Button>
        </div>
      )}
    </div>
  );
}
