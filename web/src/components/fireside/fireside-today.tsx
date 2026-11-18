import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { FlameMark } from '@/components/flame-mark';
import { LiveRegion } from '@/components/live-region';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { Label } from '@/components/ui/label';
import { Notice } from '@/components/ui/notice';
import { Switch } from '@/components/ui/switch';
import { firesideApi, firesideKey, isAsking, patchFireside, useFireside, warmthText } from '@/lib/fireside';
import { asApiError, problemToMessage } from '@/lib/problem';
import { SendSheet } from './send-sheet';

/**
 * Fireside on Today: a card per warmth received (screen 6, "Send some back" and "Okay") and the "Could use some
 * warmth today" switch (screen 7) once there is a friend. Nothing while Fireside is off or in no-account mode.
 */
export function FiresideToday() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { data } = useFireside();
  const [sendTo, setSendTo] = useState<number | null>(null);
  const [askBusy, setAskBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');

  if (!data?.enabled) return null;
  const asking = isAsking(data);
  const friend = data.friends.find((f) => f.id === sendTo) ?? null;

  function dismiss(id: number): void {
    patchFireside(queryClient, (old) => ({ ...old, warmth: old.warmth.filter((w) => w.id !== id) }));
    firesideApi.dismiss(id).catch(() => queryClient.invalidateQueries({ queryKey: firesideKey }));
  }

  async function changeAsk(on: boolean): Promise<void> {
    if (askBusy) return;
    setAskBusy(true);
    setProblem(null);
    try {
      if (on) {
        const { askingUntil } = await firesideApi.ask();
        patchFireside(queryClient, (old) => ({ ...old, askingUntil }));
      } else {
        await firesideApi.stopAsking();
        patchFireside(queryClient, (old) => ({ ...old, askingUntil: null }));
      }
    } catch (error) {
      const apiError = asApiError(error);
      if (!apiError.handled) setProblem(problemToMessage(apiError).message);
    } finally {
      setAskBusy(false);
    }
  }

  return (
    <>
      {data.warmth.map((warmth) => {
        const from = data.friends.find((f) => f.id === warmth.friendId);
        return (
          <Card key={warmth.id} data-testid="fireside-warmth" className="gap-2">
            <div className="flex items-center gap-2">
              <FlameMark className="size-[22px]" />
              <h2 className="min-w-0 text-[1.125rem] [overflow-wrap:anywhere]">
                {t('fireside.warmth.from', { name: warmth.name })}
              </h2>
            </div>
            <p className="font-display text-[1.375rem]">{warmthText(warmth.message)}</p>
            <div className="flex flex-wrap gap-2">
              {from?.canSendWarmth && (
                <Button variant="secondary" onClick={() => setSendTo(from.id)}>
                  {t('fireside.warmth.sendBack')}
                </Button>
              )}
              <Button
                variant="ghost"
                aria-label={t('fireside.warmth.okayName', { name: warmth.name })}
                onClick={() => dismiss(warmth.id)}
              >
                {t('fireside.okay')}
              </Button>
            </div>
            <p className="text-[0.875rem] text-muted-foreground [overflow-wrap:anywhere]">
              {t('fireside.warmth.quiet', { name: warmth.name })}
            </p>
          </Card>
        );
      })}
      {data.friends.length > 0 && (
        <Card data-testid="fireside-ask-today" className="gap-2">
          <div className="flex min-h-[44px] items-center justify-between gap-4">
            <Label htmlFor="fireside-asking" className="min-w-0 font-normal">
              {t('fireside.asking')}
            </Label>
            <Switch
              id="fireside-asking"
              aria-describedby="fireside-asking-hint"
              checked={asking}
              aria-disabled={askBusy || undefined}
              onCheckedChange={(on) => void changeAsk(on)}
            />
          </div>
          <p id="fireside-asking-hint" className="text-[0.875rem] text-muted-foreground">
            {t('fireside.ask.todayHint')}
          </p>
          {problem && (
            <Notice tone="notice">
              <p>{problem}</p>
            </Notice>
          )}
        </Card>
      )}
      <LiveRegion message={announcement} />
      <SendSheet friend={friend} onClose={() => setSendTo(null)} onSent={setAnnouncement} />
    </>
  );
}
