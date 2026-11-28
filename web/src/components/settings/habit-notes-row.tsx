import { useTranslation } from 'react-i18next';
import { SettingsRow } from '@/components/settings/settings-row';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Switch } from '@/components/ui/switch';

/** Whether checking in on a habit offers a short note. */
export function HabitNotesRow({
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
      id="habit-notes"
      controlId="offer-habit-note"
      label={t('settings.habitNotes.label')}
      hint={t('settings.habitNotes.hint')}
      control={(field) => <Switch {...field} checked={checked} onCheckedChange={onCheckedChange} />}
    >
      {savedStatus('habitNotes')}
      {outcome('habitNotes')}
    </SettingsRow>
  );
}
