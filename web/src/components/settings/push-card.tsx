import { type UseQueryResult, useQueryClient } from '@tanstack/react-query';
import { Check } from 'lucide-react';
import { useRef, useState } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { Link } from 'react-router';
import { BusyButton } from '@/components/auth/busy-button';
import { DestinationName, reminderDestinationsKey } from '@/components/habits/device-reminders';
import { ReminderNamesSetting } from '@/components/settings/reminder-names-setting';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { Notice } from '@/components/ui/notice';
import { Skeleton } from '@/components/ui/skeleton';
import { Switch } from '@/components/ui/switch';
import { env } from '@/lib/env';
import type { ReminderDestination } from '@/lib/models';
import { ApiError, asApiError, problemToMessage } from '@/lib/problem';
import { api, deviceSync, push, reminders } from '@/lib/services';
import { useStore } from '@/lib/store';

function isIos(): boolean {
  return (
    // eslint-disable-next-line @typescript-eslint/no-deprecated -- iPadOS reports MacIntel here and Safari has no replacement API
    /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)
  );
}

function isStandalone(): boolean {
  return (
    window.matchMedia('(display-mode: standalone)').matches ||
    (navigator as Navigator & { standalone?: boolean }).standalone === true
  );
}

/**
 * Where reminders go: a switch per reminder destination of the account, turning on this browser's push or this phone
 * when it isn't one yet, and whether browser reminders show habit names. `destinations` is the page's list query.
 */
