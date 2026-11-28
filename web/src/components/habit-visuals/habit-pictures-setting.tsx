import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';

export interface HabitPicturesSettingProps {
  /** `Me.suggestPictures`, true when missing. */
  value: boolean;
  onChange: (value: boolean) => void;
}

/** The "Suggest pictures for habits" switch: off stops the new-habit offer and the Today tip; chosen pictures stay. */
export function HabitPicturesSetting({ value, onChange }: HabitPicturesSettingProps) {
  const { t } = useTranslation();
  const id = useId();
  return (
    <div className="flex flex-col gap-1">
      <div className="flex min-h-[44px] items-center justify-between gap-4">
        <Label htmlFor={id} className="min-w-0 font-normal">
          {t('habitVisuals.settings.suggest.label')}
        </Label>
        <Switch
          id={id}
          aria-describedby={`${id}-hint`}
          checked={value}
          onCheckedChange={onChange}
        />
      </div>
      <p id={`${id}-hint`} className="text-[0.875rem] text-muted-foreground">
        {t('habitVisuals.settings.suggest.hint')}
      </p>
    </div>
  );
}
