import type { ReactNode } from 'react';
import { cardVariants } from '@/components/ui/card';

/** Leaves room for the sticky header when a Settings `#address` scrolls its card or row into view. */
export const settingsScrollMargin = 'scroll-mt-[calc(56px+env(safe-area-inset-top)+1.5rem)]';

/**
 * One Settings card: a labelled section whose `id` is its `#card-id` address and whose heading (`<id>-heading`) takes
 * focus when that address opens it. Scrolling to it leaves room for the sticky header.
 */
export function SettingsCard({
  id,
  title,
  farewell,
  children,
}: {
  id: string;
  title: string;
  farewell?: boolean;
  children: ReactNode;
}) {
  return (
    <section
      id={id}
      aria-labelledby={`${id}-heading`}
      className={cardVariants({
        variant: farewell ? 'farewell' : 'default',
        className: settingsScrollMargin,
      })}
    >
      <h2 id={`${id}-heading`} tabIndex={-1} className="text-[1.375rem] focus:outline-none">
        {title}
      </h2>
      {children}
    </section>
  );
}
