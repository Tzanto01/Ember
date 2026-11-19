import { useMutation } from '@tanstack/react-query';
import { useId, useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { useNavigate, useSearchParams } from 'react-router';
import { BusyButton, TapLink } from '@/components/auth/busy-button';
import { ShieldLeaf } from '@/components/auth/illustrations';
import { PageHeader } from '@/components/page-header';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Label } from '@/components/ui/label';
import { Notice } from '@/components/ui/notice';
import { saveFile } from '@/lib/api-client';
import { env } from '@/lib/env';
import { formatInstantDate } from '@/lib/local-date';
import type { ExportFormat } from '@/lib/models';
import { openOnWeb } from '@/lib/native/browser';
import { asApiError, problemToMessage } from '@/lib/problem';
import { safeReturnUrl } from '@/lib/return-url';
import { api, auth } from '@/lib/services';
import { useStore } from '@/lib/store';

const exportFormats: ExportFormat[] = ['json', 'csv'];

/**
 * `/consent`: health-data consent for the current privacy policy version, with the age confirmation. "No thanks"
 * declines (export-only state). While consent is withdrawn it shows the deletion date, the export and a way to delete
 * the account, above the re-consent.
 */
export default function ConsentPage() {
  const { t } = useTranslation();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const returnUrl = safeReturnUrl(params.get('returnUrl'));
  const me = useStore(auth.me);
  const policyChanged = !!me?.consent.given && auth.needsConsent();
  const withdrawn = !!me?.consent.withdrawnAt;
  const deletesAt = me?.consent.deletesAt ?? null;
  const [problem, setProblem] = useState<string | null>(null);
  const [age, setAge] = useState(false);
  const [tried, setTried] = useState(false);
  const [exporting, setExporting] = useState<ExportFormat | null>(null);
  const ageId = useId();
  const ageBox = useRef<HTMLButtonElement>(null);
  const ageError = tried && !age ? t('consent.ageRequired') : null;

  function showProblem(error: unknown): void {
    const apiError = asApiError(error);
    if (!apiError.handled) setProblem(problemToMessage(apiError).message);
  }

  const agree = useMutation({
    mutationFn: async ({ policyVersion }: { policyVersion: string; next: string }) => {
      await api.giveConsent(policyVersion, true);
      await auth.loadMe(true);
    },
    onSuccess: (_, { next }) => {
      void navigate(next);
    },
    onError: showProblem,
  });

  const decline = useMutation({
    mutationFn: async () => {
      await api.declineConsent();
      await auth.loadMe(true);
    },
    onSuccess: () => {
      void navigate('/settings');
    },
    onError: showProblem,
  });

  function give() {
    if (agree.isPending || decline.isPending || !me) return;
    setProblem(null);
    if (!age) {
      flushSync(() => setTried(true));
      ageBox.current?.focus();
      return;
    }
    agree.mutate({
      policyVersion: me.consent.currentPolicyVersion,
      next: withdrawn || returnUrl.startsWith('/consent') ? '/today' : returnUrl,
    });
  }

  function notNow() {
    if (agree.isPending || decline.isPending) return;
    setProblem(null);
    decline.mutate();
  }

  async function exportAs(format: ExportFormat): Promise<void> {
    if (exporting) return;
    setProblem(null);
    setExporting(format);
    try {
      saveFile(await api.exportData(format));
    } catch (error) {
      showProblem(error);
    } finally {
      setExporting(null);
    }
  }

  return (
    <>
      <div className="flex items-start justify-between gap-4">
        <PageHeader
          className="hyphens-auto"
          title={policyChanged ? t('consent.changed') : t('consent.title')}
        />
        <ShieldLeaf />
      </div>
      {withdrawn && me && (
        <section
          aria-labelledby="consent-withdrawn-heading"
          className="flex flex-col gap-3 rounded-md border-[length:var(--border-width)] border-border bg-muted p-4"
        >
          <h2 id="consent-withdrawn-heading" className="text-[1.125rem] font-semibold">
            {t('consent.withdrawn.title')}
          </h2>
          <p>
            {deletesAt
              ? t('consent.withdrawn.body', { date: formatInstantDate(deletesAt, me.timezone) })
              : t('consent.withdrawn.bodyNoData')}
          </p>
          <div className="flex flex-wrap gap-2">
            {env.native ? (
              <Button
                type="button"
                variant="secondary"
                onClick={() => void openOnWeb('/settings/privacy#export')}
              >
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
          <div>
            <TapLink to="/settings/privacy#delete">{t('consent.withdrawn.deleteAccount')}</TapLink>
          </div>
        </section>
      )}
      <div className="flex flex-col gap-3">
        <p>{t('consent.what')}</p>
        <p>{t('consent.why')}</p>
        <p className="hyphens-auto">
          <TapLink to="/legal/privacy">{t('consent.readPolicy')}</TapLink>
        </p>
      </div>

      {problem && <Notice tone="notice">{problem}</Notice>}

      <div className="flex flex-col gap-3">
        <div className="flex flex-col gap-2">
          <div className="flex min-h-[44px] items-center gap-3">
            <Checkbox
              ref={ageBox}
              id={ageId}
              checked={age}
              aria-required
              aria-invalid={ageError ? true : undefined}
              aria-describedby={ageError ? `${ageId}-error` : undefined}
              onCheckedChange={(value) => setAge(value === true)}
            />
            <Label htmlFor={ageId} className="min-w-0 font-normal">
              {t('consent.age')}
            </Label>
          </div>
          {ageError && (
            <p id={`${ageId}-error`} className="text-[0.875rem] text-destructive">
              {ageError}
            </p>
          )}
        </div>
        <BusyButton
          type="button"
          block
          busy={agree.isPending}
          busyLabel={t('auth.saving')}
          onClick={give}
        >
          {t('consent.agree')}
        </BusyButton>
        {!withdrawn && (
          <BusyButton
            type="button"
            variant="ghost"
            block
            busy={decline.isPending}
            busyLabel={t('auth.saving')}
            onClick={notNow}
          >
            {t('consent.notNow')}
          </BusyButton>
        )}
        <p className="text-center text-[0.875rem] text-muted-foreground">{t('consent.without')}</p>
      </div>
    </>
  );
}
