import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { TapLink } from '@/components/auth/busy-button';
import { PageHeader } from '@/components/page-header';
import { DeleteCard } from '@/components/settings/delete-card';
import {
  SettingsStatusProvider,
  useSettingsStatusState,
} from '@/components/settings/settings-status';
import { Button } from '@/components/ui/button';
import { env } from '@/lib/env';
import type { AppMode } from '@/lib/local/types';
import type { Me } from '@/lib/models';
import { queryString } from '@/lib/navigation';
import { deleteAccountPath } from '@/lib/return-url';
import { auth, localMode } from '@/lib/services';
import { useStore } from '@/lib/store';

const supportEmail = 'contact@nicowoellnitz.de';
const list = 'flex list-outside flex-col gap-2 pl-5 [overflow-wrap:anywhere]';
const heading = 'text-[1.375rem]';
const stays = ['backups', 'logs', 'feedback', 'anonymous', 'emails', 'devices', 'discord'] as const;

function SignedOut() {
  const { t } = useTranslation();
  const word = t('delete.word');
  return (
    <>
      <section aria-labelledby="delete-steps" className="flex flex-col gap-3">
        <h2 id="delete-steps" className={heading}>
          {t('deleteAccount.steps.heading')}
        </h2>
        <ol className={`${list} list-decimal`}>
          <li>
            {t('deleteAccount.steps.signIn')} {t('deleteAccount.steps.twoFactor')}
          </li>
          <li>{t('deleteAccount.steps.confirm', { word })}</li>
          <li>{t('deleteAccount.steps.done')}</li>
        </ol>
        <Button asChild className="self-start">
          <Link to={`/login?${queryString({ returnUrl: deleteAccountPath })}`}>
            {t('deleteAccount.signIn')}
          </Link>
        </Button>
      </section>
      <section aria-labelledby="delete-stuck" className="flex flex-col gap-3">
        <h2 id="delete-stuck" className={heading}>
          {t('deleteAccount.stuck.heading')}
        </h2>
        <ul className={`${list} list-disc`}>
          <li>
            {t('deleteAccount.stuck.password')}{' '}
            <TapLink to="/forgot-password">{t('deleteAccount.stuck.passwordLink')}</TapLink>
          </li>
          <li>{t('deleteAccount.stuck.discord', { button: t(env.native ? 'auth.discordContinue' : 'login.discord') })}</li>
          <li>
            {t('deleteAccount.stuck.contact')}{' '}
            <a
              href={`mailto:${supportEmail}`}
              className="inline-flex min-h-[44px] items-center font-medium text-primary underline underline-offset-4"
            >
              {supportEmail}
            </a>
          </li>
        </ul>
      </section>
      <section aria-labelledby="delete-device" className="flex flex-col gap-3">
        <h2 id="delete-device" className={heading}>
          {t('deleteAccount.device.heading')}
        </h2>
        <p>
          {t('deleteAccount.device.text', {
            group: t('settings.group.privacy'),
            card: t('settings.card.deleteDevice'),
          })}
        </p>
      </section>
    </>
  );
}

function SignedIn({ me, mode }: { me: Me; mode: AppMode }) {
  const { t } = useTranslation();
  if (mode === 'local') {
    return (
      <>
        <p>{t('deleteAccount.local')}</p>
        <DeleteCard title={t('settings.card.deleteDevice')} me={me} local />
        <p>{t('deleteAccount.localSent')}</p>
        <p>{t('deleteAccount.localAccount')}</p>
        <p>
          <TapLink to="/legal/privacy">{t('deleteAccount.stays.policy')}</TapLink>
        </p>
      </>
    );
  }
  return (
    <>
      <p className="[overflow-wrap:anywhere]">
        {me.email
          ? t('deleteAccount.signedInAs', { email: me.email })
          : t('deleteAccount.signedInDiscord')}
      </p>
      {mode === 'upgrading' && <p>{t('deleteAccount.deviceWaits')}</p>}
      <DeleteCard
        title={t('settings.card.deleteAccount')}
        me={me}
        local={false}
        more={
          <p>
            <TapLink to="/settings/privacy#export">{t('deleteAccount.keepCopy')}</TapLink>
          </p>
        }
      />
    </>
  );
}

/**
 * `/delete-account`, public: signed in, the Settings delete card itself (no-account mode: the device deletion); signed
 * out, the ways to get there. Both say what goes at once and what stays, as the privacy policy does.
 */
export default function DeleteAccountPage() {
  const { t } = useTranslation();
  const me = useStore(auth.me);
  const mode = useStore(localMode.mode);
  const local = mode === 'local';
  const status = useSettingsStatusState();
  const [lastMe, setLastMe] = useState(me);
  if (me && me !== lastMe) setLastMe(me);
  const shown = me ?? (status.busy === 'delete' ? lastMe : null);

  return (
    <div className="flex w-full flex-col gap-6">
      <PageHeader title={t('title.deleteAccount')} lede={t('deleteAccount.lede')} />
      {shown ? (
        <SettingsStatusProvider status={status}>
          <SignedIn me={shown} mode={mode} />
        </SettingsStatusProvider>
      ) : (
        <SignedOut />
      )}
      {!local && (
        <>
          <section aria-labelledby="delete-gone" className="flex flex-col gap-3">
            <h2 id="delete-gone" className={heading}>
              {t('deleteAccount.gone.heading')}
            </h2>
            <p>{t('deleteAccount.gone.text')}</p>
          </section>
          <section aria-labelledby="delete-stays" className="flex flex-col gap-3">
            <h2 id="delete-stays" className={heading}>
              {t('deleteAccount.stays.heading')}
            </h2>
            <ul className={`${list} list-disc`}>
              {stays.map((id) => (
                <li key={id}>{t(`deleteAccount.stays.${id}`)}</li>
              ))}
            </ul>
            <p>
              <TapLink to="/legal/privacy">{t('deleteAccount.stays.policy')}</TapLink>
            </p>
          </section>
        </>
      )}
      <p>
        <TapLink to="/">{t('help.back')}</TapLink>
      </p>
    </div>
  );
}
