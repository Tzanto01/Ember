import { Timer } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { LiveRegion } from '@/components/live-region';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import { asApiError, problemToMessage } from '@/lib/problem';
import { auth, habitTimer } from '@/lib/services';
import { useStore } from '@/lib/store';
import type { TimerOffer, TimerRecord } from '@/lib/timer/habit-timer';

/** How long the offer may take before the notice shows its buttons without it. */
const slowMs = 1000;

interface NoticeState {
  offer: TimerOffer | null;
  problem: string | null;
}

function EndedTimer({ record, onDone }: { record: TimerRecord; onDone: (message: string) => void }) {
  const { t } = useTranslation();
  const { name, habitId, date } = record;
  const root = useRef<HTMLElement>(null);
  const okay = useRef<HTMLButtonElement>(null);
  const confirming = useRef(false);
  const [{ offer, problem }, setState] = useState<NoticeState>({ offer: null, problem: null });
  const [busy, setBusy] = useState(false);
  const [slow, setSlow] = useState(false);
  const today = useStore(auth.me)?.localToday;
  const canCheckIn = offer?.canCheckIn === true;

  useEffect(() => onDone(''), [onDone]);

  useEffect(() => {
    let current = true;
    const load = () => {
      if (document.visibilityState === 'hidden') return;
      void habitTimer.offer().then((next) => {
        if (current && !confirming.current) setState((state) => ({ ...state, offer: next }));
      });
    };
    load();
    window.addEventListener('focus', load);
    document.addEventListener('visibilitychange', load);
    return () => {
      current = false;
      window.removeEventListener('focus', load);
      document.removeEventListener('visibilitychange', load);
    };
  }, [habitId, date]);

  useEffect(() => {
    const timer = setTimeout(() => setSlow(true), slowMs);
    return () => clearTimeout(timer);
  }, []);

  const waiting = !offer && slow;
  const primary = canCheckIn || waiting;
  const label = t((offer ? offer.yesterday : date !== today) ? 'timer.notice.markYesterday' : 'timer.notice.mark');

  const hadPrimary = useRef(false);
  useEffect(() => {
    if (hadPrimary.current && !primary && document.activeElement === document.body) okay.current?.focus();
    hadPrimary.current = primary;
  }, [primary]);

  function leave(): void {
    document.getElementById('main')?.focus({ preventScroll: true });
  }

  async function confirm(): Promise<void> {
    if (confirming.current) return;
    confirming.current = true;
    setBusy(true);
    setState((state) => ({ ...state, problem: null }));
    const hadFocus = root.current?.contains(document.activeElement) === true;
    const result = await habitTimer.confirm();
    confirming.current = false;
    setBusy(false);
    if (result.status === 'done') {
      onDone(t('timer.notice.done', { name }));
      if (hadFocus) leave();
    } else if (result.status === 'unavailable') {
      setState({ offer: { canCheckIn: false, yesterday: false }, problem: null });
    } else {
      const error = asApiError(result.error);
      if (!error.handled) setState((state) => ({ ...state, problem: problemToMessage(error).message }));
    }
  }

  function dismiss(): void {
    if (confirming.current) return;
    habitTimer.stop();
    leave();
  }

  return (
    <section
      ref={root}
      aria-label={t('timer.notice.label')}
      className="flex items-start gap-3 rounded-md border border-border bg-card px-4 py-3 text-card-foreground shadow-card [@media(min-height:36rem)]:sticky [@media(min-height:36rem)]:top-[calc(56px+env(safe-area-inset-top)+0.5rem)] [@media(min-height:36rem)]:z-[9]"
    >
      <Timer aria-hidden className="mt-[0.1875rem] size-[1.125rem] shrink-0 text-muted-foreground" />
      <div className="flex min-w-0 flex-1 flex-col gap-2 [overflow-wrap:anywhere] hyphens-auto">
        <p className="max-w-[38rem]">{t('timer.notice.up', { name })}</p>
        {problem && (
          <Notice tone="notice" heading={t('today.problem.checkIn', { name })}>
            <p>{problem}</p>
          </Notice>
        )}
        <div className="grid min-h-[44px] w-full max-w-[28rem] grid-cols-1 gap-2 sm:grid-cols-2">
          {primary ? (
            <BusyButton
              type="button"
              busy={busy || waiting}
              busyLabel={busy ? t('timer.notice.saving') : label}
              onClick={() => void confirm()}
            >
              {label}
            </BusyButton>
          ) : (
            offer && slow && <span aria-hidden="true" className="min-h-[44px]" />
          )}
          {(primary || offer) && (
            <Button
              ref={okay}
              type="button"
              variant={primary ? 'ghost' : 'secondary'}
              aria-disabled={busy || undefined}
              onClick={dismiss}
            >
              {t(primary ? 'timer.notice.notNow' : 'shell.okay')}
            </Button>
          )}
        </div>
      </div>
    </section>
  );
}

/**
 * Says gently that the habit timer is up, once, and offers "Mark as done" only when its day can still be checked in.
 * It checks nothing in without that tap. Buttons wait for the offer; after {@link slowMs} they show
 * with "Mark as done" still busy, so "Not now" always works and each keeps its place.
 */
export function TimerNotice() {
  const { t } = useTranslation();
  const view = useStore(habitTimer.view);
  const [done, setDone] = useState('');
  const ended = view?.phase === 'ended' ? view.record : null;
  return (
    <>
      <LiveRegion message={ended ? t('timer.notice.up', { name: ended.name }) : done} />
      {ended && <EndedTimer key={`${ended.habitId}-${ended.endsAt}`} record={ended} onDone={setDone} />}
    </>
  );
}
