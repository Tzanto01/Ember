import './pictures.css';
import type { TFunction } from 'i18next';
import { type ComponentProps, useId } from 'react';
import { useTranslation } from 'react-i18next';
import { nextCount } from '@/lib/habit-visuals/progress';
import { type DisplayAmount, formatAmount, progressTotal } from '@/lib/habit-visuals/units';
import { currentLanguage } from '@/lib/i18n';
import type { Habit } from '@/lib/models';
import { auth } from '@/lib/services';
import { useStore } from '@/lib/store';
import { cn } from '@/lib/utils';

/** What every Today picture gets from its row. */
export interface PictureProps {
  habit: Habit;
  /** Today's check-ins, already clamped to `timesPerDay`. */
  count: number;
  /** A save for this habit is running; taps wait for it. */
  busy: boolean;
  /** Asks the page to save today's count as this absolute value. */
  onSetCount: (count: number) => void;
  /** Fill and grow transitions are on: neither calm mode nor reduced motion. */
  animate: boolean;
}

/** An amount as text, with "pages" translated. */
export function amountText(t: TFunction, amount: DisplayAmount): string {
  return (
    amount.text ?? t('habitVisuals.today.pages', { count: amount.value, number: amount.number })
  );
}

/** The account's unit system and the language's locale, for portions and totals. */
export function useAmounts() {
  const units = useStore(auth.me)?.units ?? 'metric';
  const locale = currentLanguage();
  return {
    total: (habit: Habit, count: number) => progressTotal(count, habit, units, locale),
    portion: (habit: Habit) =>
      habit.portionAmount != null && habit.portionUnit != null
        ? formatAmount(habit.portionAmount, habit.portionUnit, units, locale)
        : null,
    times: (habit: Habit, taps: number) =>
      habit.portionAmount != null && habit.portionUnit != null
        ? formatAmount(taps * habit.portionAmount, habit.portionUnit, units, locale)
        : null,
  };
}

/**
 * The texts of a picture at `count`: the visible progress ("5 of 8, 1.25 L", or "2 of 3" without a portion or at
 * zero) and what a tap does ("Add 250 ml.", `noPortion` without a portion, or "All done for today." once full).
 */
export function usePictureText(habit: Habit, count: number, noPortion?: string) {
  const { t } = useTranslation();
  const amounts = useAmounts();
  const target = habit.timesPerDay;
  const total = count > 0 ? amounts.total(habit, count) : null;
  const portion = amounts.portion(habit);
  const full = count >= target;
  const progress = total
    ? t('habitVisuals.today.progressTotal', { done: count, target, total: amountText(t, total) })
    : t('habitVisuals.today.progress', { done: count, target });
  const action = full
    ? t('habitVisuals.today.full')
    : portion
      ? t('habitVisuals.today.add', { amount: amountText(t, portion) })
      : (noPortion ?? t('habitVisuals.today.addOne'));
  return { progress, action, full };
}

/** The always visible text progress, read out politely when it changes. */
export function ProgressText({ text, className }: { text: string; className?: string }) {
  return (
    <p
      aria-live="polite"
      aria-atomic="true"
      className={cn('text-[0.875rem] font-semibold tabular-nums', className)}
    >
      {text}
    </p>
  );
}

/** The picture itself as one button: a tap checks in once; at full it only says so in its name. */
export function PictureButton({
  label,
  busy,
  className,
  children,
  ...props
}: Omit<ComponentProps<'button'>, 'aria-label'> & { label: string; busy: boolean }) {
  return (
    <button
      type="button"
      aria-label={label}
      aria-disabled={busy || undefined}
      className={cn(
        'inline-flex min-h-[44px] min-w-[44px] shrink-0 touch-manipulation select-none items-center justify-center rounded-md px-1.5 py-1 [-webkit-touch-callout:none] hover:bg-muted aria-disabled:cursor-progress',
        className,
      )}
      {...props}
    >
      {children}
    </button>
  );
}

/** A unique id that is safe inside an SVG `url(#…)` reference. */
export function useSvgId(): string {
  return `picture-${useId().replace(/[^\w-]/g, '')}`;
}

/** A tap on a picture:`count + 1`, nothing once full or while busy. */
export function tapHandler({ habit, count, busy, onSetCount }: PictureProps) {
  return () => {
    const next = nextCount(count, habit.timesPerDay);
    if (!busy && next !== null) onSetCount(next);
  };
}
