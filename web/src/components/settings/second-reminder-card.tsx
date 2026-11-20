import { useTranslation } from 'react-i18next';
import { FormField } from '@/components/form-field';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { NativeSelect } from '@/components/ui/select';
import { useMePatchField } from '@/hooks/use-me-patch';
import { t } from '@/lib/i18n';
import { type Me, secondReminderChoices, type SecondReminderMinutes } from '@/lib/models';

function secondReminderLabel(minutes: SecondReminderMinutes): string {
  if (minutes === 0) return t('settings.second.off');
  return minutes % 60 === 0
    ? t('settings.second.hours', { count: minutes / 60 })
    : t('settings.second.minutes', { count: minutes });
}

/** How long after a reminder a second one follows when the habit is still open, or none. */
export function SecondReminderCard({ title, me }: { title: string; me: Me }) {
  const { t } = useTranslation();
  const { fail, outcome, savedStatus, patch } = useSettingsStatus();
  const [second, changeSecond] = useMePatchField(
    (value: SecondReminderMinutes) => patch('second', { secondReminderMinutes: value }),
    { onError: (error) => fail('second', error) },
  );
  const secondShown = second ?? me.secondReminderMinutes;

  return (
    <SettingsCard id="second-reminder" title={title}>
      <FormField label={t('settings.card.secondReminder')} hint={t('settings.second.hint')}>
        {(field) => (
          <NativeSelect
            {...field}
            value={String(secondShown)}
            onChange={(event) => changeSecond(Number(event.target.value) as SecondReminderMinutes)}
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
