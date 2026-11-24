import { useTranslation } from 'react-i18next';
import { SettingsCard } from '@/components/settings/settings-card';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { useStore } from '@/lib/store';
import { timerSound } from '@/lib/timer/timer-sound';

/** Whether a habit's timer ends with a sound on this device. */
export function TimerCard({ title }: { title: string }) {
  const { t } = useTranslation();
  return (
    <SettingsCard id="timer" title={title}>
      <div className="flex min-h-[44px] items-center justify-between gap-4">
        <Label htmlFor="timer-sound" className="min-w-0 font-normal">
          {t('settings.timer.sound')}
        </Label>
        <Switch
          id="timer-sound"
          aria-describedby="timer-sound-hint"
          checked={useStore(timerSound)}
          onCheckedChange={(on) => timerSound.set(on)}
        />
      </div>
      <p id="timer-sound-hint" className="text-[0.875rem] text-muted-foreground">
        {t('settings.timer.soundHint')}
      </p>
    </SettingsCard>
  );
}
