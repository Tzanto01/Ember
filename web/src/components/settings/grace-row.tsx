import { useTranslation } from 'react-i18next';
import { SettingsRow } from '@/components/settings/settings-row';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { NativeSelect } from '@/components/ui/select';
import { graceOptions } from '@/lib/models';

/** How many days without a check-in in any seven keep a streak going. */
export function GraceRow({ value, onChange }: { value: number; onChange: (value: number) => void }) {
  const { t } = useTranslation();
  const { outcome, savedStatus } = useSettingsStatus();

  return (
    <SettingsRow
      id="grace"
      controlId="grace-days"
      label={t('settings.grace.label')}
      hint={t('settings.grace.hint')}
      wide
      control={(field) => (
        <NativeSelect {...field} value={String(value)} onChange={(event) => onChange(Number(event.target.value))}>
          {graceOptions.map((n) => (
            <option key={n} value={n}>
              {n === 0 ? t('settings.grace.none') : t('settings.grace.perWeek', { count: n })}
            </option>
          ))}
        </NativeSelect>
      )}
    >
      {savedStatus('grace')}
      {outcome('grace')}
    </SettingsRow>
  );
}