export function PushCard({ title, destinations }: { title: string; destinations: UseQueryResult<ReminderDestination[]> }) {
  const { t } = useTranslation();
  const { busy, isBusy, start, finish, markSaved, show, fail, outcome, savedStatus, act } = useSettingsStatus();
  const queryClient = useQueryClient();
  const pushAvailable = useStore(push.available);
  const browserNames = useStore(push.showNames);
  const togglingRef = useRef(new Set<string>());
  const [toggling, setToggling] = useState<Partial<Record<string, boolean>>>({});
  const [toggled, setToggled] = useState<Partial<Record<string, { error: string | null }>>>({});
  const [showIosHint] = useState(() => isIos() && !isStandalone());

  async function toggleDestination(destination: ReminderDestination, enabled: boolean): Promise<void> {
    const { id } = destination;
    if (togglingRef.current.has(id)) return;
    togglingRef.current.add(id);
    setToggling((t) => ({ ...t, [id]: enabled }));
    setToggled((t) => ({ ...t, [id]: undefined }));
    try {
      const updated = await api.setReminderDestination(id, enabled);
      queryClient.setQueryData<ReminderDestination[]>(reminderDestinationsKey, (list) =>
        list?.map((d) => (d.id === updated.id ? updated : d)),
      );
      if (env.native) deviceSync.rebuildReminders();
      setToggled((t) => ({ ...t, [id]: { error: null } }));
    } catch (error) {
      if (!asApiError(error).handled) {
        const text = destination.enabled
          ? t('settings.push.stillOn', { name: destination.label })
          : t('settings.push.stillOff', { name: destination.label });
        setToggled((t) => ({ ...t, [id]: { error: text } }));
      }
    } finally {
      togglingRef.current.delete(id);
      setToggling((t) => ({ ...t, [id]: undefined }));
    }
  }

  const enablePhone = () =>
    act('push', async () => {
      const phone = (await reminders?.enablePhone()) ?? null;
      if (!phone) return { tone: 'info', text: t('settings.phone.notificationsOff') };
      await queryClient.invalidateQueries({ queryKey: reminderDestinationsKey });
      deviceSync.rebuildReminders();
      return { tone: 'success', text: t('settings.phone.on') };
    });

  async function enablePush(): Promise<void> {
    if (isBusy()) return;
    start('push');
    try {
      await push.subscribe();
      await queryClient.invalidateQueries({ queryKey: reminderDestinationsKey });
      show('push', { tone: 'success', text: t('settings.push.on') });
    } catch (error) {
      if (error instanceof ApiError) fail('push', error);
      else if (push.isDenied() || (error instanceof DOMException && error.name === 'NotAllowedError'))
        show('push', { tone: 'notice', text: t('settings.push.denied') });
      else
        show('push', { tone: 'notice', text: t('settings.push.failed') });
    } finally {
      finish();
    }
  }

  async function changeBrowserNames(on: boolean): Promise<void> {
    if (isBusy()) return;
    start('push');
    try {
      await push.setShowNames(on);
      markSaved('push');
    } catch (error) {
      fail('push', asApiError(error));
    } finally {
      finish();
    }
  }

  return (
    <SettingsCard id="push" title={title}>
      {destinations.data ? (
        destinations.data.length > 0 ? (
          <>
            <p>{t('settings.push.intro')}</p>
            <ul className="flex flex-col divide-y divide-border">
              {destinations.data.map((destination) => (
                <li key={destination.id} className="flex flex-col py-2 first:pt-0 last:pb-0">
                  <div className="flex min-h-[44px] items-center justify-between gap-4">
                    <Label
                      htmlFor={`destination-${destination.id}`}
                      className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 font-normal"
                    >
                      <DestinationName destination={destination} />
                    </Label>
                    <Switch
                      id={`destination-${destination.id}`}
                      checked={toggling[destination.id] ?? destination.enabled}
                      aria-disabled={toggling[destination.id] !== undefined || undefined}
                      onCheckedChange={(checked) => void toggleDestination(destination, checked)}
                    />
                  </div>
                  <p role="status" className="inline-flex items-center gap-1.5 font-medium text-foreground">
                    {toggled[destination.id]?.error === null && (
                      <>
                        <Check aria-hidden className="size-[18px] text-primary" />
                        {t('settings.saved')}
                      </>
                    )}
                  </p>
                  {toggled[destination.id]?.error && (
                    <Notice tone="notice" className="mt-2">
                      <p>{toggled[destination.id]?.error}</p>
                    </Notice>
                  )}
                </li>
              ))}
            </ul>
          </>
        ) : (
          <p className="text-muted-foreground">
            {t('settings.push.none')}
            {!(env.native || pushAvailable) && (
              <>
                {' '}
                <Trans
                  i18nKey="settings.push.noneDiscord"
                  components={{
                    anchor: (
                      <Link
                        to={{ pathname: '/settings', hash: 'discord' }}
                        className="text-primary underline underline-offset-4"
                      />
                    ),
                  }}
                />
              </>
            )}
          </p>
        )
      ) : destinations.isError ? (
        !asApiError(destinations.error).handled && (
          <Notice tone="notice">
            <p>{problemToMessage(asApiError(destinations.error)).message}</p>
            <Button
              type="button"
              variant="secondary"
              aria-disabled={destinations.isFetching || undefined}
              onClick={() => void destinations.refetch()}
            >
              {t('settings.tryAgain')}
            </Button>
          </Notice>
        )
      ) : (
        <>
          <p role="status" className="sr-only">
            {t('settings.push.loading')}
          </p>
          <Skeleton className="h-[7rem] rounded-md" />
        </>
      )}
      {destinations.data &&
        !destinations.data.some((d) => d.current) &&
        (env.native ? (
          <div>
            <BusyButton
              type="button"
              busy={busy === 'push'}
              busyLabel={t('settings.turningOn')}
              onClick={() => void enablePhone()}
            >
              {t('settings.phone.turnOn')}
            </BusyButton>
          </div>
        ) : (
          pushAvailable && (
            <>
              <p className="text-muted-foreground">{t('settings.push.browserHint')}</p>
              {showIosHint && (
                <p className="text-muted-foreground">{t('settings.push.iosHint')}</p>
              )}
              <div>
                <BusyButton
                  type="button"
                  busy={busy === 'push'}
                  busyLabel={t('settings.turningOn')}
                  onClick={() => void enablePush()}
                >
                  {t('settings.push.turnOn')}
                </BusyButton>
              </div>
            </>
          )
        ))}
      {!env.native && destinations.data?.some((d) => d.current && d.kind === 'browser') && (
        <>
          <ReminderNamesSetting
            checked={browserNames}
            onCheckedChange={(on) => void changeBrowserNames(on)}
          />
          {savedStatus('push')}
        </>
      )}
      {outcome('push')}
    </SettingsCard>
  );
}
