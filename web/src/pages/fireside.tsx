import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { AddFriend } from '@/components/fireside/add-friend';
import { FiresideGate } from '@/components/fireside/fireside-gate';
import { FriendList, FriendNotices } from '@/components/fireside/friends';
import { SendSheet } from '@/components/fireside/send-sheet';
import { LiveRegion } from '@/components/live-region';
import { PageHeader } from '@/components/page-header';
import { Card } from '@/components/ui/card';
import { Label } from '@/components/ui/label';
import { Notice } from '@/components/ui/notice';
import { Switch } from '@/components/ui/switch';
import { env } from '@/lib/env';
import { type Fireside, firesideApi, firesideKey } from '@/lib/fireside';
import { asApiError, problemToMessage } from '@/lib/problem';

function FiresideScreen({ fireside }: { fireside: Fireside }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [sendTo, setSendTo] = useState<number | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [notifyBusy, setNotifyBusy] = useState(false);
  const [notifyProblem, setNotifyProblem] = useState<string | null>(null);
  const friend = fireside.friends.find((f) => f.id === sendTo) ?? null;

  function announce(text: string): void {
    setAnnouncement('');
    requestAnimationFrame(() => setAnnouncement(text));
  }

  async function changeNotify(notify: boolean): Promise<void> {
    if (notifyBusy) return;
    setNotifyBusy(true);
    setNotifyProblem(null);
    try {
      queryClient.setQueryData(firesideKey, await firesideApi.put({ enabled: true, notify }));
    } catch (error) {
      const apiError = asApiError(error);
      if (!apiError.handled) setNotifyProblem(problemToMessage(apiError).message);
    } finally {
      setNotifyBusy(false);
    }
  }

  return (
    <div className="flex max-w-[40rem] flex-col gap-6" data-testid="fireside-page">
      <PageHeader title={t('fireside.title')} />
      <LiveRegion message={announcement} />
      <FriendNotices friends={fireside.friends} onAnnounce={announce} />
      <section aria-labelledby="fireside-friends-heading" className="flex flex-col gap-3">
        <h2 id="fireside-friends-heading" className="sr-only">
          {t('fireside.friends.title')}
        </h2>
        <FriendList friends={fireside.friends} onSend={(f) => setSendTo(f.id)} />
        <p className="text-[0.875rem] text-muted-foreground">{t('fireside.friends.helper')}</p>
      </section>
      <Card>
        <AddFriend fireside={fireside} onAnnounce={announce} />
      </Card>
      <Card>
        <div className="flex min-h-[44px] items-center justify-between gap-4">
          <Label htmlFor="fireside-notify" className="min-w-0 font-normal">
            {t('fireside.notify.label')}
          </Label>
          <Switch
            id="fireside-notify"
            aria-describedby="fireside-notify-hint"
            checked={fireside.notify}
            aria-disabled={notifyBusy || undefined}
            onCheckedChange={(on) => void changeNotify(on)}
          />
        </div>
        <p id="fireside-notify-hint" className="text-[0.875rem] text-muted-foreground">
          {env.native ? t('fireside.notify.android') : t('fireside.notify.hint')}
        </p>
        {notifyProblem && (
          <Notice tone="notice">
            <p>{notifyProblem}</p>
          </Notice>
        )}
      </Card>
      <SendSheet friend={friend} onClose={() => setSendTo(null)} onSent={announce} />
    </div>
  );
}

/** `/fireside`: notices, friends with Send warmth, add a friend with a code, and the notifications switch. */
export default function FiresidePage() {
  return <FiresideGate>{(fireside) => <FiresideScreen fireside={fireside} />}</FiresideGate>;
}
