import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { offerBetaUpdate } from '@/components/beta-update';
import { Button } from '@/components/ui/button';
import { checkForUpdate, type UpdateCheck } from '@/lib/native/updater';

/**
 * The beta app's "App updates" card body: the installed version and "Check for updates", which checks the manifest
 * now and says the result. A newer build goes to the update banner (even after it was dismissed); "Show update" moves
 * focus to the banner's Update button, where the install runs.
 */
export function AppUpdatesSettings() {
  const { t } = useTranslation();
  const [installed, setInstalled] = useState<string | null>(null);
  const [result, setResult] = useState<UpdateCheck | 'checking' | null>(null);

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
    if (found.status === 'update') offerBetaUpdate(found.update);
    setResult(found);
  }

  const message =
    result === 'checking'
      ? t('settings.appUpdates.checking')
      : result?.status === 'current'
        ? t('settings.appUpdates.current', { version: result.versionName })
        : result?.status === 'update'
          ? t('settings.appUpdates.available', { version: result.update.versionName })
          : result?.status === 'error'
            ? t('settings.appUpdates.error')
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
        {result !== 'checking' && result?.status === 'update' && (
          <Button onClick={() => offerBetaUpdate(result.update, true)}>
            {t('settings.appUpdates.show')}
          </Button>
        )}
      </div>
      <p role="status">{message}</p>
    </>
  );
}
