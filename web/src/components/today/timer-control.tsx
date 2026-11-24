import { Pause, Play, Square, Timer } from 'lucide-react';
import { type FocusEvent, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import type { Habit } from '@/lib/models';
import { habitTimer } from '@/lib/services';
import { useStore } from '@/lib/store';
import type { TimerView } from '@/lib/timer/habit-timer';
import { checkButtonId } from './check-button';
import { TimerSwitch } from './timer-switch';

/** `mm:ss`, from an hour `h:mm:ss`; a started second counts as a whole one. */
export function timeLeftText(ms: number): string {
  const total = Math.ceil(ms / 1000);
  const pad = (value: number) => String(value).padStart(2, '0');
  const hours = Math.floor(total / 3600);
  const rest = `${pad(Math.floor(total / 60) % 60)}:${pad(total % 60)}`;
  return hours ? `${hours}:${rest}` : rest;
}

/** Focuses the timer's first control, or the habit's check button once the row has none. */
function focusControl(root: HTMLElement | null, habitId: number): void {
  (root?.querySelector('button') ?? document.getElementById(checkButtonId(habitId)))?.focus();
}

/** The remaining time, updated each second while `running` and the page is visible; never announced by itself. */
function TimeLeft({ name, running }: { name: string; running: boolean }) {
  const { t } = useTranslation();
  const [left, setLeft] = useState(() => habitTimer.remaining());

  useEffect(() => {
    let next: ReturnType<typeof setTimeout> | undefined;
    const tick = () => {
      clearTimeout(next);
      const ms = habitTimer.remaining();
      setLeft(ms);
      if (running && ms > 0 && document.visibilityState === 'visible') next = setTimeout(tick, ms % 1000 || 1000);
    };
    tick();
    document.addEventListener('visibilitychange', tick);
    return () => {
      clearTimeout(next);
      document.removeEventListener('visibilitychange', tick);
    };
  }, [running]);

  return (
    <span role="timer" aria-label={t('timer.left', { name })} className="text-[1.125rem] font-semibold tabular-nums">
      {timeLeftText(left)}
    </span>
  );
}

export interface TimerControlProps {
  habit: Habit;
  /** False on rows that can't start a timer (paused, not started); one already going still shows. */
  startable: boolean;
  onAnnounce?: (message: string) => void;
}

/**
 * A Today row's timer: the start control of a habit with a length, or while its timer goes the remaining time with
 * Pause or Resume and Stop. Asks softly before another habit's timer makes room. Nothing without a length or timer.
 */
export function TimerControl({ habit, startable, onAnnounce }: TimerControlProps) {
  const { t } = useTranslation();
  const view = useStore(habitTimer.view);
  const notifies = useStore(habitTimer.notifies);
  const root = useRef<HTMLDivElement>(null);
  const focused = useRef(false);
  const [other, setOther] = useState<TimerView | null>(null);
  const [asking, setAsking] = useState(false);
  const { id, name } = habit;
  const minutes = habit.timerMinutes ?? null;
  const active = view && view.phase !== 'ended' ? view : null;
  const phase = active?.record.habitId === id ? active.phase : null;
  const going = phase !== null;

  const wasGoing = useRef(going);
  useLayoutEffect(() => {
    if (wasGoing.current === going) return;
    wasGoing.current = going;
    if (!focused.current) return;
    focused.current = false;
    focusControl(root.current, id);
  }, [going, id]);

  function start(): void {
    habitTimer.start(habit);
    onAnnounce?.(t('timer.announce.started', { name }));
  }

  function tapStart(): void {
    const current = habitTimer.view.get();
    if (!current || current.phase === 'ended') {
      start();
      return;
    }
    setOther(current);
    setAsking(true);
  }

  function toggle(): void {
    if (phase === 'running') habitTimer.pause();
    else habitTimer.resume();
    onAnnounce?.(t(phase === 'running' ? 'timer.announce.paused' : 'timer.announce.resumed', { name }));
  }

  function stop(): void {
    habitTimer.stop();
    onAnnounce?.(t('timer.announce.stopped', { name }));
  }

  if (!going && (minutes === null || !startable)) return null;
  return (
    <div
      ref={root}
      data-timer=""
      onFocus={() => (focused.current = true)}
      onBlur={(event: FocusEvent) => {
        if (event.target.isConnected) focused.current = false;
      }}
      className="col-span-2 flex min-w-0 flex-col sm:col-span-1 sm:col-start-2 sm:row-start-1 sm:items-end"
    >
      {going ? (
        <>
          <div className="flex flex-wrap items-center gap-x-2">
            <TimeLeft name={name} running={phase === 'running'} />
            {phase === 'paused' && <span className="text-[0.875rem] text-muted-foreground">{t('timer.paused')}</span>}
            <span className="flex gap-2">
              <Button
                variant="ghost"
                size="icon"
                aria-label={t(phase === 'running' ? 'timer.pause' : 'timer.resume', { name })}
                onClick={toggle}
              >
                {phase === 'running' ? <Pause aria-hidden /> : <Play aria-hidden />}
              </Button>
              <Button variant="ghost" size="icon" aria-label={t('timer.stop', { name })} onClick={stop}>
                <Square aria-hidden />
              </Button>
            </span>
          </div>
          {phase === 'running' && !notifies && (
            <p className="text-[0.875rem] text-muted-foreground">{t('timer.here')}</p>
          )}
        </>
      ) : (
        <Button
          variant="ghost"
          data-guide="timer"
          className="-ms-3 min-w-[44px] self-start px-3 text-muted-foreground sm:ms-0"
          aria-label={t('timer.start', { name, minutes })}
          onClick={tapStart}
        >
          <Timer aria-hidden />
          {t('timer.minutes', { minutes })}
        </Button>
      )}
      {other && (
        <TimerSwitch
          current={other}
          name={name}
          open={asking}
          onOpenChange={setAsking}
          onSwitch={start}
          onClosed={() => focusControl(root.current, id)}
        />
      )}
    </div>
  );
}
