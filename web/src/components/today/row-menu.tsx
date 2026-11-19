import { ArrowDown, ArrowUp, Ellipsis, NotebookPen, Undo2 } from 'lucide-react';
import { DropdownMenu } from 'radix-ui';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

const contentClass =
  'z-40 min-w-[12rem] rounded-lg border-[length:max(1px,var(--border-width))] border-border bg-popover p-1 text-popover-foreground shadow-card motion-safe:animate-[ember-arrive_var(--ember-dur-fast)_var(--ember-ease)_both]';
const itemClass =
  'flex min-h-[44px] cursor-pointer select-none items-center gap-3 rounded-md px-3 text-base outline-none data-[highlighted]:bg-accent data-[highlighted]:text-accent-foreground data-[disabled]:cursor-default data-[disabled]:opacity-50 [&_svg]:size-[18px] [&_svg]:shrink-0 [&_svg]:text-muted-foreground';

function Menu({
  label,
  className,
  children,
}: {
  label: string;
  className?: string;
  children: ReactNode;
}) {
  return (
    <DropdownMenu.Root modal={false}>
      <DropdownMenu.Trigger asChild>
        <Button
          variant="ghost"
          size="icon"
          className={cn('shrink-0 text-muted-foreground', className)}
          aria-label={label}
        >
          <Ellipsis aria-hidden />
        </Button>
      </DropdownMenu.Trigger>
      <DropdownMenu.Portal>
        <DropdownMenu.Content
          align="end"
          sideOffset={4}
          collisionPadding={8}
          className={contentClass}
        >
          {children}
        </DropdownMenu.Content>
      </DropdownMenu.Portal>
    </DropdownMenu.Root>
  );
}

export interface RowMenuProps {
  /** What the menu is for, in its accessible name ("Options for Walk"). */
  name: string;
  /** Null leaves Move up and Move down out (rows that can't be rearranged). */
  move: { up: boolean; down: boolean; onMove: (by: -1 | 1) => void } | null;
  /** Null leaves "Note" out (routine headings). */
  onNote: (() => void) | null;
}

/** The "⋯" menu of a Today row or routine heading: Move up, Move down (the keyboard and screen-reader way to rearrange) and Note. */
export function RowMenu({ name, move, onNote }: RowMenuProps) {
  const { t } = useTranslation();
  return (
    <Menu label={t('today.menu.row', { name })}>
      {move && (
        <>
          <DropdownMenu.Item
            className={itemClass}
            disabled={!move.up}
            onSelect={() => move.onMove(-1)}
          >
            <ArrowUp aria-hidden />
            {t('today.menu.moveUp')}
          </DropdownMenu.Item>
          <DropdownMenu.Item
            className={itemClass}
            disabled={!move.down}
            onSelect={() => move.onMove(1)}
          >
            <ArrowDown aria-hidden />
            {t('today.menu.moveDown')}
          </DropdownMenu.Item>
        </>
      )}
      {onNote && (
        <DropdownMenu.Item className={itemClass} onSelect={onNote}>
          <NotebookPen aria-hidden />
          {t('today.menu.note')}
        </DropdownMenu.Item>
      )}
    </Menu>
  );
}

/** Today's "List options" menu; for now only "Sort by time again", so show it only while an own order is saved. */
export function ListMenu({ onSortByTime }: { onSortByTime: () => void }) {
  const { t } = useTranslation();
  return (
    <Menu label={t('today.menu.list')}>
      <DropdownMenu.Item className={itemClass} onSelect={onSortByTime}>
        <Undo2 aria-hidden />
        {t('today.menu.sortByTime')}
      </DropdownMenu.Item>
    </Menu>
  );
}
