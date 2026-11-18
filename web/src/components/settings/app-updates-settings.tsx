import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { BetaInstallAction, useBetaInstallText } from '@/components/beta-update';
import { betaUpdate, checkForUpdate, offerBetaUpdate, type UpdateCheck } from '@/lib/native/updater';
import { useStore } from '@/lib/store';

/**
 * The beta app's "App updates" card body: the installed version and "Check for updates", which checks the manifest
 * now and says the result. A newer build is offered here with "Update now" and in the update banner (even after it was
 * dismissed); both show the same shared install state.
 */
export function AppUpdatesSettings() {
  const { t } = useTranslation();
  const [installed, setInstalled] = useState<string | null>(null);
  const [result, setResult] = useState<UpdateCheck | 'checking' | null>(null);
  const { update, phase } = useStore(betaUpdate);
  const installText = useBetaInstallText(phase, update?.versionName ?? '');

  useEffect(() => {
    let active = true;
    void import('@capacitor/app')
      .then(({ App }) => App.getInfo())
      .then((info) => {
        if (active) setInstalled(info.version);
      })
      .catch(() => undefined);
    return () => {
      active = false;
    };
  }, []);

  async function check() {
    setResult('checking');
    const found = await checkForUpdate();
    if (found.status === 'update') offerBetaUpdate(found.update, true);
    setResult(found);
  }

  const offered = !!update && (result === null || (result !== 'checking' && result.status === 'update'));
  const message =
    result === 'checking'
      ? t('settings.appUpdates.checking')
      : result?.status === 'error'
        ? t('settings.appUpdates.error')
        : result?.status === 'current'
          ? t('settings.appUpdates.current', { version: result.versionName })
          : offered
            ? installText
            : '';

  return (
    <>
      {installed && <p>{t('settings.appUpdates.installed', { version: installed })}</p>}
      <div className="flex flex-wrap items-center gap-3">
        <BusyButton
          variant="secondary"
          busy={result === 'checking'}
          busyLabel={t('settings.appUpdates.checking')}
          onClick={() => void check()}
        >
          {t('settings.appUpdates.check')}
        </BusyButton>
        {offered && <BetaInstallAction />}
      </div>
      <p role="status">{message}</p>
    </>
  );
}
