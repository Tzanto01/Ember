import { Check } from 'lucide-react';
import { createContext, type ReactNode, useContext, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Notice, type NoticeTone } from '@/components/ui/notice';
import type { MePatch } from '@/lib/models';
import { type ApiError, asApiError, problemToMessage } from '@/lib/problem';
import { api, auth } from '@/lib/services';

/** A part of Settings that runs its own action and shows its own outcome. */
export type SettingsSection =
  | 'timezone'
  | 'grace'
  | 'streaks'
  | 'habitNotes'
  | 'calm'
  | 'second'
  | 'discord'
  | 'dm'
  | 'push'
  | 'appearance'
  | 'export'
  | 'consent'
  | 'password'
  | 'signout'
  | 'everywhere'
  | 'delete';

export interface SettingsMessage {
  tone: NoticeTone;
  text: string;
}

/**
 * What the Settings cards share: one action at a time across every card, one "Saved." mark, one outcome message, and
 * every `PATCH /api/me` in order.
 */
export interface SettingsStatus {
  /** The section whose action is running. */
  busy: SettingsSection | null;
  /** The outcome message and the section it belongs to; starting any action clears it. */
  message: (SettingsMessage & { section: SettingsSection }) | null;
  /** Whether an action is running right now, also within the frame it started in. */
  isBusy: () => boolean;
  start: (section: SettingsSection) => void;
  finish: () => void;
  markSaved: (section: SettingsSection) => void;
  show: (section: SettingsSection, message: SettingsMessage) => void;
  /** Shows the problem as this section's outcome unless it was handled already. */
  fail: (section: SettingsSection, error: ApiError) => void;
  /** The section's outcome notice, if the message is its. */
  outcome: (section: SettingsSection) => ReactNode;
  /** The section's polite "Saved." status line. */
  savedStatus: (section: SettingsSection) => ReactNode;
  /** Sends `PATCH /api/me` after every earlier one, as this section's action; resolves the error, if any. */
  patch: (section: SettingsSection, body: MePatch) => Promise<ApiError | null>;
  /** Runs an action unless another is running and shows the message it returns, or its problem. */
  act: (section: SettingsSection, action: () => Promise<SettingsMessage>) => Promise<void>;
}

const SettingsStatusContext = createContext<SettingsStatus | null>(null);

/** Holds the shared status for one Settings page; pass it to `SettingsStatusProvider`. */
export function useSettingsStatusState(): SettingsStatus {
  const { t } = useTranslation();
  const busyRef = useRef<SettingsSection | null>(null);
  const chain = useRef<Promise<unknown>>(Promise.resolve());
  const [busy, setBusy] = useState<SettingsSection | null>(null);
  const [saved, setSaved] = useState<SettingsSection | null>(null);
  const [message, setMessage] = useState<(SettingsMessage & { section: SettingsSection }) | null>(
    null,
  );

  function start(section: SettingsSection): void {
    busyRef.current = section;
    setBusy(section);
    setSaved(null);
    setMessage(null);
  }

  function finish(): void {
    busyRef.current = null;
    setBusy(null);
  }

  function show(section: SettingsSection, next: SettingsMessage): void {
    setMessage({ ...next, section });
  }

  function fail(section: SettingsSection, error: ApiError): void {
    if (!error.handled) show(section, { tone: 'notice', text: problemToMessage(error).message });
  }

  return {
    busy,
    message,
    isBusy: () => busyRef.current !== null,
    start,
    finish,
    markSaved: setSaved,
    show,
    fail,
    outcome: (section) =>
      message?.section === section ? (
        <Notice tone={message.tone}>
          <p>{message.text}</p>
        </Notice>
      ) : null,
    savedStatus: (section) => (
      <p role="status" className="inline-flex items-center gap-1.5 font-medium text-foreground">
        {saved === section && (
          <>
            <Check aria-hidden className="size-[18px] text-primary" />
            {t('settings.saved')}
          </>
        )}
      </p>
    ),
    patch: (section, body) => {
      const run = chain.current.then(async () => {
        start(section);
        try {
          auth.setMe(await api.updateMe(body));
          setSaved(section);
          return null;
        } catch (error) {
          return asApiError(error);
        } finally {
          finish();
        }
      });
      chain.current = run;
      return run;
    },
    act: async (section, action) => {
      if (busyRef.current) return;
      start(section);
      try {
        show(section, await action());
      } catch (error) {
        fail(section, asApiError(error));
      } finally {
        finish();
      }
    },
  };
}

/** Shares one page's `useSettingsStatusState()` with its cards. */
export function SettingsStatusProvider({
  status,
  children,
}: {
  status: SettingsStatus;
  children: ReactNode;
}) {
  return <SettingsStatusContext value={status}>{children}</SettingsStatusContext>;
}

/** The shared status of the Settings page this card is on. */
export function useSettingsStatus(): SettingsStatus {
  const status = useContext(SettingsStatusContext);
  if (!status) throw new Error('useSettingsStatus needs a SettingsStatusProvider');
  return status;
}
