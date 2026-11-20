import { Circle, CircleCheck } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Spinner } from '@/components/spinner';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import type { LetGoHabit, LocalDateString } from '@/lib/models';
import { asApiError, problemToMessage } from '@/lib/problem';
import { api } from '@/lib/services';
import { cn } from '@/lib/utils';

const ackSeconds = 6;

interface Problem {
  heading: string;
  message: string;
}

/** Today's "made it" state for one let-go row: the toggle's look, and what the line under the row says. */
export interface MadeIt {
  pressed: boolean;
  busy: boolean;
  /** The day the warm line was earned on, for a few seconds after marking, while that mark still stands. */
  ackDate: LocalDateString | null;
  undone: boolean;
  problem: Problem | null;
  toggle: () => void;
}

/** Marks or unmarks today on a free let-go day and hands the habit the server returned to `onChange`. */
export function useMadeIt(habit: LetGoHabit, onChange: (habit: LetGoHabit) => void): MadeIt {
  const { t } = useTranslation();
  const [busy, setBusy] = useState(false);
  const [ackDate, setAckDate] = useState<LocalDateString | null>(null);
  const [undone, setUndone] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const pressed = habit.today.madeIt;
  const date = habit.today.date;

  useEffect(() => {
    if (!ackDate) return;
    const timer = setTimeout(() => setAckDate(null), ackSeconds * 1000);
    return () => clearTimeout(timer);
  }, [ackDate]);

  async function toggle() {
    if (busy) return;
    const mark = !pressed;
    setBusy(true);
    setProblem(null);
    try {
      onChange(mark ? await api.markMadeIt(habit.id, date) : await api.clearMadeIt(habit.id, date));
      setAckDate(mark ? date : null);
      setUndone(!mark);
    } catch (error) {
      const apiError = asApiError(error);
      if (!apiError.handled) {
        setProblem({
          heading: t(mark ? 'letgo.sheet.saveFailed' : 'letgo.undoFailed'),
          message: problemToMessage(apiError).message,
        });
      }
    } finally {
      setBusy(false);
    }
  }

  return { pressed, busy, ackDate, undone, problem, toggle: () => void toggle() };
}

/** "Made it today" (`aria-pressed`): an open circle until tapped, then a quiet tinted look with a check; tapping again undoes. */
export function MadeItButton({ habit, state }: { habit: LetGoHabit; state: MadeIt }) {
  const { t } = useTranslation();
  const Icon = state.pressed ? CircleCheck : Circle;
  return (
    <Button
      variant="outline"
      aria-pressed={state.pressed}
      aria-label={t('letgo.madeItName', { name: habit.name })}
      aria-disabled={state.busy || undefined}
      onClick={state.toggle}
      className={cn(
        'aria-disabled:cursor-progress',
        state.pressed &&
          'border-[color-mix(in_oklch,var(--done)_45%,var(--input))] bg-[color-mix(in_oklch,var(--done)_12%,var(--card))] font-normal hover:bg-[color-mix(in_oklch,var(--done)_18%,var(--card))]',
      )}
    >
      {state.busy ? (
        <Spinner />
      ) : (
        <Icon aria-hidden className={cn(!state.pressed && 'text-muted-foreground')} />
      )}
      {t('letgo.madeIt')}
    </Button>
  );
}

/**
 * What follows a tap, inside the row: the warm line for a few seconds after marking (visible, and announced politely), only an
 * announcement after undoing, and a quiet notice when saving failed. The live region stays mounted so it is heard.
 */
export function MadeItFeedback({ habit, state }: { habit: LetGoHabit; state: MadeIt }) {
  const { t } = useTranslation();
  const acknowledged = state.pressed && state.ackDate === habit.today.date;
  return (
    <>
      <p
        aria-live="polite"
        aria-atomic="true"
        className={cn(
          acknowledged
            ? 'basis-full text-[0.9375rem] text-foreground motion-safe:animate-[ember-arrive_var(--ember-dur)_var(--ember-ease)_both]'
            : 'sr-only',
        )}
      >
        {acknowledged
          ? t('letgo.madeItAck')
          : state.undone && !state.pressed
            ? t('letgo.madeItUndone')
            : ''}
      </p>
      {state.problem && (
        <Notice tone="notice" heading={state.problem.heading} className="basis-full">
          <p>{state.problem.message}</p>
        </Notice>
      )}
    </>
  );
}
