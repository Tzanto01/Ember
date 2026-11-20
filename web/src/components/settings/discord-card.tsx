import { useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { ConfirmForm } from '@/components/settings/two-factor-settings';
import { Button } from '@/components/ui/button';
import { env } from '@/lib/env';
import type { Me } from '@/lib/models';
import { openOnWeb } from '@/lib/native/browser';
import { loadPage } from '@/lib/navigation';
import { api, auth } from '@/lib/services';

/**
 * Links Discord (after the password, when the account has one) or unlinks it. The native app links on the web, since
 * the Discord sign-in runs in the browser.
 */
export function DiscordCard({ title, me }: { title: string; me: Me }) {
  const { t } = useTranslation();
  const { busy, outcome, act } = useSettingsStatus();
  const linkDiscordButton = useRef<HTMLButtonElement>(null);
  const [linkingDiscord, setLinkingDiscord] = useState(false);

  const unlinkDiscord = () =>
    act('discord', async () => {
      await api.unlinkDiscord();
      await auth.refreshAntiforgery();
      await auth.loadMe(true);
      return { tone: 'success', text: t('settings.discord.unlinked') };
    });

  async function linkDiscord(password: string): Promise<void> {
    await api.startDiscordLink(password);
    loadPage(api.discordLinkUrl());
  }

  function cancelLinkDiscord(): void {
    flushSync(() => setLinkingDiscord(false));
    linkDiscordButton.current?.focus();
  }

  return (
    <SettingsCard id="discord" title={title}>
      {me.discord.linked ? (
        <>
          <p>{t('settings.discord.linkedText')}</p>
          <div>
            <BusyButton
              type="button"
              variant="secondary"
              busy={busy === 'discord'}
              busyLabel={t('settings.discord.unlinking')}
              onClick={() => void unlinkDiscord()}
            >
              {t('settings.discord.unlink')}
            </BusyButton>
          </div>
        </>
      ) : (
        <>
          <p>{t('settings.discord.intro')}</p>
          {linkingDiscord ? (
            <ConfirmForm
              kind="password"
              email={me.email}
              hint={t('settings.discord.passwordHint')}
              submitLabel={t('settings.discord.continue')}
              busyLabel={t('settings.checking')}
              onSubmit={linkDiscord}
              onCancel={cancelLinkDiscord}
            />
          ) : (
            <div>
              {env.native ? (
                <Button type="button" variant="secondary" onClick={() => void openOnWeb('/settings/account#discord')}>
                  {t('settings.discord.linkOnWeb')}
                </Button>
              ) : me.hasPassword ? (
                <Button
                  ref={linkDiscordButton}
                  type="button"
                  variant="secondary"
                  onClick={() => setLinkingDiscord(true)}
                >
                  {t('settings.discord.link')}
                </Button>
              ) : (
                <Button asChild variant="secondary">
                  <a href={api.discordLinkUrl()}>{t('settings.discord.link')}</a>
                </Button>
              )}
            </div>
          )}
          {env.native && (
            <p className="text-[0.875rem] text-muted-foreground">{t('settings.discord.nativeHint')}</p>
          )}
        </>
      )}
      {outcome('discord')}
    </SettingsCard>
  );
}
