import { useTranslation } from 'react-i18next';
import { FormField } from '@/components/form-field';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { NativeSelect } from '@/components/ui/select';
import { useMePatchField } from '@/hooks/use-me-patch';
import { graceOptions, type Me } from '@/lib/models';

/** How many days without a check-in in any seven keep a streak going. */
export function GraceCard({ title, me }: { title: string; me: Me }) {
  const { t } = useTranslation();
  const { fail, outcome, savedStatus, patch } = useSettingsStatus();
  const [grace, changeGrace] = useMePatchField((value: number) => patch('grace', { graceDaysPerWeek: value }), {
    onError: (error) => fail('grace', error),
  });
  const graceShown = grace ?? me.graceDaysPerWeek;

  return (
    <SettingsCard id="grace" title={title}>
      <FormField
        label={t('settings.grace.label')}
        hint={t('settings.grace.hint')}
      >
        {(field) => (
          <NativeSelect
            {...field}
            value={String(graceShown)}
            onChange={(event) => changeGrace(Number(event.target.value))}
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
