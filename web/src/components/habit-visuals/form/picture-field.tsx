import {
  CircleAlert,
  Flame,
  Footprints,
  GlassWater,
  LibraryBig,
  type LucideIcon,
  Minus,
  Pill,
} from 'lucide-react';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { FormField } from '@/components/form-field';
import { optionClass } from '@/components/habits/habit-fields';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { NativeSelect } from '@/components/ui/select';
import { portionRanges } from '@/lib/local/picture-rules';
import { type HabitPicture, habitPictures, type PortionUnit, type UnitSystem } from '@/lib/models';
import { cn } from '@/lib/utils';
import {
  choosePicture,
  chooseUnit,
  goalSummary,
  goalSymbol,
  habitGoalError,
  habitPictureError,
  type PictureFieldState,
  type TypedUnit,
  typedUnit,
  unitSymbol,
  withGoal,
} from './picture-form';

const icons: Record<HabitPicture | 'plain', LucideIcon> = {
  plain: Minus,
  bottle: GlassWater,
  pills: Pill,
  kindling: Flame,
  shelf: LibraryBig,
  trail: Footprints,
};

const options = ['plain', ...habitPictures] as const;

const unitNames = {
  ml: 'habitVisuals.form.unitName.ml',
  'fl oz': 'habitVisuals.form.unitName.flOz',
  m: 'habitVisuals.form.unitName.m',
  km: 'habitVisuals.form.unitName.km',
  mi: 'habitVisuals.form.unitName.mi',
  min: 'habitVisuals.form.unitName.min',
  pages: 'habitVisuals.form.unitName.pages',
} as const satisfies Record<TypedUnit, string>;

export interface PictureFieldProps<T extends PictureFieldState> {
  /** The habit form; read for the picture fields, frequency and times per day. */
  value: T & { kind: string };
  /** Called with the whole form; a bottle goal or portion change also sets `timesPerDay`. */
  onChange: (value: T & { kind: string }) => void;
  units: UnitSystem;
  /** Show the client messages (after a submit). */
  touched: boolean;
  /** Server field messages; a client message wins. */
  errors?: Record<string, string>;
}

/**
 * "Picture on Today" for a build habit: plain (the default) or one of the five pictures, then what one tap counts as
 * (bottle, shelf and trail) in the display unit, and for a daily or weekday bottle the daily goal that sets times per
 * day, with the taps and the real total under it.
 */
export function PictureField<T extends PictureFieldState>({
  value,
  onChange,
  units,
  touched,
  errors = {},
}: PictureFieldProps<T>) {
  const { t } = useTranslation();
  const id = useId();
  const [portionLeft, setPortionLeft] = useState(false);
  const [goalLeft, setGoalLeft] = useState(false);
  const labelId = `${id}-label`;
  const hintId = `${id}-hint`;
  const pictureError = errors['picture'] ?? null;
  const portionError =
    (touched || portionLeft ? habitPictureError(value, units) : null) ??
    errors['portionAmount'] ??
    errors['portionUnit'] ??
    null;
  const goalError = touched || goalLeft ? habitGoalError(value, units) : null;
  const summary = goalSummary(value, units);
  const unitChoices = value.picture ? portionRanges[value.picture].map((range) => range.unit) : [];
  const portionUnit = value.picture ? value.portionUnit : null;
  const shownUnit = portionUnit ? typedUnit(portionUnit, units) : null;
  const decimal = shownUnit === 'fl oz' || shownUnit === 'km' || shownUnit === 'mi';

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-col gap-2">
        <p id={labelId} className="font-medium">
          {t('habitVisuals.form.label')}
        </p>
        <p id={hintId} className="-mt-1 text-[0.875rem] text-muted-foreground">
          {t('habitVisuals.form.hint')}
        </p>
        <RadioGroup
          aria-labelledby={labelId}
          aria-describedby={cn(hintId, pictureError && `${id}-error`)}
          value={value.picture ?? 'plain'}
          onValueChange={(picture) =>
            onChange(
              choosePicture(value, picture === 'plain' ? null : (picture as HabitPicture), units),
            )
          }
          className="grid-cols-2 sm:grid-cols-3"
        >
          {options.map((option) => {
            const Icon = icons[option];
            return (
              <Label
                key={option}
                htmlFor={`${id}-${option}`}
                className={cn(optionClass, 'gap-2 px-3')}
              >
                <RadioGroupItem
                  id={`${id}-${option}`}
                  value={option}
                  data-picture-option={option}
                  aria-labelledby={`${id}-${option}-name`}
                />
                <Icon aria-hidden className="size-[1.25rem] shrink-0" />
                <span id={`${id}-${option}-name`} className="min-w-0 [overflow-wrap:anywhere]">
                  {t(`habitVisuals.picture.${option}`)}
                </span>
              </Label>
            );
          })}
        </RadioGroup>
        {pictureError && (
          <p
            id={`${id}-error`}
            role="alert"
            className="flex items-start gap-2 text-[0.875rem] text-destructive"
          >
            <CircleAlert aria-hidden className="mt-[0.125rem] size-[1.125rem] shrink-0" />
            <span>{pictureError}</span>
          </p>
        )}
      </div>

      {value.picture === 'bottle' && value.frequency !== 'weekly' && (
        <FormField label={t('habitVisuals.form.goal')} error={goalError}>
          {(field) => (
            <>
              <div className="flex items-center gap-3">
                <Input
                  {...field}
                  aria-describedby={
                    cn(
                      field['aria-describedby'],
                      `${field.id}-unit`,
                      summary && `${field.id}-taps`,
                    ) || undefined
                  }
                  inputMode="decimal"
                  autoComplete="off"
                  maxLength={6}
                  className="max-w-[8rem]"
                  value={value.goal}
                  onChange={(event) =>
                    onChange(withGoal({ ...value, goal: event.target.value }, units))
                  }
                  onBlur={() => setGoalLeft(true)}
                />
                <span id={`${field.id}-unit`}>{goalSymbol(units)}</span>
              </div>
              {summary && (
                <p
                  id={`${field.id}-taps`}
                  aria-live="polite"
                  className="text-[0.875rem] text-muted-foreground"
                >
                  {summary.text}
                </p>
              )}
            </>
          )}
        </FormField>
      )}

      {portionUnit && shownUnit && (
        <FormField label={t('habitVisuals.form.portion')} error={portionError}>
          {(field) => (
            <div className="flex flex-wrap items-center gap-3">
              <Input
                {...field}
                aria-describedby={
                  cn(field['aria-describedby'], unitChoices.length === 1 && `${field.id}-unit`) ||
                  undefined
                }
                inputMode={decimal ? 'decimal' : 'numeric'}
                autoComplete="off"
                maxLength={6}
                className="max-w-[8rem]"
                value={value.portion}
                onChange={(event) =>
                  onChange(withGoal({ ...value, portion: event.target.value }, units))
                }
                onBlur={() => setPortionLeft(true)}
              />
              {unitChoices.length > 1 ? (
                <NativeSelect
                  aria-label={t('habitVisuals.form.unitLabel')}
                  className="max-w-[12rem]"
                  value={portionUnit}
                  onChange={(event) =>
                    onChange(chooseUnit(value, event.target.value as PortionUnit, units))
                  }
                >
                  {unitChoices.map((unit) => (
                    <option key={unit} value={unit}>
                      {t(unitNames[typedUnit(unit, units)])}
                    </option>
                  ))}
                </NativeSelect>
              ) : (
                <span id={`${field.id}-unit`}>{unitSymbol(shownUnit)}</span>
              )}
            </div>
          )}
        </FormField>
      )}
    </div>
  );
}
