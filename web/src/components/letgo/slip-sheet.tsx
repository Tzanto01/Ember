import { Check, X } from 'lucide-react';
import { Dialog } from 'radix-ui';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import { type LetGoHabit, type SlipReason, slipReasons } from '@/lib/models';
import { asApiError, problemToMessage } from '@/lib/problem';
import { api } from '@/lib/services';
import { cn } from '@/lib/utils';
import { notedText } from './letgo-text';
import { TryInstead } from './try-instead';

export interface SlipSheetProps {
  habit: LetGoHabit;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Called with the habit the server returned after a slip was saved or undone. */
  onChange: (habit: LetGoHabit) => void;
}

interface Problem {
  heading: string;
  message: string;
}

const chipClass =
  'inline-flex min-h-[44px] cursor-pointer items-center gap-2 rounded-full border-[length:max(1px,var(--border-width))] border-input bg-card px-4 text-base text-card-foreground transition-[color,background-color,border-color,scale] duration-[var(--ember-dur-fast)] ease-ember hover:bg-accent active:scale-[0.97] aria-pressed:border-primary aria-pressed:bg-accent aria-pressed:font-semibold aria-pressed:text-accent-foreground';

/** Focuses today's journal on Today (its text box, or its heading while the journal is locked) and brings it into view. */
function focusJournal(date: string): void {
  const journal = document.querySelector<HTMLElement>(`[data-journal="${date}"]`);
  const target = journal?.querySelector<HTMLElement>('textarea') ?? journal?.querySelector<HTMLElement>('h2');
  target?.focus({ preventScroll: true });
  target?.scrollIntoView({ block: 'center' });
}

/**
 * The "I slipped" dialog for today: optional reason chips (at most one), a link to today's journal and Save. After
 * saving it says "Noted." with the longest stretch and, when this was the day's only slip, offers Undo, which removes it again.
 * Mount it with a new `key` each time it opens so it starts from a clean form.
 */
