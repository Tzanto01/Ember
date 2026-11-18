import { useQueryClient } from '@tanstack/react-query';
import { type FormEvent, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { FormField } from '@/components/form-field';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Notice } from '@/components/ui/notice';
import {
  type Fireside,
  type FiresideCode,
  firesideApi,
  firesideCodeLength,
  firesideKey,
  formatCode,
  isCodeShaped,
  normalizeCode,
  patchFireside,
  putFriend,
} from '@/lib/fireside';
import { firesideLimits } from '@/lib/fireside-limits';
import { asApiError, problemToMessage } from '@/lib/problem';

const minute = 60_000;
const offDetail = 'Turn on Fireside first.';

function minutesLeft(code: FiresideCode | null, now: number): number {
  return code ? Math.ceil((Date.parse(code.expiresAt) - now) / minute) : 0;
}

/** Re-renders every few seconds while `on`, for the code's remaining minutes. */
function useNow(on: boolean): number {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    if (!on) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 5000);
    return () => clearInterval(timer);
  }, [on]);
  return now;
}

/**
 * "Add a friend" (screen 2): the caller's code with its remaining minutes and "New code" (or "Make a code"), and
 * "Got a code from someone?" with "Join their fireside". `onAnnounce` gets text for the page's live region.
 */
export function AddFriend({ fireside, onAnnounce }: { fireside: Fireside; onAnnounce: (text: string) => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const now = useNow(!!fireside.code);
  const left = minutesLeft(fireside.code, now);
  const code = left > 0 ? fireside.code : null;
  const [making, setMaking] = useState(false);
  const [codeProblem, setCodeProblem] = useState<string | null>(null);
  const [typed, setTyped] = useState('');
  const [joinError, setJoinError] = useState<string | null>(null);
  const [joinProblem, setJoinProblem] = useState<string | null>(null);
  const [joining, setJoining] = useState(false);
  const [joined, setJoined] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  const joinedRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (joined) joinedRef.current?.focus();
  }, [joined]);

  async function makeCode(): Promise<void> {
    if (making) return;
    setMaking(true);
    setCodeProblem(null);
    try {
      const made = await firesideApi.makeCode();
      patchFireside(queryClient, (old) => ({ ...old, code: made }));
      onAnnounce(t('fireside.code.madeAnnounce', { code: formatCode(made.code) }));
    } catch (error) {
      const apiError = asApiError(error);
      if (!apiError.handled) setCodeProblem(problemToMessage(apiError).message);
    } finally {
      setMaking(false);
    }
  }

  async function join(event: FormEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();
    if (joining) return;
    const value = normalizeCode(typed);
    setJoinProblem(null);
    if (value.length !== firesideCodeLength || !isCodeShaped(value)) {
      setJoinError(t('fireside.join.format', { length: firesideCodeLength }));
      inputRef.current?.focus();
      return;
    }
    setJoinError(null);
    setJoining(true);
    try {
      const friend = await firesideApi.join(value);
      putFriend(queryClient, friend);
      setTyped('');
      setJoined(friend.name);
      onAnnounce(t('fireside.join.joined', { name: friend.name }));
    } catch (error) {
      const apiError = asApiError(error);
      if (apiError.handled) return;
      if (apiError.status === 400) {
        const friendly = problemToMessage(apiError);
        setJoinError(friendly.fields['code'] ? t('fireside.join.invalid') : friendly.message);
        inputRef.current?.focus();
      } else if (apiError.status === 409) {
        setJoinProblem(
          apiError.problem?.detail === offDetail
            ? t('fireside.join.off')
            : t('fireside.join.full', { max: firesideLimits.friends }),
        );
      } else if (apiError.status === 429) {
        setJoinProblem(t('fireside.join.tooMany'));
      } else {
        setJoinProblem(problemToMessage(apiError).message);
      }
    } finally {
      setJoining(false);
      void queryClient.invalidateQueries({ queryKey: firesideKey });
    }
  }

  return (
    <section id="fireside-add" aria-labelledby="fireside-add-heading" className="flex flex-col gap-4" data-testid="fireside-add">
      <div className="flex flex-col gap-1">
        <h2 id="fireside-add-heading" className="text-[1.375rem]">
          {t('fireside.add.title')}
        </h2>
        <p className="text-muted-foreground">{t('fireside.add.lede')}</p>
      </div>
      <div className="flex flex-col gap-3 rounded-lg bg-secondary p-4 text-secondary-foreground">
        <p className="font-medium">{t('fireside.code.yours')}</p>
        {code ? (
          <>
            <p
              data-testid="fireside-code"
              className="font-mono text-[1.75rem] font-bold tracking-[0.15em] select-all"
            >
              {formatCode(code.code)}
            </p>
            <p className="text-[0.875rem]">
              {left >= firesideLimits.codeMinutes
                ? t('fireside.code.fresh', { minutes: firesideLimits.codeMinutes })
                : t('fireside.code.left', { count: left })}
            </p>
          </>
        ) : (
          <p className="text-[0.875rem]">{t('fireside.code.none')}</p>
        )}
        <div>
          <BusyButton
            type="button"
            variant={code ? 'outline' : 'default'}
            busy={making}
            busyLabel={t('fireside.code.making')}
            onClick={() => void makeCode()}
          >
            {code ? t('fireside.code.new') : t('fireside.code.make')}
          </BusyButton>
        </div>
        {codeProblem && (
          <Notice tone="notice">
            <p>{codeProblem}</p>
          </Notice>
        )}
      </div>
      {joined ? (
        <div
          ref={joinedRef}
          tabIndex={-1}
          className="flex flex-col items-start gap-2 rounded-md bg-success px-4 py-3 text-success-foreground focus:outline-none"
        >
          <p className="font-semibold [overflow-wrap:anywhere]">{t('fireside.join.joined', { name: joined })}</p>
          <p className="[overflow-wrap:anywhere]">{t('fireside.join.joinedText', { name: joined })}</p>
          <Button
            variant="ghost"
            onClick={() => {
              setJoined(null);
              requestAnimationFrame(() => inputRef.current?.focus());
            }}
          >
            {t('fireside.join.done')}
          </Button>
        </div>
      ) : (
        <form noValidate onSubmit={(event) => void join(event)} className="flex flex-col gap-3">
          <FormField label={t('fireside.join.label')} error={joinError}>
            {(field) => (
              <Input
                {...field}
                ref={inputRef}
                value={typed}
                autoComplete="off"
                autoCapitalize="characters"
                autoCorrect="off"
                spellCheck={false}
                maxLength={64}
                className="font-mono tracking-[0.1em]"
                onChange={(event) => setTyped(event.target.value)}
              />
            )}
          </FormField>
          {joinProblem && (
            <Notice tone="notice">
              <p>{joinProblem}</p>
            </Notice>
          )}
          <div>
            <BusyButton type="submit" variant="secondary" busy={joining} busyLabel={t('fireside.join.joining')}>
              {t('fireside.join.submit')}
            </BusyButton>
          </div>
        </form>
      )}
      <p className="text-[0.875rem] text-muted-foreground">{t('fireside.add.quiet')}</p>
    </section>
  );
}
