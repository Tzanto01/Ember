import { useTranslation } from 'react-i18next';
import { FormField } from '@/components/form-field';
import { NativeSelect } from '@/components/ui/select';
import type { UnitSystem } from '@/lib/models';

const systems = ['metric', 'imperial'] as const satisfies readonly UnitSystem[];

export interface UnitsSettingProps {
  /** `Me.units`, metric when missing. */
  value: UnitSystem;
  onChange: (value: UnitSystem) => void;
}

/** "Units": metric (ml, km) or imperial (fl oz, mi) for portions and totals; display only. */
export function UnitsSetting({ value, onChange }: UnitsSettingProps) {
  const { t } = useTranslation();
  return (
    <FormField
      label={t('habitVisuals.settings.units.label')}
      hint={t('habitVisuals.settings.units.hint')}
    >
      {(field) => (
        <NativeSelect
          {...field}
          value={value}
          onChange={(event) => onChange(event.target.value as UnitSystem)}
        >
          {systems.map((system) => (
            <option key={system} value={system}>
              {t(`habitVisuals.settings.units.${system}`)}
            </option>
          ))}
        </NativeSelect>
      )}
    </FormField>
  );
}
