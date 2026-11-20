import { useTranslation } from 'react-i18next';
import { ColorSchemePicker } from '@/components/settings/color-scheme-picker';
import { LanguagePicker } from '@/components/settings/language-picker';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { ThemePicker } from '@/components/theme-picker';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { env } from '@/lib/env';
import {
  applyLanguage,
  currentLanguage,
  heldLanguage,
  holdLanguage,
  type Language,
  rememberDeviceLanguage,
  storedLanguage,
} from '@/lib/i18n';
import { auth } from '@/lib/services';
import { useStore } from '@/lib/store';
import {
  applyGentleMotion,
  gentleMotion,
  rememberAccountTheme,
  type ThemeId,
} from '@/lib/theme';

/**
 * Language, light or dark, the theme and calm mode, saved to the account when there is one; in the native app also
 * gentle motion, which stays on this device.
 */
export function AppearanceCard({
  title,
  local,
  calm,
  onCalmChange,
}: {
  title: string;
  local: boolean;
  calm: boolean;
  onCalmChange: (value: boolean) => void;
}) {
  const { t } = useTranslation();
  const { show, outcome, savedStatus, patch } = useSettingsStatus();
  const gentle = useStore(gentleMotion);

  function saveTheme(theme: ThemeId): void {
    if (!auth.signedIn()) return;
    void patch('appearance', { theme }).then((error) => {
      if (!error) rememberAccountTheme(theme);
      else if (!error.handled) show('appearance', { tone: 'info', text: t('settings.appearance.notSaved') });
    });
  }

  function saveLanguage(language: Language): void {
    const before = currentLanguage();
    const stored = storedLanguage();
    holdLanguage(language);
    rememberDeviceLanguage(language);
    applyLanguage(language);
    void patch('appearance', { language }).then((error) => {
      if (heldLanguage() !== language) return;
      holdLanguage(null);
      if (!error) return;
      rememberDeviceLanguage(stored);
      applyLanguage(before);
      if (!error.handled) show('appearance', { tone: 'info', text: t('language.notSaved') });
    });
  }

  return (
    <SettingsCard id="appearance" title={title}>
      <p id="appearance-hint">
        {local ? t('settings.appearance.hintLocal') : t('settings.appearance.hintAccount')}
      </p>
      <LanguagePicker local={local} onChoose={saveLanguage} />
      <ColorSchemePicker />
      <ThemePicker labelledBy="appearance-heading" describedBy="appearance-hint" onChoose={saveTheme} />
      {outcome('appearance')}
      <div className="flex min-h-[44px] items-center justify-between gap-4">
        <Label htmlFor="calm-mode" className="min-w-0 font-normal">
          {t('settings.calm.label')}
        </Label>
        <Switch
          id="calm-mode"
          aria-describedby="calm-mode-hint"
          checked={calm}
          onCheckedChange={onCalmChange}
        />
      </div>
      <p id="calm-mode-hint" className="text-[0.875rem] text-muted-foreground">
        {t('settings.calm.hint')}
      </p>
      {savedStatus('calm')}
      {outcome('calm')}
      {env.native && (
        <>
          <div className="flex min-h-[44px] items-center justify-between gap-4">
            <Label htmlFor="gentle-motion" className="min-w-0 font-normal">
              {t('settings.gentleMotion.label')}
            </Label>
            <Switch
              id="gentle-motion"
              aria-describedby="gentle-motion-hint"
              checked={gentle}
              onCheckedChange={applyGentleMotion}
            />
          </div>
          <p id="gentle-motion-hint" className="text-[0.875rem] text-muted-foreground">
            {t('settings.gentleMotion.hint')}
          </p>
        </>
      )}
    </SettingsCard>
  );
}
