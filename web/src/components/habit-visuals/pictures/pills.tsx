import { Check } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { type PillSlot, pillSlots, slotTapCount } from '@/lib/habit-visuals/progress';
import { currentLanguage } from '@/lib/i18n';
import { formatTime } from '@/lib/local-date';
import { cn } from '@/lib/utils';
import { type PictureProps, ProgressText, usePictureText } from './shared';

function PillIcon() {
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" className="size-[18px]">
      <rect
        x="3"
        y="8"
        width="18"
        height="8"
        rx="4"
        fill="none"
        stroke="currentColor"
        strokeWidth="2"
      />
      <path d="M12 8v8" stroke="currentColor" strokeWidth="2" />
    </svg>
  );
}

/**
 * The pill box: one toggle button per check-in of the day, labelled with the habit's reminder times when there is one
 * per slot, else "1st", "2nd", … An open slot checks in once (the next open slot fills); a taken slot undoes back to it.
 */
export function Pills({ habit, count, busy, onSetCount, animate }: PictureProps) {
  const { t } = useTranslation();
  const { progress } = usePictureText(habit, count);
  const locale = currentLanguage();
  const ordinals = new Intl.PluralRules(locale, { type: 'ordinal' });
  const ordinal = (position: number) => {
    const form = ordinals.select(position);
    return t(
      `habitVisuals.today.ordinal.${form === 'one' || form === 'two' || form === 'few' ? form : 'other'}`,
      {
        n: position,
      },
    );
  };
  const slotText = ({ label }: PillSlot) =>
    label.kind === 'time' ? formatTime(label.time, locale) : ordinal(label.position);

  return (
    <div className="flex min-w-0 flex-col gap-1" data-picture-motion={animate ? '' : undefined}>
      <ul className="flex flex-wrap gap-1.5">
        {pillSlots(habit, count).map((slot) => {
          const text = slotText(slot);
          return (
            <li key={slot.index}>
              <button
                type="button"
                aria-pressed={slot.taken}
                aria-disabled={busy || undefined}
                aria-label={t('habitVisuals.today.pills.slot', { name: habit.name, slot: text })}
                onClick={() =>
                  !busy && onSetCount(slotTapCount(slot.index, count, habit.timesPerDay))
                }
                className={cn(
                  'inline-flex min-h-[44px] min-w-[44px] touch-manipulation select-none items-center justify-center gap-1 rounded-md border-2 px-2 text-[0.875rem] font-semibold tabular-nums aria-disabled:cursor-progress',
                  slot.taken
                    ? 'border-done bg-done text-done-foreground'
                    : 'border-input bg-card text-muted-foreground hover:bg-muted',
                )}
              >
                {slot.taken ? (
                  <Check aria-hidden className="size-[16px]" strokeWidth={3} />
                ) : (
                  <PillIcon />
                )}
                <span aria-hidden="true">{text}</span>
              </button>
            </li>
          );
        })}
      </ul>
      <ProgressText text={progress} />
    </div>
  );
}
