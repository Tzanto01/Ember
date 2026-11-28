import { type HabitKind, type HabitPicture, habitPictures, type PortionUnit } from '../models';

/** One unit a picture's portion may use and its whole-number range in that canonical unit. */
export interface PortionRange {
  unit: PortionUnit;
  min: number;
  max: number;
}

/** A portion as stored: both set or both null. */
export interface Portion {
  portionAmount: number | null;
  portionUnit: PortionUnit | null;
}

/** The units and ranges each picture's portion allows (Core `PictureRules`); pills and kindling take none. */
export const portionRanges: Record<HabitPicture, readonly PortionRange[]> = {
  bottle: [{ unit: 'ml', min: 50, max: 2000 }],
  pills: [],
  kindling: [],
  shelf: [
    { unit: 'min', min: 1, max: 240 },
    { unit: 'pages', min: 1, max: 500 },
  ],
  trail: [
    { unit: 'min', min: 1, max: 240 },
    { unit: 'm', min: 100, max: 50000 },
  ],
};

/** The portion a picture gets when it is set without one. */
export const defaultPortions: Record<HabitPicture, Portion> = {
  bottle: { portionAmount: 250, portionUnit: 'ml' },
  pills: { portionAmount: null, portionUnit: null },
  kindling: { portionAmount: null, portionUnit: null },
  shelf: { portionAmount: 15, portionUnit: 'min' },
  trail: { portionAmount: 10, portionUnit: 'min' },
};

export function isHabitPicture(value: unknown): value is HabitPicture {
  return (habitPictures as readonly unknown[]).includes(value);
}

/** The first rule a stored picture and portion break. */
export interface PictureError {
  field: 'picture' | 'portionAmount' | 'portionUnit';
  message: string;
}

/**
 * Checks a habit's picture and portion as finally stored (after defaults) in Core's order and returns the first
 * failure, or null when the combination is valid. Null and undefined both mean not set.
 */
export function pictureError(
  kind: HabitKind,
  picture: unknown,
  portionAmount: unknown,
  portionUnit: unknown,
): PictureError | null {
  const hasPicture = picture !== null && picture !== undefined;
  const hasAmount = portionAmount !== null && portionAmount !== undefined;
  const hasUnit = portionUnit !== null && portionUnit !== undefined;
  if (hasPicture && !isHabitPicture(picture))
    return { field: 'picture', message: `Must be one of ${habitPictures.join(', ')}.` };
  if (kind === 'letGo') {
    if (hasPicture) return { field: 'picture', message: 'Never set for let-go habits.' };
    if (hasAmount || hasUnit)
      return { field: 'portionAmount', message: 'Never set for let-go habits.' };
    return null;
  }
  if (!hasAmount && !hasUnit) return null;
  if (picture === 'pills' || picture === 'kindling')
    return { field: 'portionAmount', message: 'Pills and kindling have no portion.' };
  if (hasAmount !== hasUnit)
    return { field: 'portionAmount', message: 'Send portionAmount and portionUnit together.' };
  if (!hasPicture) return { field: 'portionAmount', message: 'Choose a picture first.' };
  const ranges = portionRanges[picture as HabitPicture];
  const range = ranges.find((r) => r.unit === portionUnit);
  if (!range)
    return {
      field: 'portionUnit',
      message: `Must be ${ranges.map((r) => r.unit).join(' or ')} for ${picture as HabitPicture}.`,
    };
  const amount = portionAmount as number;
  if (!Number.isInteger(amount) || amount < range.min || amount > range.max)
    return { field: 'portionAmount', message: `Must be between ${range.min} and ${range.max}.` };
  return null;
}
