import { useQueryClient } from '@tanstack/react-query';
import { ChevronRight } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { BusyButton } from '@/components/auth/busy-button';
import { FlameMark } from '@/components/flame-mark';
import { Button } from '@/components/ui/button';
import { Card } from '@/components/ui/card';
import { Notice } from '@/components/ui/notice';
import { type FiresideFriend, firesideApi, firesideKey, patchFireside, putFriend } from '@/lib/fireside';
import { asApiError, problemToMessage } from '@/lib/problem';
import { FriendAvatar } from './avatar';

type Busy = 'rename' | 'joined' | 'remove';

function useFriendAction(onAnnounce: (text: string) => void) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState<{ id: number; kind: Busy } | null>(null);
  const [problem, setProblem] = useState<{ id: number; text: string } | null>(null);

  async function run(friend: FiresideFriend, kind: Busy): Promise<void> {
    if (busy) return;
    setBusy({ id: friend.id, kind });
    setProblem(null);
    try {
      if (kind === 'remove') {
        await firesideApi.remove(friend.id, true);
        patchFireside(queryClient, (old) => ({
          ...old,
          code: null,
          friends: old.friends.filter((f) => f.id !== friend.id),
          warmth: old.warmth.filter((w) => w.friendId !== friend.id),
        }));
        onAnnounce(t('fireside.joinedNotice.removed', { name: friend.name }));
      } else {
        putFriend(queryClient, await firesideApi.acknowledge(friend.id, { [kind]: true }));
      }
    } catch (error) {
      const apiError = asApiError(error);
      if (apiError.status === 404) void queryClient.invalidateQueries({ queryKey: firesideKey });
      else if (!apiError.handled) setProblem({ id: friend.id, text: problemToMessage(apiError).message });
    } finally {
      setBusy(null);
    }
  }

  return { busy, problem, run };
}

/**
 * The joined notices (screen 3) and rename notices of the caller's friends: "Okay" clears one, "Not someone I know"
 * removes that friend and turns the caller's code off.
 */
export function FriendNotices({
  friends,
  onAnnounce,
}: {
  friends: FiresideFriend[];
  onAnnounce: (text: string) => void;
}) {
  const { t } = useTranslation();
  const { busy, problem, run } = useFriendAction(onAnnounce);
  const joined = friends.filter((f) => f.joinedViaMyCode);
  const renamed = friends.filter((f) => f.renamedFrom);
  if (joined.length === 0 && renamed.length === 0) return null;

  const failure = (friend: FiresideFriend) =>
    problem?.id === friend.id && (
      <Notice tone="notice">
        <p>{problem.text}</p>
      </Notice>
    );

  return (
    <div className="flex flex-col gap-3">
      {joined.map((friend) => (
        <Card key={`joined-${friend.id}`} data-testid="fireside-joined-notice">
          <div className="flex items-center gap-3">
            <FriendAvatar name={friend.name} />
            <h2 className="min-w-0 text-[1.125rem] [overflow-wrap:anywhere]">
              {t('fireside.joinedNotice.title', { name: friend.name })}
            </h2>
          </div>
          <div className="flex flex-wrap gap-2">
            <BusyButton
              type="button"
              busy={busy?.id === friend.id && busy.kind === 'joined'}
              busyLabel={t('fireside.saving')}
              onClick={() => void run(friend, 'joined')}
            >
              {t('fireside.okay')}
            </BusyButton>
            <BusyButton
              type="button"
              variant="ghost"
              busy={busy?.id === friend.id && busy.kind === 'remove'}
              busyLabel={t('fireside.removing')}
              onClick={() => void run(friend, 'remove')}
            >
              {t('fireside.joinedNotice.notKnown')}
            </BusyButton>
          </div>
          <p className="text-[0.875rem] text-muted-foreground">{t('fireside.joinedNotice.explain')}</p>
          {failure(friend)}
        </Card>
      ))}
      {renamed.map((friend) => (
        <Notice key={`renamed-${friend.id}`} tone="info" data-testid="fireside-rename-notice">
          <p className="[overflow-wrap:anywhere]">
            {t('fireside.renamed', { old: friend.renamedFrom, name: friend.name })}
          </p>
          <BusyButton
            type="button"
            variant="ghost"
            busy={busy?.id === friend.id && busy.kind === 'rename'}
            busyLabel={t('fireside.saving')}
            onClick={() => void run(friend, 'rename')}
          >
            {t('fireside.okay')}
          </BusyButton>
          {failure(friend)}
        </Notice>
      ))}
    </div>
  );
}

/** The friends list (screen 4): name, the ask line, the flame when shared, and Send warmth or "Sent today". */
export function FriendList({ friends, onSend }: { friends: FiresideFriend[]; onSend: (friend: FiresideFriend) => void }) {
  const { t } = useTranslation();
  if (friends.length === 0) return <p className="text-muted-foreground">{t('fireside.friends.empty')}</p>;
  return (
    <ul
      aria-labelledby="fireside-friends-heading"
      className="flex flex-col divide-y divide-border overflow-hidden rounded-xl border-[length:var(--border-width)] border-border bg-card text-card-foreground shadow-card"
    >
      {friends.map((friend) => (
        <li key={friend.id} data-testid="fireside-friend" className="flex flex-wrap items-center gap-3 px-4 py-3">
          <Link
            to={`/fireside/${friend.id}`}
            className="-my-1 flex min-h-[44px] min-w-0 flex-1 basis-[12rem] items-center gap-3 rounded-md text-card-foreground no-underline hover:text-primary"
          >
            <FriendAvatar name={friend.name} />
            <span className="flex min-w-0 flex-col">
              <span className="flex items-center gap-1.5 font-semibold [overflow-wrap:anywhere]">
                {friend.name}
                {friend.checkedInToday === true && (
                  <>
                    <FlameMark className="size-[16px]" />
                    <span className="sr-only">{t('fireside.friends.checkedIn')}</span>
                  </>
                )}
              </span>
              {friend.asking && <span className="text-[0.875rem] text-muted-foreground">{t('fireside.asking')}</span>}
            </span>
            <ChevronRight aria-hidden className="ml-auto size-[18px] shrink-0 text-muted-foreground" />
          </Link>
          {friend.canSendWarmth ? (
            <Button
              variant="secondary"
              aria-label={t('fireside.friends.sendTo', { name: friend.name })}
              onClick={() => onSend(friend)}
            >
              {t('fireside.friends.send')}
            </Button>
          ) : (
            <span className="inline-flex min-h-[44px] items-center px-2 text-[0.875rem] text-muted-foreground">
              {t('fireside.friends.sent')}
            </span>
          )}
        </li>
      ))}
    </ul>
  );
}
