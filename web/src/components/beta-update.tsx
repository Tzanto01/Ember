import { X } from 'lucide-react';
import { useEffect, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import { currentLanguage } from '@/lib/i18n';
import {
  type BetaInstallPhase,
  betaUpdate,
  findReleaseNotes,
  findUpdate,
  installBetaUpdate,
  offerBetaUpdate,
  resumeBetaInstall,
  updaterAvailable,
} from '@/lib/native/updater';
import { type LocalizedNotes, notesIn } from '@/lib/release-notes';
import { useStore } from '@/lib/store';

const hour = 60 * 60 * 1000;

/** What the offered update's install is doing, as one line of text. */
export function useBetaInstallText(phase: BetaInstallPhase, version: string): string {
  const { t } = useTranslation();
  return {
    offer: t('betaUpdate.offer', { version }),
    permission: t('betaUpdate.permission'),
    downloading: t('betaUpdate.downloading', { version }),
    failed: t('betaUpdate.failed'),
  }[phase];
}

/**
 * The offered update's install action: the download progress (bar and percent) while downloading, otherwise
 * "Update now" ("Try again" after a failure), which starts the shared install.
 */
export function BetaInstallAction() {
  const { t } = useTranslation();
  const { phase, percent } = useStore(betaUpdate);
  if (phase === 'downloading') {
    return (
      <div className="flex w-full items-center gap-3">
        <progress
          className="h-2 flex-1 accent-primary"
          aria-label={t('betaUpdate.progress')}
          max={100}
          {...(percent === null ? {} : { value: percent })}
        />
        {percent !== null && <span aria-hidden>{percent}%</span>}
      </div>
    );
  }
  return (
    <Button variant="secondary" onClick={() => void installBetaUpdate()}>
      {phase === 'failed' ? t('betaUpdate.retry') : t('betaUpdate.update')}
    </Button>
  );
}

/**
 * "Update available" banner for the sideloaded beta app; renders nothing on the web and in the Play build. Checks
 * the manifest on mount and when the app returns to the foreground (at most once an hour), and shows the shared
 * offer and install state (`betaUpdate`). "Show changes" reveals the new version's notes from the server when it has
 * them. Dismissing hides that version until Settings offers it again.
 */
export function BetaUpdateBanner() {
  const { t } = useTranslation();
  const { update, dismissed, phase } = useStore(betaUpdate);
  const [notes, setNotes] = useState<{ versionName: string; notes: LocalizedNotes } | null>(null);
  const [changesOpen, setChangesOpen] = useState(false);
  const notesId = useId();
  const text = useBetaInstallText(phase, update?.versionName ?? '');

  useEffect(() => {
    if (!updaterAvailable()) return;
    let active = true;
    let lastCheck = -Infinity;
    let listener: { remove: () => Promise<void> } | undefined;

    async function check() {
      if (Date.now() - lastCheck < hour) return;
      lastCheck = Date.now();
      const found = await findUpdate();
      if (active && found) offerBetaUpdate(found);
    }

    async function resumed() {
      await resumeBetaInstall();
      await check();
    }

    void check();
    void import('@capacitor/app')
      .then(({ App }) => App.addListener('resume', () => void resumed()))
      .then((handle) => {
        if (active) listener = handle;
        else void handle.remove();
      })
      .catch(() => undefined);
    return () => {
      active = false;
      void listener?.remove();
    };
  }, []);

  const versionName = update?.versionName;
  useEffect(() => {
    if (!versionName) return;
    let active = true;
    void findReleaseNotes(versionName).then((found) => {
      if (active && found) setNotes({ versionName, notes: found });
    });
    return () => {
      active = false;
    };
  }, [versionName]);

  if (!update || update.versionCode <= dismissed) return null;

  const hasNotes = phase === 'offer' && notes?.versionName === update.versionName;

  return (
    <Notice tone={phase === 'failed' ? 'notice' : 'info'} aria-label={t('betaUpdate.label')}>
      <div className="flex w-full flex-wrap items-start gap-2">
        <p>{text}</p>
        <BetaInstallAction />
        {hasNotes && (
          <Button
            variant="ghost"
            aria-expanded={changesOpen}
            aria-controls={changesOpen ? notesId : undefined}
            onClick={() => setChangesOpen((open) => !open)}
          >
            {changesOpen ? t('betaUpdate.hideChanges') : t('betaUpdate.showChanges')}
          </Button>
        )}
        <Button
          variant="ghost"
          size="icon"
          aria-label={t('betaUpdate.dismiss')}
          className="-my-2 -mr-2 ml-auto"
          onClick={() => betaUpdate.update((state) => ({ ...state, dismissed: update.versionCode }))}
        >
          <X aria-hidden />
        </Button>
        {hasNotes && changesOpen && (
          <ul
            id={notesId}
            aria-label={t('betaUpdate.notesLabel', { version: update.versionName })}
            className="flex w-full list-disc flex-col gap-1 pl-5"
          >
            {notesIn(notes.notes, currentLanguage()).map((note) => (
              <li key={note}>{note}</li>
            ))}
          </ul>
        )}
      </div>
    </Notice>
  );
}
