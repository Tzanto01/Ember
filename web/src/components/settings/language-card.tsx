import { useTranslation } from 'react-i18next';
import { LanguagePicker } from '@/components/settings/language-picker';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import {
  applyLanguage,
  currentLanguage,
  heldLanguage,
  holdLanguage,
  type Language,
  rememberDeviceLanguage,
  storedLanguage,
} from '@/lib/i18n';

/**
 * English or Deutsch: applies at once and remembers it on this device, and saves it to the account when there is one;
 * a failed save goes back to the language before and says so.
 */
export function LanguageCard({ title, local }: { title: string; local: boolean }) {
  const { t } = useTranslation();
  const { show, outcome, patch } = useSettingsStatus();

  function saveLanguage(language: Language): void {
    const before = currentLanguage();
    const stored = storedLanguage();
    holdLanguage(language);
    rememberDeviceLanguage(language);
    applyLanguage(language);
    void patch('language', { language }).then((error) => {
      if (heldLanguage() !== language) return;
      holdLanguage(null);
      if (!error) return;
      rememberDeviceLanguage(stored);
      applyLanguage(before);
      if (!error.handled) show('language', { tone: 'info', text: t('language.notSaved') });
    });
  }

  return (
    <SettingsCard id="language" title={title}>
      <LanguagePicker labelledBy="language-heading" local={local} onChoose={saveLanguage} />
      {outcome('language')}
    </SettingsCard>
  );
}
