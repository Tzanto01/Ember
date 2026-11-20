import { useQueryClient } from '@tanstack/react-query';
import { X } from 'lucide-react';
import { Dialog } from 'radix-ui';
import { type SubmitEvent, useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { type FiresideFriend, firesideApi, firesideKey, warmthMessages, type WarmthMessage, warmthText } from '@/lib/fireside';
import { asApiError, problemToMessage } from '@/lib/problem';

/**
 * The send sheet (screen 5): the five warmth messages as a radio group, Send and Cancel, then "Sent". Open while
 * `friend` is set; `onClose` runs on Cancel, Done, Escape or a tap outside. `onSent` gets the announcement text.
 */
export function SendSheet({
  friend,
  onClose,
  onSent,
}: {
  friend: FiresideFriend | null;
  onClose: () => void;
  onSent?: (announcement: string) => void;
}) {
  const { t } = useTranslation();
  const id = useId();
  const queryClient = useQueryClient();
  const [message, setMessage] = useState<WarmthMessage>('thinking');
  const [busy, setBusy] = useState(false);
  const [sent, setSent] = useState<WarmthMessage | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const sentRef = useRef<HTMLDivElement>(null);
  const name = friend?.name ?? '';

  useEffect(() => {
    if (friend) return;
    setMessage('thinking');
    setSent(null);
    setProblem(null);
  }, [friend]);

  useEffect(() => {
    if (sent) sentRef.current?.focus();
  }, [sent]);

  async function send(event: SubmitEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();
    if (!friend || busy) return;
    setBusy(true);
    setProblem(null);
    try {
      await firesideApi.sendWarmth(friend.id, message);
      setSent(message);
      onSent?.(t('fireside.send.sentAnnounce', { name }));
    } catch (error) {
      const apiError = asApiError(error);
      if (!apiError.handled)
        setProblem(apiError.status === 409 ? t('fireside.send.already', { name }) : problemToMessage(apiError).message);
    } finally {
      setBusy(false);
      void queryClient.invalidateQueries({ queryKey: firesideKey });
    }
  }

  return (
    <Dialog.Root open={!!friend} onOpenChange={(open) => !open && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-[color-mix(in_oklch,var(--foreground)_35%,transparent)]" />
        <Dialog.Content
          data-testid="fireside-send-sheet"
          className="fixed inset-x-0 bottom-0 z-40 mx-auto flex max-h-[85dvh] w-full max-w-[32rem] flex-col gap-4 overflow-y-auto rounded-t-2xl border-t border-border bg-card p-5 pb-[calc(1.25rem+env(safe-area-inset-bottom))] text-card-foreground shadow-card focus:outline-none motion-safe:animate-[ember-arrive_var(--ember-dur)_var(--ember-ease)_both] md:bottom-auto md:top-[15dvh] md:rounded-2xl md:border"
        >
          <div className="flex items-start justify-between gap-3">
            <Dialog.Title className="pt-2 text-[1.375rem] [overflow-wrap:anywhere]">
              {sent ? t('fireside.send.sentTitle') : t('fireside.send.title', { name })}
            </Dialog.Title>
            <Dialog.Close asChild>
              <Button variant="ghost" size="icon" className="-mr-2 shrink-0 rounded-full" aria-label={t('fireside.send.close')}>
                <X aria-hidden />
              </Button>
            </Dialog.Close>
          </div>
          {sent ? (
            <>
              <Dialog.Description asChild>
                <div ref={sentRef} tabIndex={-1} className="focus:outline-none">
                  <p className="[overflow-wrap:anywhere]">
                    {t('fireside.send.sentText', { message: warmthText(sent), name })}
                  </p>
                </div>
              </Dialog.Description>
              <div>
                <Dialog.Close asChild>
                  <Button>{t('fireside.send.done')}</Button>
                </Dialog.Close>
              </div>
            </>
          ) : (
            <form noValidate onSubmit={(event) => void send(event)} className="flex flex-col gap-4">
              <Dialog.Description className="text-muted-foreground [overflow-wrap:anywhere]">
                <span id={`${id}-pick`}>{t('fireside.send.pick', { name })}</span>
              </Dialog.Description>
              <RadioGroup
                aria-labelledby={`${id}-pick`}
                value={message}
                onValueChange={(value) => setMessage(value as WarmthMessage)}
                className="flex flex-col gap-2"
              >
                {warmthMessages.map((option) => (
                  <label
                    key={option}
                    htmlFor={`${id}-${option}`}
                    className="flex min-h-[48px] cursor-pointer items-center gap-3 rounded-lg border-[length:max(1px,var(--border-width))] border-border px-4 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-accent has-[[data-state=checked]]:text-accent-foreground"
                  >
                    <RadioGroupItem id={`${id}-${option}`} value={option} aria-labelledby={`${id}-${option}-name`} />
                    <span id={`${id}-${option}-name`}>{warmthText(option)}</span>
                  </label>
                ))}
              </RadioGroup>
              {problem && (
                <Notice tone="notice">
                  <p>{problem}</p>
                </Notice>
              )}
              <div className="flex flex-wrap gap-2">
                <BusyButton type="submit" busy={busy} busyLabel={t('fireside.send.sending')}>
                  {t('fireside.send.send')}
                </BusyButton>
                <Dialog.Close asChild>
                  <Button type="button" variant="ghost">
                    {t('fireside.send.cancel')}
                  </Button>
                </Dialog.Close>
              </div>
            </form>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
