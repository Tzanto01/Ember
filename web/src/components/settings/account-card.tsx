import { useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { Trans, useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Button } from '@/components/ui/button';
import type { Me } from '@/lib/models';
import { api, auth } from '@/lib/services';

/** Who is signed in, a password reset link, sign out here and sign out everywhere after a confirmation. */
export function AccountCard({ title, me }: { title: string; me: Me }) {
  const { t } = useTranslation();
  const { busy, isBusy, start, finish, show, outcome, act } = useSettingsStatus();
  const everywhereButton = useRef<HTMLButtonElement>(null);
  const everywhereText = useRef<HTMLParagraphElement>(null);
  const [confirmEverywhere, setConfirmEverywhere] = useState(false);

  const changePassword = (email: string) =>
    act('password', async () => {
      await api.forgotPassword(email);
      return {
        tone: 'success',
        text: t('settings.account.passwordSent', { email }),
      };
    });

  async function signOut(): Promise<void> {
    if (isBusy()) return;
    start('signout');
    if (await auth.logout()) return;
    finish();
    show('signout', { tone: 'notice', text: t('settings.account.signOutFailed') });
  }

  function askEverywhere(open: boolean): void {
    flushSync(() => setConfirmEverywhere(open));
    (open ? everywhereText : everywhereButton).current?.focus();
  }

  async function signOutEverywhere(): Promise<void> {
    if (isBusy()) return;
    start('everywhere');
    if (await auth.logout(true)) return;
    finish();
    show('everywhere', {
      tone: 'notice',
      text: t('settings.account.everywhereFailed'),
    });
  }

  return (
    <SettingsCard id="account" title={title}>
      <p>
        {me.email ? (
          <Trans
            i18nKey="settings.account.signedInAs"
            values={{ email: me.email }}
            components={{ strong: <strong className="[overflow-wrap:anywhere]" /> }}
          />
        ) : (
          t('settings.account.signedInDiscord')
        )}
      </p>
      <div className="flex flex-wrap gap-3">
        {me.email && me.hasPassword && (
          <BusyButton
            type="button"
            variant="secondary"
            busy={busy === 'password'}
            busyLabel={t('settings.account.sending')}
            onClick={() => void changePassword(me.email ?? '')}
          >
            {t('settings.account.changePassword')}
          </BusyButton>
        )}
        <BusyButton
          type="button"
          variant="secondary"
          busy={busy === 'signout'}
          busyLabel={t('nav.signingOut')}
          onClick={() => void signOut()}
        >
          {t('nav.signOut')}
        </BusyButton>
        {!confirmEverywhere && (
          <Button
            ref={everywhereButton}
            type="button"
            variant="ghost"
            onClick={() => askEverywhere(true)}
          >
            {t('settings.account.everywhere')}
          </Button>
        )}
      </div>
      {confirmEverywhere && (
        <div className="flex flex-col gap-3 rounded-md border-[length:var(--border-width)] border-border p-3">
          <p ref={everywhereText} tabIndex={-1} className="focus:outline-none">
            {t('settings.account.everywhereConfirm')}
          </p>
          <div className="flex flex-wrap gap-3">
            <BusyButton
              type="button"
              variant="secondary"
              busy={busy === 'everywhere'}
              busyLabel={t('nav.signingOut')}
              onClick={() => void signOutEverywhere()}
            >
              {t('settings.account.everywhereYes')}
            </BusyButton>
            <Button type="button" variant="ghost" onClick={() => askEverywhere(false)}>
              {t('settings.cancel')}
            </Button>
          </div>
        </div>
      )}
      {outcome('password')}
      {outcome('signout')}
      {outcome('everywhere')}
    </SettingsCard>
  );
}
