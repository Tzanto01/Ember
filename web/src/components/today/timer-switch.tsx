import { Dialog } from 'radix-ui';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import type { TimerView } from '@/lib/timer/habit-timer';
import { cn } from '@/lib/utils';

export interface TimerSwitchProps {
  /** The timer that would make room. */
  current: TimerView;
  /** The habit whose timer was tapped. */
  name: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSwitch: () => void;
  /** Takes the focus back once the dialog closed. */
  onClosed: () => void;
}

/** The soft question before another habit's timer replaces the current one: switch, or keep it. */
export function TimerSwitch({ current, name, open, onOpenChange, onSwitch, onClosed }: TimerSwitchProps) {
  const { t } = useTranslation();
  const values = { current: current.record.name, name };
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-[color-mix(in_oklch,var(--foreground)_35%,transparent)]" />
        <Dialog.Content
          onCloseAutoFocus={(event) => {
            event.preventDefault();
            onClosed();
          }}
          className={cn(
            'fixed z-40 flex flex-col gap-4 overflow-y-auto border-border bg-card text-card-foreground shadow-card [overflow-wrap:anywhere] focus:outline-none',
            'motion-safe:animate-[ember-arrive_var(--ember-dur)_var(--ember-ease)_both]',
            'inset-x-0 bottom-0 max-h-[85dvh] rounded-t-2xl border-t p-5 pb-[calc(1.25rem+env(safe-area-inset-bottom))]',
            'sm:inset-x-auto sm:top-1/2 sm:bottom-auto sm:left-1/2 sm:w-[28rem] sm:-translate-x-1/2 sm:-translate-y-1/2 sm:rounded-2xl sm:border sm:p-6',
          )}
        >
          <Dialog.Title className="text-[1.375rem]">{t('timer.switch.title')}</Dialog.Title>
          <Dialog.Description className="text-muted-foreground">
            {t(current.phase === 'paused' ? 'timer.switch.bodyPaused' : 'timer.switch.body', values)}
          </Dialog.Description>
          <div className="flex flex-col gap-3">
            <Button
              type="button"
              block
              onClick={() => {
                onSwitch();
                onOpenChange(false);
              }}
            >
              {t('timer.switch.yes', values)}
            </Button>
            <Dialog.Close asChild>
              <Button type="button" variant="ghost" block>
                {t('timer.switch.keep', values)}
              </Button>
            </Dialog.Close>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
