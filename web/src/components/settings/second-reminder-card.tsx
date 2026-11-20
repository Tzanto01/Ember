import { useTranslation } from 'react-i18next';
import { FormField } from '@/components/form-field';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { NativeSelect } from '@/components/ui/select';
import { t } from '@/lib/i18n';
import { secondReminderChoices, type SecondReminderMinutes } from '@/lib/models';

function secondReminderLabel(minutes: SecondReminderMinutes): string {
  if (minutes === 0) return t('settings.second.off');
  return minutes % 60 === 0
    ? t('settings.second.hours', { count: minutes / 60 })
    : t('settings.second.minutes', { count: minutes });
}

/** How long after a reminder a second one follows when the habit is still open, or none. */
export function SecondReminderCard({
  title,
  value,
  onChange,
}: {
  title: string;
  value: SecondReminderMinutes;
  onChange: (value: SecondReminderMinutes) => void;
}) {
  const { t } = useTranslation();
  const { outcome, savedStatus } = useSettingsStatus();

  return (
    <SettingsCard id="second-reminder" title={title}>
      <FormField label={t('settings.card.secondReminder')} hint={t('settings.second.hint')}>
        {(field) => (
          <NativeSelect
            {...field}
            value={String(value)}
            onChange={(event) => onChange(Number(event.target.value) as SecondReminderMinutes)}
          >
            {secondReminderChoices.map((n) => (
              <option key={n} value={n}>
                {secondReminderLabel(n)}
              </option>
            ))}
          </NativeSelect>
        )}
      </FormField>
      {savedStatus('second')}
      {outcome('second')}
    </SettingsCard>
  );
}
