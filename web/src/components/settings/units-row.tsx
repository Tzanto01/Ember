import { useTranslation } from 'react-i18next';
import { SettingsRow } from '@/components/settings/settings-row';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { NativeSelect } from '@/components/ui/select';
import type { UnitSystem } from '@/lib/models';

const systems = ['metric', 'imperial'] as const satisfies readonly UnitSystem[];

/** Metric (ml, km) or imperial (fl oz, mi) for portions and totals; display only. */
export function UnitsRow({ value, onChange }: { value: UnitSystem; onChange: (value: UnitSystem) => void }) {
  const { t } = useTranslation();
  const { outcome, savedStatus } = useSettingsStatus();

  return (
    <SettingsRow
      id="units"
      controlId="unit-system"
      label={t('habitVisuals.settings.units.label')}
      hint={t('habitVisuals.settings.units.hint')}
      wide
      control={(field) => (
        <NativeSelect {...field} value={value} onChange={(event) => onChange(event.target.value as UnitSystem)}>
          {systems.map((system) => (
            <option key={system} value={system}>
              {t(`habitVisuals.settings.units.${system}`)}
            </option>
          ))}
        </NativeSelect>
      )}
    >
      {savedStatus('units')}
      {outcome('units')}
    </SettingsRow>
  );
}
