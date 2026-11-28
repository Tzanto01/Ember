import type { ComponentType } from 'react';
import { useReduceMotion } from '@/components/moment/use-reduce-motion';
import { clampCount } from '@/lib/habit-visuals/progress';
import type { Habit, HabitPicture } from '@/lib/models';
import { cn } from '@/lib/utils';
import { Bottle } from './bottle';
import { Kindling } from './kindling';
import { Pills } from './pills';
import type { PictureProps } from './shared';
import { Shelf } from './shelf';
import { Trail } from './trail';

const pictures: Record<HabitPicture, ComponentType<PictureProps>> = {
  bottle: Bottle,
  pills: Pills,
  kindling: Kindling,
  shelf: Shelf,
  trail: Trail,
};

/** Whether Today shows the habit's picture: it has one, and it is neither paused nor before its start. */
export function showsPicture(habit: Habit): boolean {
  const state = habit.today.state;
  return !!habit.picture && !habit.pausedToday && state !== 'paused' && state !== 'beforestart';
}

/**
 * A habit's picture on its Today row, or nothing when {@link showsPicture} says no. It sits under the row and, from
 * `sm`, beside the check button unless the row also has a timer. Taps ask `onSetCount` for an absolute count.
 */
export function HabitPictureControl({
  habit,
  busy,
  onSetCount,
}: {
  habit: Habit;
  busy: boolean;
  onSetCount: (count: number) => void;
}) {
  const animate = !useReduceMotion();
  const Picture = habit.picture && pictures[habit.picture];
  if (!showsPicture(habit) || !Picture) return null;
  const beside = habit.timerMinutes == null;
  return (
    <div
      data-picture={habit.picture}
      data-picture-beside={beside ? '' : undefined}
      className={cn(
        'col-span-full flex min-w-0 pt-2',
        beside &&
          'sm:col-span-1 sm:col-start-2 sm:row-start-1 sm:max-w-[18rem] sm:justify-end sm:pt-0',
      )}
    >
      <Picture
        habit={habit}
        count={
          habit.today.state === 'done'
            ? habit.timesPerDay
            : clampCount(habit.today.count, habit.timesPerDay)
        }
        busy={busy}
        onSetCount={onSetCount}
        animate={animate}
      />
    </div>
  );
}
