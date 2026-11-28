import { ChevronRight } from 'lucide-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import type { FieldControlProps } from '@/components/form-field';
import { settingsScrollMargin } from '@/components/settings/settings-card';
import { Badge } from '@/components/ui/badge';
import { cardVariants } from '@/components/ui/card';
import { Label } from '@/components/ui/label';
import { cn } from '@/lib/utils';

/** Attributes `SettingsRow` hands to its control. */
export type SettingsControlProps = Pick<FieldControlProps, 'id' | 'aria-describedby'>;

export interface SettingsRowProps {
  /** The row's `#address`; its label (`<id>-heading`) takes focus when the address opens it. */
  id: string;
  /** The control's id, which the label points at. */
  controlId: string;
  label: string;
  /** One short line under the label, linked with `aria-describedby`. */
  hint?: string;
  /** Shows the "This device" chip beside the label and adds it to the control's description. */
  device?: boolean;
  /** For a select: the control takes the row's width under the label when the row is too narrow for both. */
  wide?: boolean;
  /** Renders the control; spread the given props onto it. */
  control: (props: SettingsControlProps) => ReactNode;
  /** Settings that only apply while this one is on, indented under it. */
  nested?: ReactNode;
  /** Shown under the row at full width: the section's saved status, its outcome, notices. */
  children?: ReactNode;
}

/**
 * One setting with a single control: label (with the "This device" chip when `device`) and a short hint on the left,
 * the control on the right, at least 44 px tall. Put rows in `SettingsRows`.
 *
 * ```tsx
 * <SettingsRow id="streaks" controlId="show-streaks" label="Show streaks" hint="Days in a row" control={(field) => (
 *   <Switch {...field} checked={on} onCheckedChange={setOn} />
 * )}>
 *   {savedStatus('streaks')}
 * </SettingsRow>
 * ```
 */
export function SettingsRow({
  id,
  controlId,
  label,
  hint,
  device,
  wide,
  control,
  nested,
  children,
}: SettingsRowProps) {
  const hintId = `${controlId}-hint`;
  const deviceId = `${controlId}-device`;
  const describedBy = [hint ? hintId : '', device ? deviceId : ''].filter(Boolean).join(' ');
  return (
    <div id={id} className={cn('flex flex-col py-3', settingsScrollMargin)}>
      <div className="flex min-h-[44px] flex-wrap items-center gap-x-4 gap-y-2">
        <div className="flex min-w-0 flex-[1_1_12rem] flex-col gap-0.5">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
            <Label
              id={`${id}-heading`}
              htmlFor={controlId}
              tabIndex={-1}
              className="focus:outline-none"
            >
              {label}
            </Label>
            {device && <DeviceChip id={deviceId} />}
          </div>
          {hint && (
            <p id={hintId} className="text-[0.875rem] text-muted-foreground">
              {hint}
            </p>
          )}
        </div>
        <div className={wide ? 'flex-[1_1_10rem] sm:max-w-[14rem]' : 'shrink-0'}>
          {control({ id: controlId, ...(describedBy ? { 'aria-describedby': describedBy } : {}) })}
        </div>
      </div>
      {children && <div className="flex flex-col [&>:not(:empty)]:mt-2">{children}</div>}
      {nested && <div className="mt-1 ml-1 border-l-2 border-border pl-4">{nested}</div>}
    </div>
  );
}

/**
 * A Settings row that opens another page: label, a short hint and a chevron, at least 44 px tall. The link holds only
 * the label (its name) and stretches over the whole row; the hint describes it. `id` is its `#address`; the link
 * itself (`<id>-heading`) takes focus when the address opens it.
 */
export function SettingsLinkRow({
  id,
  to,
  label,
  hint,
}: {
  id: string;
  to: string;
  label: string;
  hint?: string;
}) {
  return (
    <div
      id={id}
      className={cn(
        'group relative flex min-h-[44px] items-center gap-4 rounded-md py-3 has-[a:focus-visible]:outline-[3px] has-[a:focus-visible]:outline-offset-2 has-[a:focus-visible]:outline-ring has-[a:focus-visible]:outline-solid',
        settingsScrollMargin,
      )}
    >
      <div className="flex min-w-0 flex-1 flex-col gap-0.5">
        <Link
          id={`${id}-heading`}
          to={to}
          aria-describedby={hint ? `${id}-hint` : undefined}
          className="font-medium text-card-foreground no-underline group-hover:underline after:absolute after:inset-0 after:content-[''] focus-visible:outline-none"
        >
          {label}
        </Link>
        {hint && (
          <p id={`${id}-hint`} className="text-[0.875rem] text-muted-foreground">
            {hint}
          </p>
        )}
      </div>
      <ChevronRight aria-hidden className="size-[18px] shrink-0 text-muted-foreground" />
    </div>
  );
}

/** The small "This device" chip on settings that stay on this device; add its `id` to the control's `aria-describedby`. */
export function DeviceChip({ id }: { id: string }) {
  const { t } = useTranslation();
  return (
    <Badge id={id} className="px-2 text-[0.8125rem] font-semibold">
      {t('settings.thisDevice')}
    </Badge>
  );
}

/** One card holding `SettingsRow`s and `SettingsLinkRow`s, divided by lines. */
export function SettingsRows({ children }: { children: ReactNode }) {
  return (
    <div className={cn(cardVariants(), 'gap-0 divide-y divide-border py-1 md:py-2')}>
      {children}
    </div>
  );
}
