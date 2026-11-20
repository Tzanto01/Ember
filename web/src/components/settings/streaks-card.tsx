import { useTranslation } from 'react-i18next';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';

/** Whether habits show their streaks; the page saves the choice, since grace days only show while streaks do. */
export function StreaksCard({
  title,
  checked,
  onCheckedChange,
}: {
  title: string;
  checked: boolean;
  onCheckedChange: (value: boolean) => void;
}) {
  const { t } = useTranslation();
  const { outcome, savedStatus } = useSettingsStatus();

  return (
    <SettingsCard id="streaks" title={title}>
      <div className="flex min-h-[44px] items-center justify-between gap-4">
        <Label htmlFor="show-streaks" className="min-w-0 font-normal">
          {t('settings.streaks.label')}
        </Label>
        <Switch
          id="show-streaks"
          aria-describedby="show-streaks-hint"
          checked={checked}
          onCheckedChange={onCheckedChange}
        />
      </div>
      <p id="show-streaks-hint" className="text-[0.875rem] text-muted-foreground">
        {t('settings.streaks.hint')}
      </p>
      {savedStatus('streaks')}
      {outcome('streaks')}
    </SettingsCard>
  );
}
