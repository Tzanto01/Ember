import { ChevronDown, CircleHelp, X } from 'lucide-react';
import { Accordion, Dialog } from 'radix-ui';
import { type KeyboardEvent, type ReactNode, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useLocation } from 'react-router';
import { TapLink } from '@/components/auth/busy-button';
import { useReduceMotion } from '@/components/moment/use-reduce-motion';
import { Button } from '@/components/ui/button';
import { env } from '@/lib/env';
import { findGuideTarget, showTarget } from '@/lib/guide/highlight';
import { type GuideEntry, guideFor } from '@/lib/guide/registry';
import { cn } from '@/lib/utils';

const sideQuery = '(min-width: 64rem)';
const focusable = 'a[href], button, input, select, textarea, [tabindex]';

function besideHeader(trigger: HTMLElement | null) {
  return { top: trigger?.closest('header')?.getBoundingClientRect().bottom ?? 0 };
}

function Answer({ entry, onShow }: { entry: GuideEntry; onShow: (target: string) => void }) {
  const { t } = useTranslation();
  const target = entry.target && findGuideTarget(entry.target) ? entry.target : null;
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
 * The header's "How this works" button and the guide it opens for the current screen. Opened below `lg` it is a modal
 * bottom sheet (Android Back closes it); opened from `lg` it is a side panel under the header that leaves the page
 * usable and in the tab order, makes room for itself beside `#main` and follows the user between screens. It switches
 * mode when the window crosses `lg` while open, keeping the open answer and the focused question. Renders nothing on a screen without a guide. "Show me" closes the guide, scrolls its target
 * to the middle, rings it briefly and focuses it when it can take focus.
 */
export function Guide() {
  const { t } = useTranslation();
  const { pathname } = useLocation();
  const guide = guideFor(pathname);
  const instant = useReduceMotion();
  const [open, setOpen] = useState(false);
  const [side, setSide] = useState<{ top: number } | null>(null);
  const [answer, setAnswer] = useState({ pathname, id: '' });
  const trigger = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const pending = useRef<string | null>(null);
  const refocus = useRef<string | null>(null);

  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- closes the panel when the guide goes away
    if (!guide) setOpen(false);
  }, [guide]);

  useEffect(() => {
    if (!open || side || !env.native) return;
    let remove: (() => void) | null = null;
    let gone = false;
    void import('@capacitor/app')
      .then(({ App }) => App.addListener('backButton', () => setOpen(false)))
      .then((listener) => {
        if (gone) void listener.remove();
        else remove = () => void listener.remove();
      });
    return () => {
      gone = true;
      remove?.();
    };
  }, [open, side]);

  useEffect(() => {
    const query = window.matchMedia?.(sideQuery);
    if (!open || !query) return;
    const crossed = () => {
      const active = document.activeElement;
      refocus.current =
        active instanceof HTMLElement && panel.current?.contains(active)
          ? (active.closest<HTMLElement>('[data-entry]')?.dataset['entry'] ?? null)
          : null;
      setSide(query.matches ? besideHeader(trigger.current) : null);
    };
    query.addEventListener('change', crossed);
    return () => query.removeEventListener('change', crossed);
  }, [open]);

  useEffect(() => {
    const main = document.getElementById('main');
    if (!open || !side || !main) return;
    const fit = () => {
      main.style.paddingRight = '';
      const covered =
        (panel.current?.offsetWidth ?? 0) -
        (window.innerWidth - main.getBoundingClientRect().right);
      if (covered > 0) main.style.paddingRight = `calc(${covered}px + 1.5rem)`;
    };
    fit();
    window.addEventListener('resize', fit);
    return () => {
      window.removeEventListener('resize', fit);
      main.style.paddingRight = '';
    };
  }, [open, side]);

  if (!guide) return null;

  function change(next: boolean): void {
    if (next) {
      const wide = window.matchMedia?.(sideQuery).matches ?? false;
      setSide(wide ? besideHeader(trigger.current) : null);
      setAnswer({ pathname, id: '' });
    }
    setOpen(next);
  }

  function show(target: string): void {
    pending.current = target;
    setOpen(false);
  }

  /** Moves focus once the guide closes; after a mode switch the guide is still open and keeps its focus. */
  function closed(event: Event): void {
    event.preventDefault();
    if (panel.current) return;
    const target = pending.current ? findGuideTarget(pending.current) : null;
    pending.current = null;
    if (target?.matches(focusable)) target.focus({ preventScroll: true });
    else if (trigger.current?.isConnected) trigger.current.focus({ preventScroll: true });
    if (target) showTarget(target, instant);
  }

  function leaveByTab(event: KeyboardEvent): void {
    if (event.key === 'Tab') event.stopPropagation();
  }

  const screen = t(guide.nameKey);
  const content = (
    <Dialog.Content
      ref={panel}
      onOpenAutoFocus={(event) => {
        const entry = refocus.current;
        refocus.current = null;
        const question = entry ? panel.current?.querySelector<HTMLElement>(`[data-entry="${entry}"]`) : null;
        if (!question) return;
        event.preventDefault();
        question.focus();
      }}
      onCloseAutoFocus={closed}
      onInteractOutside={side ? (event) => event.preventDefault() : undefined}
      onEscapeKeyDown={(event) => {
        if (side && !panel.current?.contains(event.target as Node)) event.preventDefault();
      }}
      onKeyDownCapture={side ? leaveByTab : undefined}
      style={side ? { top: side.top } : undefined}
      className={cn(
        'fixed z-40 flex flex-col gap-4 overflow-y-auto border-border bg-card text-card-foreground shadow-card focus:outline-none',
        'motion-safe:animate-[ember-arrive_var(--ember-dur)_var(--ember-ease)_both]',
        side
          ? 'right-0 bottom-0 w-[24rem] border-l p-6'
          : 'inset-x-0 bottom-0 max-h-[85dvh] rounded-t-2xl border-t p-5 pb-[calc(1.25rem+env(safe-area-inset-bottom))]',
      )}
    >
      <div className="flex items-start justify-between gap-3">
        <Dialog.Title className="pt-2 text-[1.375rem]">{t('guide.title', { screen })}</Dialog.Title>
        <Dialog.Close asChild>
          <Button
            variant="ghost"
            size="icon"
            className="-mr-2 shrink-0 rounded-full"
            aria-label={t('guide.close')}
          >
            <X aria-hidden />
          </Button>
        </Dialog.Close>
      </div>
      <Dialog.Description className="text-muted-foreground">{t(guide.ledeKey)}</Dialog.Description>
      <Accordion.Root
        type="single"
        collapsible
        value={answer.pathname === pathname ? answer.id : ''}
        onValueChange={(id) => setAnswer({ pathname, id })}
        className="flex flex-col divide-y divide-border border-y border-border"
      >
        {guide.entries.map((entry) => (
          <Accordion.Item key={entry.id} value={entry.id}>
            <Accordion.Header>
              <Accordion.Trigger data-entry={entry.id} className="group flex min-h-[48px] w-full cursor-pointer items-center justify-between gap-3 py-3 text-left font-semibold">
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
      <p className="flex flex-wrap items-center gap-x-2 text-muted-foreground">
        {t('guide.stillUnclear')}
        <TapLink to="/feedback" onClick={() => setOpen(false)}>
          {t('guide.sendFeedback')}
        </TapLink>
      </p>
    </Dialog.Content>
  );
  let body: ReactNode = content;
  if (!side) {
    body = (
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-[color-mix(in_oklch,var(--foreground)_35%,transparent)]" />
        {content}
      </Dialog.Portal>
    );
  }

  return (
    <Dialog.Root open={open} onOpenChange={change} modal={!side}>
      <Dialog.Trigger asChild>
        <Button
          ref={trigger}
          variant="ghost"
          size="icon"
          className="rounded-full text-muted-foreground"
          aria-label={t('guide.open')}
        >
          <CircleHelp aria-hidden className="size-[22px]" />
        </Button>
      </Dialog.Trigger>
      {body}
    </Dialog.Root>
  );
}
