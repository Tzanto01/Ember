import { useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { Trans, useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { FormField, focusFirstInvalid } from '@/components/form-field';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { t } from '@/lib/i18n';
import { browserTimeZone, isValidTimeZone, timeZoneLabel } from '@/lib/local-date';
import type { Me } from '@/lib/models';

const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : [];

function zoneError(value: string): string | null {
  const zone = value.trim();
  if (!zone) return t('settings.timezone.empty');
  return isValidTimeZone(zone) ? null : t('settings.timezone.unknown');
}

/** The account's timezone, which decides when a day starts: this browser's zone in one tap, or any IANA zone typed in. */
export function TimezoneCard({ title, me }: { title: string; me: Me }) {
  const { t } = useTranslation();
  const { busy, isBusy, fail, outcome, savedStatus, patch } = useSettingsStatus();
  const tzForm = useRef<HTMLFormElement>(null);
  const [zone, setZone] = useState<string | null>(null);
  const [zoneTouched, setZoneTouched] = useState(false);
  const [zoneServerError, setZoneServerError] = useState<string | null>(null);
  const [browserZone] = useState(browserTimeZone);

  async function saveTimezone(chosen?: string): Promise<void> {
    if (isBusy()) return;
    let next = chosen;
    if (next === undefined) {
      flushSync(() => setZoneTouched(true));
      if (zoneError(zoneValue)) {
        focusFirstInvalid(tzForm.current);
        return;
      }
      next = zoneValue.trim();
    }
    setZoneServerError(null);
    const error = await patch('timezone', { timezone: next });
    if (!error) {
      setZone(null);
      setZoneTouched(false);
    } else if (error.status === 400) {
      setZoneServerError(t('settings.timezone.unknown'));
    } else {
      fail('timezone', error);
    }
  }

  const zoneValue = zone ?? me.timezone;
  const zoneMessage = (zoneTouched ? zoneError(zoneValue) : null) ?? zoneServerError;
  const suggestZone = isValidTimeZone(browserZone) && (!me.timezoneSet || me.timezone !== browserZone);

  return (
    <SettingsCard id="timezone" title={title}>
      {me.timezoneSet ? (
        <p>
          <Trans
            i18nKey="settings.timezone.follows"
            values={{ zone: timeZoneLabel(me.timezone) }}
            components={{ strong: <strong /> }}
          />
        </p>
      ) : (
        <p>
          <Trans
            i18nKey="settings.timezone.notSet"
            values={{ zone: timeZoneLabel(me.timezone) }}
            components={{ strong: <strong /> }}
          />
        </p>
      )}
      {suggestZone && (
        <div>
          <Button
            type="button"
            aria-disabled={busy !== null || undefined}
            onClick={() => void saveTimezone(browserZone)}
          >
            {t('settings.timezone.use', { zone: browserZone })}
          </Button>
        </div>
      )}
      <form
        ref={tzForm}
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void saveTimezone();
        }}
        className="flex flex-col gap-3"
      >
        <FormField
          label={t('settings.timezone.label')}
          hint={t('settings.timezone.hint')}
          error={zoneMessage}
        >
          {(field) => (
            <Input
              {...field}
              type="text"
              list="timezone-list"
              autoComplete="off"
              spellCheck={false}
              value={zoneValue}
              onChange={(event) => setZone(event.target.value)}
              onBlur={() => setZoneTouched(true)}
            />
          )}
        </FormField>
        <datalist id="timezone-list">
          {zones.map((name) => (
            <option key={name} value={name} />
          ))}
        </datalist>
        <div className="flex flex-wrap items-center gap-3">
          <BusyButton
            type="submit"
            variant="secondary"
            busy={busy === 'timezone'}
            busyLabel={t('settings.saving')}
          >
            {t('settings.timezone.save')}
          </BusyButton>
          {savedStatus('timezone')}
        </div>
      </form>
      {outcome('timezone')}
    </SettingsCard>
  );
}
