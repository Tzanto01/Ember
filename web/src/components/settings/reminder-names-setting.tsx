import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { SettingsRow } from '@/components/settings/settings-row';
import { Switch } from '@/components/ui/switch';
import { reminderNames } from '@/lib/native/reminder-names';
import { useStore } from '@/lib/store';

/**
 * The "Show habit names in reminders" row, marked as a setting of this device. Without `checked` and
 * `onCheckedChange` it is the native per-device setting bound to `reminderNames`; with them it shows and changes
 * another value (this browser's push). `children` show under the row.
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
  checked?: boolean;
  onCheckedChange?: (checked: boolean) => void;
  children?: ReactNode;
}) {
  const { t } = useTranslation();
  const native = useStore(reminderNames);
  return (
    <SettingsRow
      id={id}
      controlId={controlId}
      label={t('settings.reminderNames.label')}
      hint={t('settings.reminderNames.hint')}
      device
      control={(field) => (
        <Switch
          {...field}
          checked={checked ?? native}
          onCheckedChange={onCheckedChange ?? ((on) => reminderNames.set(on))}
        />
      )}
    >
      {children}
    </SettingsRow>
  );
}
