import { useState } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { FormField } from '@/components/form-field';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { TimeZoneSelect } from '@/components/settings/time-zone-select';
import { browserTimeZone, isValidTimeZone, timeZoneLabel } from '@/lib/local-date';
import type { Me } from '@/lib/models';

/**
 * The account's timezone, which decides when a day starts: picked from a searchable list that offers this device's
 * zone first. `value` is the zone shown (the one being saved, else `me.timezone`); `onChange` saves a pick, and the
 * page shows the zone in use again when that fails.
 */
export function TimezoneCard({
  title,
  me,
  value,
  onChange,
}: {
  title: string;
  me: Me;
  value: string;
  onChange: (zone: string) => void;
}) {
  const { t } = useTranslation();
  const { outcome, savedStatus } = useSettingsStatus();
  const [deviceZone] = useState(browserTimeZone);

  const suggested =
    isValidTimeZone(deviceZone) && (!me.timezoneSet || me.timezone !== deviceZone)
      ? deviceZone
      : null;

  return (
    <SettingsCard id="timezone" title={title}>
      <p>
        <Trans
          i18nKey={me.timezoneSet ? 'settings.timezone.follows' : 'settings.timezone.notSet'}
          values={{ zone: timeZoneLabel(me.timezone) }}
          components={{ strong: <strong /> }}
        />
      </p>
      <FormField
        id="timezone-zone"
        label={t('settings.timezone.label')}
        hint={t('settings.timezone.hint')}
      >
        {(field) => (
          <TimeZoneSelect
            id={field.id}
            aria-describedby={field['aria-describedby']}
            value={value}
            suggested={suggested}
            onChoose={(next) => {
              if (next !== value || !me.timezoneSet) onChange(next);
            }}
          />
        )}
      </FormField>
      {savedStatus('timezone')}
      {outcome('timezone')}
    </SettingsCard>
  );
}
