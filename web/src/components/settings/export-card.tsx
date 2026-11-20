import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Button } from '@/components/ui/button';
import { saveFile } from '@/lib/browser-download';
import { env } from '@/lib/env';
import type { ExportFormat } from '@/lib/models';
import { openOnWeb } from '@/lib/native/browser';
import { asApiError } from '@/lib/problem';
import { api } from '@/lib/services';

const exportFormats: ExportFormat[] = ['json', 'csv'];

/** Downloads the account's data as JSON or CSV; the native app sends you to the web for it. */
export function ExportCard({ title }: { title: string }) {
  const { t } = useTranslation();
  const { isBusy, start, finish, fail, outcome } = useSettingsStatus();
  const [exporting, setExporting] = useState<ExportFormat | null>(null);

  async function exportAs(format: ExportFormat): Promise<void> {
    if (isBusy()) return;
    start('export');
    setExporting(format);
    try {
      saveFile(await api.exportData(format));
    } catch (error) {
      fail('export', asApiError(error));
    } finally {
      setExporting(null);
      finish();
    }
  }

  return (
    <SettingsCard id="export" title={title}>
      <p>{t('export.intro')}</p>
      <div className="flex flex-wrap gap-2">
        {env.native ? (
          <Button type="button" variant="secondary" onClick={() => void openOnWeb('/settings/privacy#export')}>
            {t('export.onWeb')}
          </Button>
        ) : (
          exportFormats.map((format) => (
            <BusyButton
              key={format}
              type="button"
              variant="secondary"
              busy={exporting === format}
              busyLabel={t('settings.preparing')}
              onClick={() => void exportAs(format)}
            >
              {t('export.download', { format: format.toUpperCase() })}
            </BusyButton>
          ))
        )}
      </div>
      {outcome('export')}
    </SettingsCard>
  );
}
