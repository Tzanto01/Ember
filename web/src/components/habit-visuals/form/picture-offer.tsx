import { useId } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { matchPicture } from '@/lib/habit-visuals/keywords';
import { auth } from '@/lib/services';
import { useStore } from '@/lib/store';
import type { UnitSystem } from '@/lib/models';
import { choosePicture, type PictureFieldState } from './picture-form';

export interface PictureOfferProps<T extends PictureFieldState> {
  /** The new-habit form. */
  value: T & { kind: string; name: string };
  onChange: (value: T & { kind: string; name: string }) => void;
  units: UnitSystem;
  /** The name input, focused again after "Keep it plain". */
  nameId: string;
}

/**
 * The new-habit offer under the name: shown while picture suggestions are on, the habit is a build habit, still
 * plain and unanswered, and its name matches a picture on device. "Try the …" sets that picture and moves focus to it
 * in the picture field; "Keep it plain" hides the offer. Either answer marks the offer answered.
 */
export function PictureOffer<T extends PictureFieldState>({
  value,
  onChange,
  units,
  nameId,
}: PictureOfferProps<T>) {
  const { t } = useTranslation();
  const id = useId();
  const suggest = useStore(auth.me)?.suggestPictures !== false;
  const picture =
    suggest && value.kind === 'build' && !value.picture && !value.pictureOfferAnswered
      ? matchPicture(value.name)
      : null;
  if (!picture || picture === 'kindling') return null;

  function answer(next: T & { kind: string; name: string }, focus: string): void {
    flushSync(() => onChange({ ...next, pictureOfferAnswered: true }));
    document.querySelector<HTMLElement>(focus)?.focus();
  }

  return (
    <div
      role="group"
      aria-labelledby={`${id}-question`}
      className="-mt-2 flex flex-col gap-3 rounded-md border border-border bg-muted px-4 py-3"
    >
      <p id={`${id}-question`}>{t(`habitVisuals.offer.${picture}`)}</p>
      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          variant="secondary"
          onClick={() =>
            answer(choosePicture(value, picture, units, true), `[data-picture-option="${picture}"]`)
          }
        >
          {t(`habitVisuals.offer.try.${picture}`)}
        </Button>
        <Button type="button" variant="ghost" onClick={() => answer(value, `#${nameId}`)}>
          {t('habitVisuals.offer.keepPlain')}
        </Button>
      </div>
    </div>
  );
}
