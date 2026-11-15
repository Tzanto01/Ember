import { ChevronDown, CircleHelp, X } from 'lucide-react';
import { Accordion, Dialog } from 'radix-ui';
import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useLocation } from 'react-router';
import { useReduceMotion } from '@/components/moment/use-reduce-motion';
import { Button } from '@/components/ui/button';
import { findGuideTarget, showTarget } from '@/lib/guide/highlight';
import { type GuideEntry, guideFor } from '@/lib/guide/registry';
import { cn } from '@/lib/utils';

const wideQuery = '(min-width: 48rem)';

function subscribeWide(change: () => void): () => void {
  const query = window.matchMedia?.(wideQuery);
  query?.addEventListener('change', change);
  return () => query?.removeEventListener('change', change);
}

function useWide(): boolean {
  return useSyncExternalStore(subscribeWide, () => window.matchMedia?.(wideQuery).matches ?? false);
}

function Answer({ entry, onShow }: { entry: GuideEntry; onShow: (target: string) => void }) {
  const { t } = useTranslation();
  const [target] = useState(() => (entry.target && findGuideTarget(entry.target) ? entry.target : null));
  return (
    <div className="flex flex-col items-start gap-2 pb-4">
      <p>{t(entry.answerKey, entry.values ?? {})}</p>
      {target && (
        <Button variant="link" className="min-h-[44px] px-0" onClick={() => onShow(target)}>
          {t('guide.showMe')}
        </Button>
      )}
    </div>
  );
}

/**
 * The header's "How this works" button and the guide it opens for the current screen: a bottom sheet on phones, a
 * side panel from `md` that stays open while the page is used. Renders nothing on a screen without a guide.
 * "Show me" closes the guide, then scrolls its target to the middle and rings it briefly.
 */
export function Guide() {
  const { t } = useTranslation();
  const { pathname } = useLocation();
  const guide = guideFor(pathname);
  const wide = useWide();
  const instant = useReduceMotion();
  const [open, setOpen] = useState(false);
  const trigger = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const pending = useRef<string | null>(null);

  useEffect(() => {
    if (!guide) setOpen(false);
  }, [guide]);

  if (!guide) return null;

  function show(target: string): void {
    pending.current = target;
    setOpen(false);
  }

  function closed(event: Event): void {
    event.preventDefault();
    if (trigger.current?.isConnected) trigger.current.focus({ preventScroll: true });
    const target = pending.current && findGuideTarget(pending.current);
    pending.current = null;
    if (target) showTarget(target, instant);
  }

  const screen = t(guide.nameKey);

  return (
    <Dialog.Root open={open} onOpenChange={setOpen} modal={!wide}>
      <Dialog.Trigger asChild>
        <Button ref={trigger} variant="ghost" size="icon" className="rounded-full text-muted-foreground" aria-label={t('guide.open')}>
          <CircleHelp aria-hidden className="size-[22px]" />
        </Button>
      </Dialog.Trigger>
      <Dialog.Portal>
        {!wide && <Dialog.Overlay className="fixed inset-0 z-40 bg-[color-mix(in_oklch,var(--foreground)_35%,transparent)]" />}
        <Dialog.Content
          ref={panel}
          onCloseAutoFocus={closed}
          onInteractOutside={wide ? (event) => event.preventDefault() : undefined}
          onEscapeKeyDown={(event) => {
            if (wide && !panel.current?.contains(event.target as Node)) event.preventDefault();
          }}
          className={cn(
            'fixed z-40 flex flex-col gap-4 overflow-y-auto border-border bg-card text-card-foreground shadow-card focus:outline-none',
            'motion-safe:animate-[ember-arrive_var(--ember-dur)_var(--ember-ease)_both]',
            wide
              ? 'top-[calc(57px+env(safe-area-inset-top))] right-0 bottom-0 w-[24rem] border-l p-6'
              : 'inset-x-0 bottom-0 max-h-[85dvh] rounded-t-2xl border-t p-5 pb-[calc(1.25rem+env(safe-area-inset-bottom))]',
          )}
        >
          <div className="flex items-start justify-between gap-3">
            <Dialog.Title className="pt-2 text-[1.375rem]">{t('guide.title', { screen })}</Dialog.Title>
            <Dialog.Close asChild>
              <Button variant="ghost" size="icon" className="-mr-2 shrink-0 rounded-full" aria-label={t('guide.close')}>
                <X aria-hidden />
              </Button>
            </Dialog.Close>
          </div>
          <Dialog.Description className="text-muted-foreground">{t(guide.ledeKey)}</Dialog.Description>
          <Accordion.Root key={pathname} type="single" collapsible className="flex flex-col divide-y divide-border border-y border-border">
            {guide.entries.map((entry) => (
              <Accordion.Item key={entry.id} value={entry.id}>
                <Accordion.Header>
                  <Accordion.Trigger className="group flex min-h-[48px] w-full cursor-pointer items-center justify-between gap-3 py-3 text-left font-semibold">
                    {t(entry.questionKey)}
                    <ChevronDown
                      aria-hidden
                      className="size-[18px] shrink-0 text-muted-foreground motion-safe:transition-transform group-data-[state=open]:rotate-180"
                    />
                  </Accordion.Trigger>
                </Accordion.Header>
                <Accordion.Content>
                  <Answer entry={entry} onShow={show} />
                </Accordion.Content>
              </Accordion.Item>
            ))}
          </Accordion.Root>
          <p className="text-muted-foreground">
            {t('guide.stillUnclear')}{' '}
            <Link to="/feedback" onClick={() => setOpen(false)}>
              {t('guide.sendFeedback')}
            </Link>
          </p>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
