import de from '@/content/reflect-questions.de.json';
import en from '@/content/reflect-questions.en.json';
import { currentLanguage } from '@/lib/i18n';
import type { ReflectQuestion } from './types';

const content = { en: en as ReflectQuestion[], de: de as ReflectQuestion[] };

/** The optional reflect-step questions in the current language, in file order. */
export function reflectQuestions(): ReflectQuestion[] {
  return content[currentLanguage()];
}