export function SlipSheet({ habit, open, onOpenChange, onChange }: SlipSheetProps) {
  const { t } = useTranslation();
  const [reason, setReason] = useState<SlipReason | null>(null);
  const [saved, setSaved] = useState<LetGoHabit | null>(null);
  const [undone, setUndone] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const toJournal = useRef(false);
  const savedLine = useRef<HTMLParagraphElement>(null);
  const date = habit.today.date;

  function change(next: boolean): void {
    if (busy) return;
    onOpenChange(next);
  }

  async function run(action: () => Promise<LetGoHabit>, heading: string, after: (updated: LetGoHabit) => void) {
    if (busy) return;
    setBusy(true);
    setProblem(null);
    try {
      const updated = await action();
      onChange(updated);
      after(updated);
    } catch (error) {
      const apiError = asApiError(error);
      if (!apiError.handled) setProblem({ heading, message: problemToMessage(apiError).message });
    } finally {
      setBusy(false);
    }
  }

  function save(): void {
    void run(() => api.addSlip(habit.id, date, reason ?? undefined), t('letgo.sheet.saveFailed'), (updated) => {
      setSaved(updated);
      requestAnimationFrame(() => savedLine.current?.focus());
    });
  }

  function undo(): void {
    void run(() => api.clearSlips(habit.id, date), t('letgo.undoFailed'), () => {
      setUndone(true);
      requestAnimationFrame(() => savedLine.current?.focus());
    });
  }

  function openJournal(): void {
    toJournal.current = true;
    change(false);
  }

  return (
    <Dialog.Root open={open} onOpenChange={change}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-[color-mix(in_oklch,var(--foreground)_35%,transparent)]" />
        <Dialog.Content
          onCloseAutoFocus={(event) => {
            if (!toJournal.current) return;
            toJournal.current = false;
            event.preventDefault();
            focusJournal(date);
          }}
          className={cn(
            'fixed z-40 flex flex-col gap-4 overflow-y-auto border-border bg-card text-card-foreground shadow-card focus:outline-none',
            'motion-safe:animate-[ember-arrive_var(--ember-dur)_var(--ember-ease)_both]',
            'inset-x-0 bottom-0 max-h-[85dvh] rounded-t-2xl border-t p-5 pb-[calc(1.25rem+env(safe-area-inset-bottom))]',
            'sm:inset-x-auto sm:top-1/2 sm:bottom-auto sm:left-1/2 sm:w-[28rem] sm:-translate-x-1/2 sm:-translate-y-1/2 sm:rounded-2xl sm:border sm:p-6',
          )}
        >
          <div className="flex items-start justify-between gap-3">
            <div className="flex min-w-0 flex-col gap-1 pt-2">
              <Dialog.Title className="text-[1.375rem]">{t('letgo.sheet.title')}</Dialog.Title>
              <p className="text-muted-foreground [overflow-wrap:anywhere]">{habit.name}</p>
            </div>
            <Dialog.Close asChild>
              <Button
                variant="ghost"
                size="icon"
                className="-mr-2 shrink-0 rounded-full"
                aria-label={t('letgo.sheet.close')}
                aria-disabled={busy || undefined}
              >
                <X aria-hidden />
              </Button>
            </Dialog.Close>
          </div>
          {saved ? (
            <>
              <Dialog.Description asChild>
                <p ref={savedLine} tabIndex={-1} role="status" className="text-[1.125rem] focus:outline-none">
                  {undone ? t('letgo.undone') : notedText(saved.stats.longestStretch)}
                </p>
              </Dialog.Description>
              {!undone && <TryInstead />}
              {problem && (
                <Notice tone="notice" heading={problem.heading}>
                  <p>{problem.message}</p>
                </Notice>
              )}
              <div className="flex flex-col gap-3 sm:flex-row-reverse">
                <Dialog.Close asChild>
                  <Button block className="sm:w-auto">
                    {t('letgo.sheet.done')}
                  </Button>
                </Dialog.Close>
                {!undone && saved.today.slips === 1 && (
                  <BusyButton
                    variant="ghost"
                    block
                    className="sm:w-auto"
                    busy={busy}
                    busyLabel={t('letgo.sheet.saving')}
                    onClick={undo}
                  >
                    {t('letgo.undo')}
                  </BusyButton>
                )}
              </div>
            </>
          ) : (
            <>
              <Dialog.Description className="text-muted-foreground">{t('letgo.sheet.body')}</Dialog.Description>
              <div className="flex flex-col gap-2">
                <p id="slip-reasons" className="font-medium">
                  {t('letgo.sheet.reasons')}{' '}
                  <span className="font-normal text-muted-foreground">{t('habitFields.optional')}</span>
                </p>
                <div role="group" aria-labelledby="slip-reasons" className="flex flex-wrap gap-2">
                  {slipReasons.map((id) => (
                    <button
                      key={id}
                      type="button"
                      aria-pressed={reason === id}
                      className={chipClass}
                      onClick={() => setReason(reason === id ? null : id)}
                    >
                      {t(`letgo.reason.${id}`)}
                      {reason === id && <Check aria-hidden className="size-[16px]" />}
                    </button>
                  ))}
                </div>
              </div>
              <Button variant="link" className="self-start px-0" onClick={openJournal}>
                {t('letgo.sheet.journal')}
              </Button>
              {problem && (
                <Notice tone="notice" heading={problem.heading}>
                  <p>{problem.message}</p>
                </Notice>
              )}
              <div className="flex flex-col gap-3 sm:flex-row-reverse">
                <BusyButton block className="sm:w-auto" busy={busy} busyLabel={t('letgo.sheet.saving')} onClick={save}>
                  {t('letgo.sheet.save')}
                </BusyButton>
                <Dialog.Close asChild>
                  <Button variant="ghost" block className="sm:w-auto" aria-disabled={busy || undefined}>
                    {t('letgo.sheet.cancel')}
                  </Button>
                </Dialog.Close>
              </div>
            </>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
