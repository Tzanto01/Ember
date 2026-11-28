import { useState } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { FormField } from '@/components/form-field';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { TimeZoneSelect } from '@/components/settings/time-zone-select';
import { useMePatchField } from '@/hooks/use-me-patch';
import { browserTimeZone, isValidTimeZone, timeZoneLabel } from '@/lib/local-date';
import type { Me } from '@/lib/models';

/**
 * The account's timezone, which decides when a day starts: picked from a searchable list that offers this device's
 * zone first, and saved as soon as it is picked; a failed save shows the zone in use again.
 */
export function TimezoneCard({ title, me }: { title: string; me: Me }) {
  const { t } = useTranslation();
  const { fail, outcome, savedStatus, patch } = useSettingsStatus();
  const [deviceZone] = useState(browserTimeZone);
  const [pending, changeZone] = useMePatchField(
    (timezone: string) => patch('timezone', { timezone }),
    {
      onError: (error) => fail('timezone', error),
    },
  );

  const zone = pending ?? me.timezone;
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
            value={zone}
            suggested={suggested}
            onChoose={(next) => {
              if (next !== zone || !me.timezoneSet) changeZone(next);
            }}
          />
        )}
      </FormField>
      {savedStatus('timezone')}
      {outcome('timezone')}
    </SettingsCard>
  );
}
