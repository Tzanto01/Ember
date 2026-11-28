import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { SettingsRow } from '@/components/settings/settings-row';
import { Switch } from '@/components/ui/switch';

/**
 * The "Show habit names in reminders" row, marked as a setting of this device, for this phone's reminders or this
 * browser's push. `children` show under the row.
 */
export function ReminderNamesSetting({
  id,
  controlId,
  checked,
  onCheckedChange,
  children,
}: {
  id: string;
  controlId: string;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  children?: ReactNode;
}) {
  const { t } = useTranslation();
  return (
    <SettingsRow
      id={id}
      controlId={controlId}
      label={t('settings.reminderNames.label')}
      hint={t('settings.reminderNames.hint')}
      device
      control={(field) => <Switch {...field} checked={checked} onCheckedChange={onCheckedChange} />}
    >
      {children}
    </SettingsRow>
  );
}
