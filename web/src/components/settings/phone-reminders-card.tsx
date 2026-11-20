import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ReminderNamesSetting } from '@/components/settings/reminder-names-setting';
import { SettingsCard } from '@/components/settings/settings-card';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import { env } from '@/lib/env';
import { updaterAvailable } from '@/lib/native/updater';
import { deviceSync, reminders } from '@/lib/services';

/**
 * Reminders this phone schedules itself: whether they show names, and a notice when notifications are off or Android
 * may deliver them late without exact alarms.
 */
export function PhoneRemindersCard({ title, local }: { title: string; local: boolean }) {
  const { t } = useTranslation();
  const [notificationsOff, setNotificationsOff] = useState(false);
  const [remindersLate, setRemindersLate] = useState(false);

  useEffect(() => {
    if (env.native) {
      void import('@capacitor/local-notifications')
        .then(({ LocalNotifications }) => LocalNotifications.checkPermissions())
        .then((status) => setNotificationsOff(status.display === 'denied'), () => undefined);
    }
    if (updaterAvailable()) void reminders?.exactAllowed().then((exact) => setRemindersLate(!exact));
  }, []);

  return (
    <SettingsCard id="phone-reminders" title={title}>
      <p>{local ? t('settings.phone.introLocal') : t('settings.phone.intro')}</p>
      <ReminderNamesSetting />
      {remindersLate && (
        <Notice tone="info">
          <p>{t('settings.phone.late')}</p>
          <Button
            variant="secondary"
            onClick={() =>
              void reminders?.allowExact().then((exact) => {
                setRemindersLate(!exact);
                if (exact) deviceSync.rebuildReminders();
              })
            }
          >
            {t('settings.phone.allowExact')}
          </Button>
        </Notice>
      )}
      {notificationsOff && (
        <Notice tone="info">
          <p>{t('settings.phone.notificationsOff')}</p>
        </Notice>
      )}
    </SettingsCard>
  );
}
