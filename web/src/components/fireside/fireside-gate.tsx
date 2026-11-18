import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate } from 'react-router';
import { PageHeader } from '@/components/page-header';
import { Spinner } from '@/components/spinner';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import { type Fireside, useFireside } from '@/lib/fireside';

/**
 * Shows `children` with the loaded Fireside; sends the user to Settings › People while Fireside is off or in
 * no-account mode, and shows loading and load-failure states meanwhile.
 */
export function FiresideGate({ children }: { children: (fireside: Fireside) => ReactNode }) {
  const { t } = useTranslation();
  const { data, available, isError, refetch, isFetching } = useFireside();

  if (!available || data?.enabled === false) return <Navigate to="/settings/people" replace />;
  if (data) return children(data);
  return (
    <div className="flex max-w-[40rem] flex-col gap-6">
      <PageHeader title={t('fireside.title')} />
      {isError && !isFetching ? (
        <Notice tone="notice">
          <p>{t('fireside.loadFailed')}</p>
          <Button variant="ghost" onClick={() => void refetch()}>
            {t('fireside.retry')}
          </Button>
        </Notice>
      ) : (
        <p role="status" className="flex items-center gap-2 text-muted-foreground">
          <Spinner />
          {t('fireside.loading')}
        </p>
      )}
    </div>
  );
}
