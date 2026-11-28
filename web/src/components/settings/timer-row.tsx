import { useTranslation } from 'react-i18next';
import { SettingsRow } from '@/components/settings/settings-row';
import { Switch } from '@/components/ui/switch';
import { useStore } from '@/lib/store';
import { timerSound } from '@/lib/timer/timer-sound';

/** Whether a habit's timer ends with a sound on this device. */
export function TimerRow() {
  const { t } = useTranslation();
  const on = useStore(timerSound);
  return (
    <SettingsRow
      id="timer"
      controlId="timer-sound"
      label={t('settings.timer.sound')}
      hint={t('settings.timer.soundHint')}
      device
      control={(field) => <Switch {...field} checked={on} onCheckedChange={(value) => timerSound.set(value)} />}
    />
  );
}
