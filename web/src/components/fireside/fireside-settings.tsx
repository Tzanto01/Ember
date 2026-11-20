import { useQueryClient } from '@tanstack/react-query';
import { type SubmitEvent, useEffect, useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { BusyButton, TapLink } from '@/components/auth/busy-button';
import { fieldMessages, FormField } from '@/components/form-field';
import { LiveRegion } from '@/components/live-region';
import { Spinner } from '@/components/spinner';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Notice } from '@/components/ui/notice';
import { Switch } from '@/components/ui/switch';
import { useFireside } from '@/hooks/use-fireside';
import { type FiresidePut, firesideApi, firesideKey, firesideNameMax } from '@/lib/fireside';
import { asApiError, problemToMessage } from '@/lib/problem';

function nameError(value: string): string | null {
  const trimmed = value.trim();
  if (!trimmed) return fieldMessages.required;
  if (trimmed.length > firesideNameMax) return fieldMessages.maxLength(firesideNameMax);
  return null;
}

/**
 * Settings › People › Fireside (screen 1): what Fireside is, "Use Fireside" and the name friends see. Turning it on
 * needs a name and, without a current Fireside consent, asks for it first; turning it off asks first. Renders nothing
 * until the Fireside has loaded.
 */
export function FiresideSettings() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { data, isError, refetch } = useFireside();
  const [name, setName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [busy, setBusy] = useState<'on' | 'off' | 'name' | null>(null);
  const [confirmOff, setConfirmOff] = useState(false);
  const [askConsent, setAskConsent] = useState(false);
  const [announcement, setAnnouncement] = useState('');
  const nameRef = useRef<HTMLInputElement>(null);
  const confirmRef = useRef<HTMLParagraphElement>(null);
  const consentRef = useRef<HTMLHeadingElement>(null);
  const switchRef = useRef<HTMLButtonElement>(null);
  const value = name ?? data?.name ?? '';

  useEffect(() => {
    if (confirmOff) confirmRef.current?.focus();
  }, [confirmOff]);

  useEffect(() => {
    if (askConsent) consentRef.current?.focus();
  }, [askConsent]);

  if (!data) {
    return isError ? (
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
    );
  }

  async function save(kind: 'on' | 'off' | 'name', body: FiresidePut, done: string): Promise<boolean> {
    if (busy) return false;
    setBusy(kind);
    setProblem(null);
    setAnnouncement('');
    try {
      queryClient.setQueryData(firesideKey, await firesideApi.put(body));
      setName(null);
      setAnnouncement(done);
      return true;
    } catch (caught) {
      const apiError = asApiError(caught);
      if (apiError.type === 'fireside_consent_required') {
        setAskConsent(true);
        void refetch();
      } else if (!apiError.handled) {
        const friendly = problemToMessage(apiError);
        if (friendly.fields['name']) {
          setError(t('fireside.settings.nameInvalid', { max: firesideNameMax }));
          nameRef.current?.focus();
        } else setProblem(friendly.message);
      }
      return false;
    } finally {
      setBusy(null);
    }
  }

  function toggle(on: boolean): void {
    if (!on) {
      setConfirmOff(true);
      return;
    }
    const invalid = nameError(value);
    setError(invalid);
    if (invalid) {
      nameRef.current?.focus();
      return;
    }
    if (!data?.consentGiven) {
      setAskConsent(true);
      return;
    }
    void save('on', { enabled: true, name: value.trim() }, t('fireside.settings.turnedOn'));
  }

  async function agreeAndTurnOn(): Promise<void> {
    if (!data) return;
    if (!(await save('on', { enabled: true, name: value.trim(), consentVersion: data.consentVersion }, t('fireside.settings.turnedOn')))) return;
    flushSync(() => setAskConsent(false));
    switchRef.current?.focus();
  }

  function cancelConsent(): void {
    flushSync(() => setAskConsent(false));
    switchRef.current?.focus();
  }

  function cancelOff(): void {
    flushSync(() => setConfirmOff(false));
    switchRef.current?.focus();
  }

  async function turnOff(): Promise<void> {
    await save('off', { enabled: false }, t('fireside.settings.turnedOff'));
    flushSync(() => setConfirmOff(false));
    switchRef.current?.focus();
  }

  function rename(event: SubmitEvent<HTMLFormElement>): void {
    event.preventDefault();
    const invalid = nameError(value);
    setError(invalid);
    if (invalid) {
      nameRef.current?.focus();
      return;
    }
    if (data?.enabled) void save('name', { enabled: true, name: value.trim() }, t('fireside.settings.nameSaved'));
  }

  const changed = data.enabled && value.trim() !== (data.name ?? '');

  return (
    <div className="flex flex-col gap-4" data-testid="fireside-settings">
      <p>{t('fireside.settings.intro')}</p>
      <p className="text-[0.875rem] text-muted-foreground">{t('fireside.settings.privacy')}</p>
      <div className="flex min-h-[44px] items-center justify-between gap-4">
        <Label htmlFor="fireside-enabled" className="min-w-0 font-normal">
          {t('fireside.settings.use')}
        </Label>
        <Switch
          ref={switchRef}
          id="fireside-enabled"
          checked={data.enabled}
          aria-disabled={busy !== null || confirmOff || askConsent || undefined}
          onCheckedChange={(on) => !busy && !confirmOff && !askConsent && toggle(on)}
        />
      </div>
      {askConsent && (
        <div
          role="group"
          aria-labelledby="fireside-consent-heading"
          className="flex flex-col gap-3 rounded-md border-[length:var(--border-width)] border-border p-3"
        >
          <h3 id="fireside-consent-heading" ref={consentRef} tabIndex={-1} className="font-semibold focus:outline-none">
            {t('fireside.consent.title')}
          </h3>
          <p>{t('fireside.consent.body')}</p>
          <div className="flex flex-wrap gap-2">
            <BusyButton
              type="button"
              busy={busy === 'on'}
              busyLabel={t('fireside.settings.turningOn')}
              onClick={() => void agreeAndTurnOn()}
            >
              {t('fireside.consent.agree')}
            </BusyButton>
            <Button type="button" variant="ghost" onClick={cancelConsent}>
              {t('fireside.consent.cancel')}
            </Button>
          </div>
        </div>
      )}
      {confirmOff && (
        <div className="flex flex-col gap-3 rounded-md border-[length:var(--border-width)] border-border p-3">
          <p ref={confirmRef} tabIndex={-1} className="focus:outline-none">
            {t('fireside.settings.offConfirm')}
          </p>
          <div className="flex flex-wrap gap-2">
            <BusyButton
              type="button"
              variant="destructive"
              busy={busy === 'off'}
              busyLabel={t('fireside.settings.turningOff')}
              onClick={() => void turnOff()}
            >
              {t('fireside.settings.offYes')}
            </BusyButton>
            <Button type="button" variant="ghost" onClick={cancelOff}>
              {t('fireside.settings.offNo')}
            </Button>
          </div>
        </div>
      )}
      <form noValidate onSubmit={rename} className="flex flex-col gap-3">
        <FormField label={t('fireside.settings.name')} hint={t('fireside.settings.nameHint', { max: firesideNameMax })} error={error}>
          {(field) => (
            <Input
              {...field}
              ref={nameRef}
              value={value}
              autoComplete="nickname"
              maxLength={64}
              onChange={(event) => {
                setName(event.target.value);
                if (error) setError(nameError(event.target.value));
              }}
              onBlur={() => value && setError(nameError(value))}
            />
          )}
        </FormField>
        {changed && (
          <div>
            <BusyButton type="submit" busy={busy === 'name'} busyLabel={t('fireside.settings.saving')}>
              {t('fireside.settings.saveName')}
            </BusyButton>
          </div>
        )}
      </form>
      {busy === 'on' && (
        <p role="status" className="flex items-center gap-2 text-muted-foreground">
          <Spinner />
          {t('fireside.settings.turningOn')}
        </p>
      )}
      {problem && (
        <Notice tone="notice">
          <p>{problem}</p>
        </Notice>
      )}
      {data.enabled && (
        <div>
          <TapLink to="/fireside">{t('fireside.settings.open')}</TapLink>
        </div>
      )}
      <LiveRegion message={announcement} />
    </div>
  );
}
