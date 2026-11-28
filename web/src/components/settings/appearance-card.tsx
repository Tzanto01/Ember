import { useTranslation } from 'react-i18next';
import { ColorSchemePicker } from '@/components/settings/color-scheme-picker';
import { SettingsCard } from '@/components/settings/settings-card';
import { SettingsRow } from '@/components/settings/settings-row';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { ThemePicker } from '@/components/theme-picker';
import { Switch } from '@/components/ui/switch';
import { env } from '@/lib/env';
import { useStore } from '@/lib/store';
import { applyGentleMotion, gentleMotion, rememberAccountTheme, type ThemeId } from '@/lib/theme';

/**
 * Look & feel in two cards: "Everywhere" (`#appearance`) holds the theme and calm mode, saved to the account when
 * there is one; "This device" (`#this-device`) holds light or dark and, in the native app, gentle motion.
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
  const { show, markSaved, outcome, savedStatus, patch } = useSettingsStatus();
  const gentle = useStore(gentleMotion);

  function saveTheme(theme: ThemeId): void {
    void patch('appearance', { theme }).then((error) => {
      if (!error) rememberAccountTheme(theme);
      else if (!error.handled)
        show('appearance', { tone: 'info', text: t('settings.appearance.notSaved') });
    });
  }

  return (
    <>
      <SettingsCard id="appearance" title={title}>
        <p id="appearance-hint">
          {local ? t('settings.appearance.hintLocal') : t('settings.appearance.hintAccount')}
        </p>
        <div className="flex flex-col gap-2">
          <p id="theme-label" className="font-medium">
            {t('settings.theme.label')}
          </p>
          <ThemePicker
            labelledBy="theme-label"
            describedBy="appearance-hint"
            onChoose={saveTheme}
          />
          {savedStatus('appearance')}
          {outcome('appearance')}
        </div>
        <SettingsRow
          id="calm"
          controlId="calm-mode"
          label={t('settings.calm.label')}
          hint={t('settings.calm.hint')}
          control={(field) => <Switch {...field} checked={calm} onCheckedChange={onCalmChange} />}
        >
          {savedStatus('calm')}
          {outcome('calm')}
        </SettingsRow>
      </SettingsCard>
      <SettingsCard id="this-device" title={t('settings.thisDevice')}>
        <p id="this-device-hint">{t('settings.deviceLooks.hint')}</p>
        <ColorSchemePicker />
        {env.native && (
          <SettingsRow
            id="gentle"
            controlId="gentle-motion"
            label={t('settings.gentleMotion.label')}
            hint={t('settings.gentleMotion.hint')}
            control={(field) => (
              <Switch
                {...field}
                checked={gentle}
                onCheckedChange={(on) => {
                  applyGentleMotion(on);
                  markSaved('gentle');
                }}
              />
            )}
          >
            {savedStatus('gentle')}
          </SettingsRow>
        )}
      </SettingsCard>
    </>
  );
}
