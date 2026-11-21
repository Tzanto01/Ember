import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { DiagnosticsSheet } from '@/components/settings/diagnostics-sheet';
import { SettingsCard } from '@/components/settings/settings-card';
import { Button } from '@/components/ui/button';

/** The "Send a tech report" Settings card (Android app only): one line and a button that opens {@link DiagnosticsSheet}. */
export function DiagnosticsCard({ title }: { title: string }) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(false);

  return (
    <SettingsCard id="diagnostics" title={title}>
      <p>{t('diagnostics.intro')}</p>
      <div>
        <Button type="button" variant="outline" onClick={() => setOpen(true)}>
          {t('diagnostics.open')}
        </Button>
      </div>
      <DiagnosticsSheet open={open} onClose={() => setOpen(false)} />
    </SettingsCard>
  );
}
