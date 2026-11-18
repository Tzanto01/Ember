import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { reminderNames } from '@/lib/native/reminder-names';
import { useStore } from '@/lib/store';

/**
 * The "Show habit names in reminders" switch. Without props it is the native per-device setting bound to
 * `reminderNames`; with `checked` and `onCheckedChange` it shows and changes another value (this browser's push).
 */
export function ReminderNamesSetting({
  checked,
  onCheckedChange,
}: {
  checked?: boolean;
  onCheckedChange?: (checked: boolean) => void;
} = {}) {
  const { t } = useTranslation();
  const id = useId();
  const native = useStore(reminderNames);
  return (
    <div className="flex items-start justify-between gap-4">
      <div className="flex min-w-0 flex-col gap-1">
        <Label htmlFor={id}>{t('settings.reminderNames.label')}</Label>
        <p id={`${id}-hint`} className="text-[0.875rem] text-muted-foreground">
          {t('settings.reminderNames.hint')}
        </p>
      </div>
      <Switch
        id={id}
        checked={checked ?? native}
        aria-describedby={`${id}-hint`}
        onCheckedChange={onCheckedChange ?? ((on) => reminderNames.set(on))}
        className="mt-[2px]"
      />
    </div>
  );
}
