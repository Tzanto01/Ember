import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';
import { BusyButton } from '@/components/auth/busy-button';
import { BackLink } from '@/components/back-link';
import { FriendAvatar } from '@/components/fireside/avatar';
import { FiresideGate } from '@/components/fireside/fireside-gate';
import { PageHeader } from '@/components/page-header';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { Label } from '@/components/ui/label';
import { Notice } from '@/components/ui/notice';
import { Switch } from '@/components/ui/switch';
import { type Fireside, firesideApi, firesideKey, patchFireside, putFriend } from '@/lib/fireside';
import { asApiError, problemToMessage } from '@/lib/problem';

function FriendScreen({ fireside, id }: { fireside: Fireside; id: number }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const friend = fireside.friends.find((f) => f.id === id);
  const [busy, setBusy] = useState<'flame' | 'mute' | 'remove' | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [confirm, setConfirm] = useState(false);
  const confirmRef = useRef<HTMLParagraphElement>(null);
  const removeRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (confirm) confirmRef.current?.focus();
  }, [confirm]);

  const back = <BackLink to="/fireside" label={t('fireside.title')} name={t('fireside.backName')} />;

  if (!friend) {
    return (
      <div className="flex max-w-[40rem] flex-col gap-6">
        {back}
        <PageHeader title={t('fireside.friend.goneTitle')} lede={t('fireside.friend.gone')} />
      </div>
    );
  }

  async function act(kind: 'flame' | 'mute' | 'remove', action: () => Promise<void>): Promise<void> {
    if (busy) return;
    setBusy(kind);
    setProblem(null);
    try {
      await action();
    } catch (error) {
      const apiError = asApiError(error);
      if (apiError.status === 404) void queryClient.invalidateQueries({ queryKey: firesideKey });
      else if (!apiError.handled) setProblem(problemToMessage(apiError).message);
    } finally {
      setBusy(null);
    }
  }

  function patch(kind: 'flame' | 'mute', body: { muted?: boolean; shareFlame?: boolean }): void {
    void act(kind, async () => putFriend(queryClient, await firesideApi.patchFriend(id, body)));
  }

  function remove(): void {
    void act('remove', async () => {
      await firesideApi.remove(id, false);
      await navigate('/fireside', { replace: true });
      patchFireside(queryClient, (old) => ({
        ...old,
        friends: old.friends.filter((f) => f.id !== id),
        warmth: old.warmth.filter((w) => w.friendId !== id),
      }));
    });
  }

  function cancel(): void {
    flushSync(() => setConfirm(false));
    removeRef.current?.focus();
  }

  const name = friend.name;
  return (
    <div className="flex max-w-[40rem] flex-col gap-6" data-testid="fireside-friend-page">
      {back}
      <div className="flex items-center gap-4">
        <FriendAvatar name={name} className="size-[56px] text-[1.5rem]" />
        <PageHeader title={name} className="min-w-0" />
      </div>
      {problem && (
        <Notice tone="notice">
          <p>{problem}</p>
        </Notice>
      )}
      <Card>
        <h2 className="text-[1.375rem] [overflow-wrap:anywhere]">{t('fireside.friend.sees', { name })}</h2>
        <dl className="flex flex-col gap-3">
          <div className="flex flex-wrap items-baseline justify-between gap-x-4">
            <dt>{t('fireside.friend.yourName')}</dt>
            <dd className="text-muted-foreground">{t('fireside.friend.always')}</dd>
          </div>
          <div className="flex flex-wrap items-baseline justify-between gap-x-4">
            <dt>{t('fireside.friend.ask')}</dt>
            <dd className="text-muted-foreground">{t('fireside.friend.askWhen')}</dd>
          </div>
        </dl>
        <div className="flex min-h-[44px] items-center justify-between gap-4">
          <Label htmlFor="fireside-share-flame" className="min-w-0 font-normal">
            {t('fireside.friend.flame')}
          </Label>
          <Switch
            id="fireside-share-flame"
            aria-describedby="fireside-share-flame-hint"
            checked={friend.shareFlame}
            aria-disabled={busy !== null || undefined}
            onCheckedChange={(on) => patch('flame', { shareFlame: on })}
          />
        </div>
        <p id="fireside-share-flame-hint" className="text-[0.875rem] text-muted-foreground">
          {t('fireside.friend.flameHint')}
        </p>
      </Card>
      <Card>
        <h2 className="text-[1.375rem]">{t('fireside.friend.quiet')}</h2>
        <div className="flex min-h-[44px] items-center justify-between gap-4">
          <Label htmlFor="fireside-mute" className="min-w-0 font-normal [overflow-wrap:anywhere]">
            {t('fireside.friend.mute', { name })}
          </Label>
          <Switch
            id="fireside-mute"
            aria-describedby="fireside-mute-hint"
            checked={friend.muted}
            aria-disabled={busy !== null || undefined}
            onCheckedChange={(on) => patch('mute', { muted: on })}
          />
        </div>
        <p id="fireside-mute-hint" className="text-[0.875rem] text-muted-foreground [overflow-wrap:anywhere]">
          {t('fireside.friend.muteHint', { name })}
        </p>
        <div className="flex flex-col items-start gap-2 border-t border-border pt-3">
          {confirm ? (
            <div className="flex w-full flex-col gap-3 rounded-md border-[length:var(--border-width)] border-border p-3">
              <p ref={confirmRef} tabIndex={-1} className="focus:outline-none [overflow-wrap:anywhere]">
                {t('fireside.friend.removeConfirm', { name })}
              </p>
              <div className="flex flex-wrap gap-2">
                <BusyButton
                  type="button"
                  variant="destructive"
                  busy={busy === 'remove'}
                  busyLabel={t('fireside.removing')}
                  onClick={remove}
                >
                  {t('fireside.friend.removeYes')}
                </BusyButton>
                <Button type="button" variant="ghost" onClick={cancel}>
                  {t('fireside.friend.removeNo', { name })}
                </Button>
              </div>
            </div>
          ) : (
            <Button ref={removeRef} type="button" variant="outline" onClick={() => setConfirm(true)}>
              {t('fireside.friend.remove')}
            </Button>
          )}
          <p className="text-[0.875rem] text-muted-foreground [overflow-wrap:anywhere]">
            {t('fireside.friend.removeHint', { name })}
          </p>
        </div>
      </Card>
    </div>
  );
}

/** `/fireside/:id`: what this friend sees (name, the ask, the flame switch), mute, and remove with a confirm. */
export default function FiresideFriendPage() {
  const id = Number(useParams()['id']);
  return <FiresideGate>{(fireside) => <FriendScreen fireside={fireside} id={id} />}</FiresideGate>;
}
