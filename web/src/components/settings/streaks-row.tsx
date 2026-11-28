import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { SettingsRow } from '@/components/settings/settings-row';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Switch } from '@/components/ui/switch';

/**
 * Whether habits show their streaks; the page saves the choice, since grace days (`nested`) only show while streaks
 * do.
 */
export function StreaksRow({
  checked,
  onCheckedChange,
  nested,
}: {
  checked: boolean;
  onCheckedChange: (value: boolean) => void;
  nested?: ReactNode;
}) {
  const { t } = useTranslation();
  const { outcome, savedStatus } = useSettingsStatus();

  return (
    <SettingsRow
      id="streaks"
      controlId="show-streaks"
      label={t('settings.streaks.label')}
      hint={t('settings.streaks.hint')}
      control={(field) => <Switch {...field} checked={checked} onCheckedChange={onCheckedChange} />}
      nested={nested}
    >
      {savedStatus('streaks')}
      {outcome('streaks')}
    </SettingsRow>
  );
}
