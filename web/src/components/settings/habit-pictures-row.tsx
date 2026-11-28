import { useTranslation } from 'react-i18next';
import { SettingsRow } from '@/components/settings/settings-row';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Switch } from '@/components/ui/switch';

/** Whether Ember suggests a picture for a habit; off stops the new-habit offer and the Today tip, chosen pictures stay. */
export function HabitPicturesRow({
  checked,
  onCheckedChange,
}: {
  checked: boolean;
  onCheckedChange: (value: boolean) => void;
}) {
  const { t } = useTranslation();
  const { outcome, savedStatus } = useSettingsStatus();

  return (
    <SettingsRow
      id="habit-pictures"
      controlId="suggest-pictures"
      label={t('habitVisuals.settings.suggest.label')}
      hint={t('habitVisuals.settings.suggest.hint')}
      control={(field) => <Switch {...field} checked={checked} onCheckedChange={onCheckedChange} />}
    >
      {savedStatus('suggestPictures')}
      {outcome('suggestPictures')}
    </SettingsRow>
  );
}
