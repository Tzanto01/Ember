import { useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { BusyButton, TapLink } from '@/components/auth/busy-button';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Button } from '@/components/ui/button';
import { formatInstantDate } from '@/lib/local-date';
import type { Me } from '@/lib/models';
import { asApiError } from '@/lib/problem';
import { api, auth } from '@/lib/services';

/**
 * Consent to keep the account's data: withdraw it after a confirmation, which leads to `/consent`, or give it again;
 * once withdrawn, says when the data will be deleted.
 */
export function ConsentCard({ title, me }: { title: string; me: Me }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { busy, isBusy, start, finish, fail, outcome } = useSettingsStatus();
  const withdrawButton = useRef<HTMLButtonElement>(null);
  const withdrawText = useRef<HTMLParagraphElement>(null);
  const [confirmWithdraw, setConfirmWithdraw] = useState(false);

  function askWithdraw(open: boolean): void {
    flushSync(() => setConfirmWithdraw(open));
    (open ? withdrawText : withdrawButton).current?.focus();
  }

  async function withdrawConsent(): Promise<void> {
    if (isBusy()) return;
    start('consent');
    try {
      await api.withdrawConsent();
      await auth.loadMe(true);
    } catch (error) {
      fail('consent', asApiError(error));
      finish();
      return;
    }
    finish();
    await navigate('/consent');
  }

  return (
    <SettingsCard id="consent" title={title}>
      {me.consent.given ? (
        <>
          <p>{t('settings.privacy.given')}</p>
          {!confirmWithdraw && (
            <div>
              <Button ref={withdrawButton} type="button" variant="secondary" onClick={() => askWithdraw(true)}>
                {t('settings.privacy.withdraw')}
              </Button>
            </div>
          )}
          {confirmWithdraw && (
            <div className="flex flex-col gap-3 rounded-md border-[length:var(--border-width)] border-border p-3">
              <p ref={withdrawText} tabIndex={-1} className="focus:outline-none">
                {t('settings.privacy.withdrawConfirm')}
              </p>
              <div className="flex flex-wrap gap-3">
                <BusyButton
                  type="button"
                  variant="destructive"
                  busy={busy === 'consent'}
                  busyLabel={t('auth.saving')}
                  onClick={() => void withdrawConsent()}
                >
                  {t('settings.privacy.withdrawYes')}
                </BusyButton>
                <Button type="button" variant="ghost" onClick={() => askWithdraw(false)}>
                  {t('settings.privacy.withdrawNo')}
                </Button>
              </div>
            </div>
          )}
        </>
      ) : (
        <>
          <p>
            {!me.consent.withdrawnAt
              ? t('settings.privacy.missing')
              : me.consent.deletesAt
                ? t('settings.privacy.withdrawn', { date: formatInstantDate(me.consent.deletesAt, me.timezone) })
                : t('settings.privacy.withdrawnNoData')}
          </p>
          <div>
            <TapLink to="/consent">{t('settings.privacy.give')}</TapLink>
          </div>
        </>
      )}
      {outcome('consent')}
    </SettingsCard>
  );
}
