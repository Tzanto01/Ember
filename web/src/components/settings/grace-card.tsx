import { useTranslation } from 'react-i18next';
import { FormField } from '@/components/form-field';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { NativeSelect } from '@/components/ui/select';
import { graceOptions } from '@/lib/models';

/** How many days without a check-in in any seven keep a streak going. */
export function GraceCard({
  title,
  value,
  onChange,
}: {
  title: string;
  value: number;
  onChange: (value: number) => void;
}) {
  const { t } = useTranslation();
  const { outcome, savedStatus } = useSettingsStatus();

  return (
    <SettingsCard id="grace" title={title}>
      <FormField
        label={t('settings.grace.label')}
        hint={t('settings.grace.hint')}
      >
        {(field) => (
          <NativeSelect
            {...field}
            value={String(value)}
            onChange={(event) => onChange(Number(event.target.value))}
          >
            {graceOptions.map((n) => (
              <option key={n} value={n}>
                {n === 0 ? t('settings.grace.none') : t('settings.grace.perWeek', { count: n })}
              </option>
            ))}
          </NativeSelect>
        )}
      </FormField>
      <p className="text-[0.875rem] text-muted-foreground">{t('settings.grace.note')}</p>
      {savedStatus('grace')}
      {outcome('grace')}
    </SettingsCard>
  );
}
