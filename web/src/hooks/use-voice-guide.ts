import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { env } from '@/lib/env';
import { isLanguage, type Language } from '@/lib/i18n';
import { checkVoice, voiceAvailable, voiceOn } from '@/lib/moment/voice';
import { useStore } from '@/lib/store';

/**
 * Voice guidance for the moment flow in the app language: `available` once the device has an offline voice for it
 * (the switch shows only then), `active` while it is also switched on.
 */
export function useVoiceGuide(): { available: boolean; active: boolean; language: Language } {
  const { i18n } = useTranslation();
  const language: Language = isLanguage(i18n.language) ? i18n.language : 'en';
  const on = useStore(voiceOn);
  const known = useStore(voiceAvailable)[language] === true;

  useEffect(() => {
    const check = () => checkVoice(language);
    check();
    const synth = env.native ? undefined : globalThis.speechSynthesis;
    synth?.addEventListener?.('voiceschanged', check);
    return () => synth?.removeEventListener?.('voiceschanged', check);
  }, [language]);

  return { available: known, active: known && on, language };
}
