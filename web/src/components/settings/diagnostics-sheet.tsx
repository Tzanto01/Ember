import { X } from 'lucide-react';
import { Dialog } from 'radix-ui';
import { useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { BusyButton } from '@/components/auth/busy-button';
import { CopyButton } from '@/components/settings/secret-codes';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import { Skeleton } from '@/components/ui/skeleton';
import {
  collectDiagnostics,
  type DeviceDiagnostics,
  DiagnosticsError,
  type DiagnosticsFailure,
  diagnosticsBody,
  diagnosticsKeepDays,
  diagnosticsWindowDays,
  sendDiagnostics,
} from '@/lib/native/diagnostics';

type Report =
  | { step: 'collecting' }
  | { step: 'collectFailed' }
  | { step: 'ready'; device: DeviceDiagnostics }
  | { step: 'sent'; ref: string };

const placeholders = { ref: 'EMB-…', createdAt: '…' };

/**
 * The "Send a tech report" sheet: reads this device's report, shows exactly the body that will be sent, sends it on
 * Send and then shows its code to post in Ember's Discord. Open while `open`; `onClose` runs on Cancel, Done, Escape
 * or a tap outside. The report lives only while the sheet is open.
 */
export function DiagnosticsSheet({ open, onClose }: { open: boolean; onClose: () => void }) {
  return (
    <Dialog.Root open={open} onOpenChange={(next) => !next && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-[color-mix(in_oklch,var(--foreground)_35%,transparent)]" />
        <Dialog.Content className="fixed inset-x-0 bottom-0 z-40 mx-auto flex max-h-[85dvh] w-full max-w-[32rem] flex-col gap-4 overflow-y-auto rounded-t-2xl border-t border-border bg-card p-5 pb-[calc(1.25rem+env(safe-area-inset-bottom))] text-card-foreground shadow-card focus:outline-none motion-safe:animate-[ember-arrive_var(--ember-dur)_var(--ember-ease)_both] md:bottom-auto md:top-[10dvh] md:rounded-2xl md:border">
          <DiagnosticsReport />
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function DiagnosticsReport() {
  const { t } = useTranslation();
  const id = useId();
  const [report, setReport] = useState<Report>({ step: 'collecting' });
  const [attempt, setAttempt] = useState(0);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<DiagnosticsFailure | null>(null);
  const sentRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let current = true;
    collectDiagnostics().then(
      (device) => current && setReport({ step: 'ready', device }),
      () => current && setReport({ step: 'collectFailed' }),
    );
    return () => {
      current = false;
    };
  }, [attempt]);

  useEffect(() => {
    if (report.step === 'sent') sentRef.current?.focus();
  }, [report.step]);

  function retry() {
    setReport({ step: 'collecting' });
    setAttempt((count) => count + 1);
  }

  async function send(device: DeviceDiagnostics): Promise<void> {
    if (busy) return;
    setBusy(true);
    setFailure(null);
    try {
      const ref = await sendDiagnostics(device);
      setReport({ step: 'sent', ref });
    } catch (error) {
      setFailure(error instanceof DiagnosticsError ? error.kind : 'failed');
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div className="flex items-start justify-between gap-3">
        <Dialog.Title className="pt-2 text-[1.375rem]">
          {report.step === 'sent' ? t('diagnostics.sent.title') : t('settings.card.diagnostics')}
        </Dialog.Title>
        <Dialog.Close asChild>
          <Button
            variant="ghost"
            size="icon"
            className="-mr-2 shrink-0 rounded-full"
            aria-label={t('diagnostics.close')}
          >
            <X aria-hidden />
          </Button>
        </Dialog.Close>
      </div>
      {report.step === 'sent' ? (
        <>
          <div ref={sentRef} tabIndex={-1} className="flex flex-col gap-1 focus:outline-none">
            <p>{t('diagnostics.sent.onItsWay')}</p>
            <p className="text-muted-foreground">
              {t('diagnostics.sent.code')}
            </p>
            <p
              className="font-mono text-[1.75rem] font-bold tracking-[0.1em] select-all [overflow-wrap:anywhere]"
            >
              {report.ref}
            </p>
          </div>
          <CopyButton text={report.ref}>{t('diagnostics.sent.copy')}</CopyButton>
          <Dialog.Description>{t('diagnostics.sent.post')}</Dialog.Description>
          <div>
            <Dialog.Close asChild>
              <Button>{t('diagnostics.sent.done')}</Button>
            </Dialog.Close>
          </div>
        </>
      ) : (
        <>
          <Dialog.Description className="text-muted-foreground">
            {t('diagnostics.description', { days: diagnosticsWindowDays })}
          </Dialog.Description>
          {report.step === 'collecting' && (
            <>
              <p role="status" className="sr-only">
                {t('diagnostics.loading')}
              </p>
              <Skeleton className="h-[12rem] rounded-md" />
            </>
          )}
          {report.step === 'collectFailed' && (
            <Notice tone="notice">
              <p>{t('diagnostics.collectFailed')}</p>
              <Button type="button" variant="outline" onClick={retry}>
                {t('diagnostics.retry')}
              </Button>
            </Notice>
          )}
          {report.step === 'ready' && (
            <>
              {report.device.launches.length === 0 && report.device.exits.length === 0 && (
                <p>{t('diagnostics.empty')}</p>
              )}
              <div className="flex flex-col gap-2">
                <p id={`${id}-preview`} className="font-semibold">
                  {t('diagnostics.preview')}
                </p>
                <div
                  role="region"
                  aria-labelledby={`${id}-preview`}
                  tabIndex={0}
                  className="max-h-[40dvh] overflow-auto rounded-md border-[length:max(1px,var(--border-width))] border-border bg-muted p-3 focus-visible:outline-2 focus-visible:outline-ring"
                >
                  <pre
                    className="font-mono text-[0.8125rem] leading-relaxed"
                  >
                    {JSON.stringify(diagnosticsBody(report.device, placeholders), null, 2)}
                  </pre>
                </div>
                <p className="text-[0.875rem] text-muted-foreground">{t('diagnostics.added')}</p>
                <p className="text-[0.875rem] text-muted-foreground">{t('diagnostics.installation')}</p>
              </div>
              <p className="text-[0.875rem] text-muted-foreground">
                {t('diagnostics.keep', { days: diagnosticsKeepDays })}
              </p>
              {failure && (
                <Notice tone="notice" heading={t('diagnostics.notSent')}>
                  <p>{t(`diagnostics.error.${failure}`)}</p>
                </Notice>
              )}
              <p id={`${id}-consent`}>{t('diagnostics.consent')}</p>
              <div className="flex flex-wrap gap-2">
                <BusyButton
                  type="button"
                  busy={busy}
                  busyLabel={t('diagnostics.sending')}
                  aria-describedby={`${id}-consent`}
                  onClick={() => void send(report.device)}
                >
                  {t('diagnostics.send')}
                </BusyButton>
                <Dialog.Close asChild>
                  <Button type="button" variant="ghost">
                    {t('diagnostics.cancel')}
                  </Button>
                </Dialog.Close>
              </div>
            </>
          )}
        </>
      )}
    </>
  );
}
