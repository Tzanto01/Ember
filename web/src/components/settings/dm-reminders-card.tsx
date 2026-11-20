import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import type { Me } from '@/lib/models';
import { api, auth } from '@/lib/services';

/** Says when Discord DM reminders are paused and resumes them. */
export function DmRemindersCard({ title, me }: { title: string; me: Me }) {
  const { t } = useTranslation();
  const { busy, outcome, act } = useSettingsStatus();

  const resumeDms = () =>
    act('dm', async () => {
      await api.resumeDmReminders();
      await auth.loadMe(true);
      return { tone: 'success', text: t('settings.dm.resumed') };
    });

  return (
    <SettingsCard id="dm-reminders" title={title}>
      {me.dmReminders.paused && (
        <>
          <p>{t('settings.dm.paused')}</p>
          <div>
            <BusyButton
              type="button"
              variant="secondary"
              busy={busy === 'dm'}
              busyLabel={t('settings.dm.resuming')}
              onClick={() => void resumeDms()}
            >
              {t('settings.dm.resume')}
            </BusyButton>
          </div>
        </>
      )}
      {outcome('dm')}
    </SettingsCard>
  );
}
