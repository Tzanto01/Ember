import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { Button } from '@/components/ui/button';
import { useFireside } from '@/hooks/use-fireside';
import { firesideApi, isAsking, patchFireside } from '@/lib/fireside';

/**
 * The ask step at the end of a moment (screen 7): "Let your fireside know you could use some warmth?". Shown only
 * while Fireside is on, with a friend, and not already asking, judged from the Fireside already loaded this session
 * (the moment fetches nothing itself); never blocks leaving the moment, and a failed request only says so quietly.
 */
export function FiresideAsk() {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const { data } = useFireside({ cacheOnly: true });
  const [state, setState] = useState<'open' | 'busy' | 'asked' | 'failed' | 'closed'>('open');
  const [shown, setShown] = useState(false);
  const sectionRef = useRef<HTMLElement>(null);

  const wanted = !!data?.enabled && data.friends.length > 0 && !isAsking(data);
  if (state === 'closed' || (!shown && !wanted) || !data) return null;
  if (!shown) setShown(true);

  const names = data.friends.slice(0, 3).map((f) => f.name);
  if (data.friends.length > 3) names.push(t('fireside.ask.others'));
  const list = new Intl.ListFormat(i18n.language, { type: 'conjunction' }).format(names);

  function close(): void {
    sectionRef.current?.parentElement?.querySelector<HTMLElement>('h1')?.focus();
    setState('closed');
  }

  async function ask(): Promise<void> {
    if (state === 'busy') return;
    setState('busy');
    try {
      const { askingUntil } = await firesideApi.ask();
      patchFireside(queryClient, (old) => ({ ...old, askingUntil }));
      setState('asked');
    } catch {
      setState('failed');
    }
  }

  return (
    <section
      ref={sectionRef}
      aria-labelledby="fireside-ask-heading"
      data-testid="fireside-ask"
      className="flex flex-col gap-3 rounded-lg bg-secondary p-4 text-secondary-foreground"
    >
      <h2 id="fireside-ask-heading" className="text-[1.125rem]">
        {t('fireside.ask.title')}
      </h2>
      {state === 'asked' ? (
        <p role="status">{t('fireside.ask.done')}</p>
      ) : (
        <>
          <p className="[overflow-wrap:anywhere]">{t('fireside.ask.names', { names: list, count: names.length })}</p>
          {state === 'failed' && <p role="status">{t('fireside.ask.failed')}</p>}
          <div className="flex flex-wrap gap-2">
            <BusyButton type="button" busy={state === 'busy'} busyLabel={t('fireside.saving')} onClick={() => void ask()}>
              {t('fireside.ask.yes')}
            </BusyButton>
            <Button type="button" variant="ghost" onClick={close}>
              {t('fireside.ask.no')}
            </Button>
          </div>
        </>
      )}
    </section>
  );
}
