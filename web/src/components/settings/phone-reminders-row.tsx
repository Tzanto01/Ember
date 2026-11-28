import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ReminderNamesSetting } from '@/components/settings/reminder-names-setting';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import { env } from '@/lib/env';
import { reminderNames } from '@/lib/native/reminder-names';
import { updaterAvailable } from '@/lib/native/updater';
import { deviceSync, reminders } from '@/lib/services';
import { useStore } from '@/lib/store';

/**
 * Reminders this phone schedules itself: whether they show names, how to get them without an account, and a notice
 * when notifications are off or Android may deliver them late without exact alarms.
 */
export function PhoneRemindersRow({ local }: { local: boolean }) {
  const { t } = useTranslation();
  const [notificationsOff, setNotificationsOff] = useState(false);
  const [remindersLate, setRemindersLate] = useState(false);
  const { markSaved, savedStatus } = useSettingsStatus();
  const names = useStore(reminderNames);

  useEffect(() => {
    if (env.native) {
      void import('@capacitor/local-notifications')
        .then(({ LocalNotifications }) => LocalNotifications.checkPermissions())
        .then((status) => setNotificationsOff(status.display === 'denied'), () => undefined);
    }
    if (updaterAvailable()) void reminders?.exactAllowed().then((exact) => setRemindersLate(!exact));
  }, []);

  return (
    <ReminderNamesSetting
      id="phone-reminders"
      controlId="phone-reminder-names"
      checked={names}
      onCheckedChange={(on) => {
        reminderNames.set(on);
        markSaved('phoneReminders');
      }}
    >
      {savedStatus('phoneReminders')}
      {local && <p className="text-[0.875rem] text-muted-foreground">{t('settings.phone.introLocal')}</p>}
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
    </ReminderNamesSetting>
  );
}
