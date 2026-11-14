import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { env } from '@/lib/env';
import { isLanguage, type Language } from '@/lib/i18n';
import { Store, useStore } from '@/lib/store';

const key = 'ember.momentVoice';
const tags: Record<Language, string> = { en: 'en-US', de: 'de-DE' };
const textToSpeech = () => import('@capacitor-community/text-to-speech');
const haptics = () => import('@capacitor/haptics');

function stored(): boolean {
  try {
    return localStorage.getItem(key) === 'on';
  } catch {
    return false;
  }
}

/** "Guide me by voice" on this device (`ember.momentVoice`); off unless the user turned it on. */
export const voiceOn = new Store(stored());

let spoken = 0;

/** Turns voice guidance on or off and remembers it on this device; turning it off stops speaking at once. */
export function setVoiceOn(on: boolean): void {
  voiceOn.set(on);
  try {
    if (on) localStorage.setItem(key, 'on');
    else localStorage.removeItem(key);
  } catch {}
  if (!on) stopVoice();
}

function webVoice(language: Language): SpeechSynthesisVoice | undefined {
  const pattern = new RegExp(`^${language}(?:[-_]|$)`, 'i');
  return globalThis.speechSynthesis
    ?.getVoices()
    .find((voice) => voice.localService && pattern.test(voice.lang));
}

async function canSpeak(language: Language): Promise<boolean> {
  if (!env.native) return webVoice(language) !== undefined;
  try {
    const { TextToSpeech } = await textToSpeech();
    return (await TextToSpeech.isLanguageSupported({ lang: tags[language] })).supported;
  } catch {
    return false;
  }
}

/** Speaks `text` in `language` with the device's own voice, cutting off anything still being spoken. Never throws. */
export function speak(text: string, language: Language): void {
  const turn = ++spoken;
  if (!env.native) {
    const voice = webVoice(language);
    const synth = globalThis.speechSynthesis;
    if (!voice || !synth) return;
    synth.cancel();
    const utterance = new SpeechSynthesisUtterance(text);
    utterance.voice = voice;
    utterance.lang = voice.lang;
    utterance.rate = 0.9;
    synth.speak(utterance);
    return;
  }
  void textToSpeech()
    .then(({ TextToSpeech, QueueStrategy }) =>
      turn === spoken
        ? TextToSpeech.speak({ text, lang: tags[language], rate: 0.9, queueStrategy: QueueStrategy.Flush })
        : undefined,
    )
    .catch(() => undefined);
}

/** Stops speaking at once. Never throws. */
export function stopVoice(): void {
  spoken++;
  if (!env.native) {
    globalThis.speechSynthesis?.cancel();
    return;
  }
  void textToSpeech()
    .then(({ TextToSpeech }) => TextToSpeech.stop())
    .catch(() => undefined);
}

/** Android app only: one short, soft vibration. Never throws. */
export function pulse(): void {
  if (!env.native) return;
  void haptics()
    .then(({ Haptics, ImpactStyle }) => Haptics.impact({ style: ImpactStyle.Light }))
    .catch(() => undefined);
}

/**
 * Voice guidance for the moment flow in the app language: `available` once the device has an offline voice for it
 * (the switch shows only then), `active` while it is also switched on.
 */
export function useVoiceGuide(): { available: boolean; active: boolean; language: Language } {
  const { i18n } = useTranslation();
  const language: Language = isLanguage(i18n.language) ? i18n.language : 'en';
  const on = useStore(voiceOn);
  const [available, setAvailable] = useState(false);

  useEffect(() => {
    let live = true;
    const check = () => void canSpeak(language).then((yes) => live && setAvailable(yes));
    check();
    const synth = env.native ? undefined : globalThis.speechSynthesis;
    synth?.addEventListener?.('voiceschanged', check);
    return () => {
      live = false;
      synth?.removeEventListener?.('voiceschanged', check);
    };
  }, [language]);

  return { available, active: available && on, language };
}
