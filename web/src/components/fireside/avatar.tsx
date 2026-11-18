import { initial } from '@/lib/fireside';
import { cn } from '@/lib/utils';

/** A friend's initial in a circle. Decorative: the name is always shown next to it. */
export function FriendAvatar({ name, className }: { name: string; className?: string }) {
  return (
    <span
      aria-hidden
      className={cn(
        'inline-flex size-[40px] shrink-0 items-center justify-center rounded-full bg-secondary font-display text-[1.125rem] font-bold text-secondary-foreground',
        className,
      )}
    >
      {initial(name)}
    </span>
  );
}
