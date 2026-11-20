import { useQuery } from '@tanstack/react-query';
import { ChevronRight, Sparkles } from 'lucide-react';
import {
  type SubmitEvent,
  useEffect,
  useRef,
  useState,
  useSyncExternalStore,
} from 'react';
import { flushSync } from 'react-dom';
import { Trans, useTranslation } from 'react-i18next';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import { BusyButton, TapLink } from '@/components/auth/busy-button';
import { BackLink } from '@/components/back-link';
import { useField, type Validator, validateAll } from '@/components/auth/use-field';
import { FiresideSettings } from '@/components/fireside/fireside-settings';
import { FormField, focusFirstInvalid } from '@/components/form-field';
import { reminderDestinationsKey } from '@/components/habits/device-reminders';
import { MoodManager } from '@/components/journal/mood-manager';
import { CrisisLines } from '@/components/moment/crisis-lines';
import { DeleteDeviceData, DeviceDataSettings } from '@/components/local/device-data-settings';
import { PageHeader } from '@/components/page-header';
import { AccountCard } from '@/components/settings/account-card';
import { AppearanceCard } from '@/components/settings/appearance-card';
import { AppUpdatesSettings } from '@/components/settings/app-updates-settings';
import { DiscordCard } from '@/components/settings/discord-card';
import { DmRemindersCard } from '@/components/settings/dm-reminders-card';
import { FeedbackForm } from '@/components/settings/feedback-form';
import { HelpsSettings } from '@/components/settings/helps-settings';
import { JournalSettings } from '@/components/settings/journal-settings';
import { PhoneRemindersCard } from '@/components/settings/phone-reminders-card';
import { PushCard } from '@/components/settings/push-card';
import { SettingsCard } from '@/components/settings/settings-card';
import {
  type SettingsMessage,
  SettingsStatusProvider,
  useSettingsStatusState,
} from '@/components/settings/settings-status';
import { settingsGroupOf, settingsGroups } from '@/components/settings/settings-groups';
import { TwoFactorSettings } from '@/components/settings/two-factor-settings';
import { Button } from '@/components/ui/button';
import { Input, PasswordInput } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Notice } from '@/components/ui/notice';
import { NativeSelect } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Switch } from '@/components/ui/switch';
import { useMePatchField } from '@/hooks/use-me-patch';
import { saveFile } from '@/lib/browser-download';
import { env } from '@/lib/env';
import { browserTimeZone, formatInstantDate, isValidTimeZone, timeZoneLabel } from '@/lib/local-date';
import {
  type ExportFormat,
  graceOptions,
  secondReminderChoices,
  type SecondReminderMinutes,
} from '@/lib/models';
import { openOnWeb } from '@/lib/native/browser';
import { updaterAvailable } from '@/lib/native/updater';
import { asApiError } from '@/lib/problem';
import { api, auth, localMode, push } from '@/lib/services';
import { useStore } from '@/lib/store';
import { t } from '@/lib/i18n';
import { cn } from '@/lib/utils';

const discordOutcomes = {
  linked: 'success',
  conflict: 'notice',
  error: 'notice',
  password: 'notice',
} as const satisfies Record<string, SettingsMessage['tone']>;

type DiscordOutcome = keyof typeof discordOutcomes;

function discordMessage(outcome: DiscordOutcome): SettingsMessage {
  return { tone: discordOutcomes[outcome], text: t(`settings.discord.outcome.${outcome}`) };
}

const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : [];

function secondReminderLabel(minutes: SecondReminderMinutes): string {
  if (minutes === 0) return t('settings.second.off');
  return minutes % 60 === 0
    ? t('settings.second.hours', { count: minutes / 60 })
    : t('settings.second.minutes', { count: minutes });
}

const exportFormats: ExportFormat[] = ['json', 'csv'];
const wideQuery = '(min-width: 48rem)';

function zoneError(value: string): string | null {
  const zone = value.trim();
  if (!zone) return t('settings.timezone.empty');
  return isValidTimeZone(zone) ? null : t('settings.timezone.unknown');
}

const passwordRequired: Validator = (value) => (value ? null : t('settings.passwordRequired'));
const noPassword: Validator = () => null;
const deleteWord: Validator = (value) => {
  const word = value.trim().normalize('NFC');
  const expected = t('delete.word');
  if (!word) return t('settings.delete.typeDelete', { word: expected });
  return word === expected ? null : t('settings.delete.typeDeleteCaps', { word: expected });
};

function subscribeWide(change: () => void): () => void {
  const query = window.matchMedia(wideQuery);
  query.addEventListener('change', change);
  return () => query.removeEventListener('change', change);
}

/** Whether the screen is `md` or wider, where Settings docks its groups in a side rail. */
function useWide(): boolean {
  return useSyncExternalStore(subscribeWide, () => window.matchMedia(wideQuery).matches);
}

/** Scrolls to a settings card and focuses its heading. */
function focusSection(id: string): void {
  document.getElementById(id)?.scrollIntoView();
  document.getElementById(`${id}-heading`)?.focus({ preventScroll: true });
}

/**
 * `/settings` and `/settings/:group`: account (who is signed in, change password, sign out), appearance, reminders,
 * push on this device, grace days, timezone, moods, Discord, two-step sign-in, private journal, export and account
 * deletion, in the groups of `settingsGroups`. On phones `/settings` lists the groups and `/settings/:group` shows one
 * with a way back; from `md` a side rail picks the group and `/settings` opens the first. A `#card-id` in the address
 * (also the older `/settings#card-id`) opens that card's group, scrolls to the card and focuses its heading;
 * `?discord=` opens the Discord card's group. Reloads `me` on entry. Every `PATCH /api/me` runs one at a time, in order.
 */
export default function SettingsPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const params = useParams();
  const wide = useWide();
  const me = useStore(auth.me);
  const local = useStore(localMode.mode) === 'local';
  const pushAvailable = useStore(push.available);
  const status = useSettingsStatusState();
  const { busy, message, start, finish, fail, outcome, savedStatus, patch } = status;

  const scrolled = useRef<string | null>(null);
  const tzForm = useRef<HTMLFormElement>(null);
  const deleteForm = useRef<HTMLFormElement>(null);
  const withdrawButton = useRef<HTMLButtonElement>(null);
  const withdrawText = useRef<HTMLParagraphElement>(null);
  const claimed = useRef<Promise<void> | null>(null);

  const [exporting, setExporting] = useState<ExportFormat | null>(null);
  const [zone, setZone] = useState<string | null>(null);
  const [zoneTouched, setZoneTouched] = useState(false);
  const [zoneServerError, setZoneServerError] = useState<string | null>(null);
  const [confirmWithdraw, setConfirmWithdraw] = useState(false);
  const [deleteErrors, setDeleteErrors] = useState<{ password?: string; confirm?: string }>({});
  const [browserZone] = useState(browserTimeZone);
  const [discordOutcome] = useState<DiscordOutcome | null>(() => {
    const outcome = new URLSearchParams(location.search).get('discord');
    return outcome && Object.hasOwn(discordOutcomes, outcome) ? (outcome as DiscordOutcome) : null;
  });
  const discordNotice = discordOutcome && discordMessage(discordOutcome);

  const password = useField(me?.hasPassword ? passwordRequired : noPassword);
  const confirm = useField(deleteWord);

  useEffect(() => {
    const params = new URLSearchParams(location.search);
    const linked = params.get('discord') === 'linked';
    void (linked ? auth.refreshAntiforgery() : Promise.resolve()).then(() => auth.loadMe(true)).catch(() => null);
    if (!local) claimed.current = push.claim();
  // eslint-disable-next-line react-hooks/exhaustive-deps -- runs once on mount; adding deps would rerun it
  }, []);

  const [grace, changeGrace] = useMePatchField((value: number) => patch('grace', { graceDaysPerWeek: value }), {
    onError: (error) => fail('grace', error),
  });
  const [streaks, changeStreaks] = useMePatchField((value: boolean) => patch('streaks', { showStreaks: value }), {
    onError: (error) => fail('streaks', error),
  });
  const [offerNote, changeOfferNote] = useMePatchField(
    (value: boolean) => patch('habitNotes', { offerHabitNote: value }),
    { onError: (error) => fail('habitNotes', error) },
  );
  const [second, changeSecond] = useMePatchField(
    (value: SecondReminderMinutes) => patch('second', { secondReminderMinutes: value }),
    { onError: (error) => fail('second', error) },
  );

  async function saveTimezone(chosen?: string): Promise<void> {
    if (status.isBusy()) return;
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

  async function exportAs(format: ExportFormat): Promise<void> {
    if (status.isBusy()) return;
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

  function askWithdraw(open: boolean): void {
    flushSync(() => setConfirmWithdraw(open));
    (open ? withdrawText : withdrawButton).current?.focus();
  }

  async function withdrawConsent(): Promise<void> {
    if (status.isBusy()) return;
    start('consent');
    try {
      await api.withdrawConsent();
      await auth.loadMe(true);
    } catch (error) {
      fail('consent', asApiError(error));
      finish();
      return;
    }
    finish();
    await navigate('/consent');
  }

  async function deleteAccount(event: SubmitEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();
    if (status.isBusy() || !me) return;
    if (!validateAll(deleteForm.current, me.hasPassword ? [password, confirm] : [confirm])) return;
    start('delete');
    setDeleteErrors({});
    try {
      await api.deleteAccount(
        me.hasPassword ? { password: password.value, confirm: 'DELETE' } : { confirm: 'DELETE' },
      );
    } catch (error) {
      const apiError = asApiError(error);
      if (apiError.status === 400) {
        flushSync(() =>
          setDeleteErrors(
            me.hasPassword
              ? { password: t('settings.passwordMismatch') }
              : { confirm: t('settings.delete.typeDeleteCaps', { word: t('delete.word') }) },
          ),
        );
        focusFirstInvalid(deleteForm.current);
      } else {
        fail('delete', apiError);
      }
      finish();
      return;
    }
    await push.forgetDevice();
    await auth.wipeDevice();
    auth.clearSession();
    await auth.refreshAntiforgery();
    await navigate('/login?reason=deleted');
  }

  const showDm = !local && !!me && (me.dmReminders.paused || message?.section === 'dm');
  const showSecond = !local || env.native;
  const streaksShown = streaks ?? me?.showStreaks !== false;
  const sections = [
    ...(local
      ? [{ id: 'device', title: t('settings.card.device') }]
      : [{ id: 'account', title: t('settings.card.account') }]),
    { id: 'appearance', title: t('settings.card.appearance') },
    ...(local ? [] : [{ id: 'push', title: t('settings.card.push') }]),
    ...(showDm ? [{ id: 'dm-reminders', title: t('settings.card.dmReminders') }] : []),
    ...(env.native ? [{ id: 'phone-reminders', title: t('settings.card.phoneReminders') }] : []),
    ...(showSecond ? [{ id: 'second-reminder', title: t('settings.card.secondReminder') }] : []),
    { id: 'routines', title: t('settings.card.routines') },
    { id: 'streaks', title: t('settings.card.streaks') },
    { id: 'habit-notes', title: t('settings.card.habitNotes') },
    ...(streaksShown ? [{ id: 'grace', title: t('settings.card.grace') }] : []),
    { id: 'timezone', title: t('settings.card.timezone') },
    { id: 'moods', title: t('settings.card.moods') },
    ...(local ? [] : [{ id: 'fireside', title: t('fireside.title') }]),
    ...(local
      ? []
      : [
          { id: 'two-factor', title: t('settings.card.twoFactor') },
          { id: 'discord', title: 'Discord' },
          { id: 'journal', title: t('settings.card.journal') },
          { id: 'moments', title: t('settings.card.moments') },
        ]),
    ...(updaterAvailable() ? [{ id: 'app-updates', title: t('settings.card.appUpdates') }] : []),
    { id: 'helps', title: t('settings.card.helps') },
    ...(local ? [] : [{ id: 'consent', title: t('settings.card.privacy') }]),
    ...(local ? [] : [{ id: 'export', title: t('settings.card.export') }]),
    { id: 'delete', title: local ? t('settings.card.deleteDevice') : t('settings.card.deleteAccount') },
    { id: 'feedback', title: t('settings.card.feedback') },
    { id: 'help', title: t('settings.card.help') },
  ];
  const title = (id: string) => sections.find((section) => section.id === id)?.title ?? '';
  const groups = settingsGroups
    .map((group) => ({ ...group, cards: sections.filter((section) => group.cards.includes(section.id)) }))
    .filter((group) => group.cards.length > 0);
  const visibleGroup = (slug?: string) => groups.find((group) => group.slug === slug);
  const query = new URLSearchParams(location.search);
  const discordPending = !!discordNotice && query.has('discord');
  const current =
    visibleGroup(settingsGroupOf(location.hash.slice(1))?.slug) ??
    visibleGroup(params['group']) ??
    (discordPending ? visibleGroup(settingsGroupOf('discord')?.slug) : undefined) ??
    (wide ? groups[0] : undefined);
  const shown = (id: string) => !!current?.cards.some((card) => card.id === id);
  const destinations = useQuery({
    queryKey: reminderDestinationsKey,
    queryFn: async () => {
      await claimed.current;
      return api.listReminderDestinations();
    },
    enabled: shown('push'),
  });
  query.delete('discord');
  const pathname = current ? `/settings/${current.slug}` : '/settings';
  const search = query.toString();
  const redirect =
    pathname !== location.pathname || discordPending
      ? `${pathname}${search ? `?${search}` : ''}${location.hash}`
      : null;
  const fromList = (location.state as { fromList?: boolean } | null)?.fromList === true;

  useEffect(() => {
    if (redirect) void navigate(redirect, { replace: true });
  // eslint-disable-next-line react-hooks/exhaustive-deps -- navigates only when the redirect changes
  }, [redirect]);

  useEffect(() => {
    const id = location.hash.slice(1);
    if (redirect || !me || !id || scrolled.current === id || !document.getElementById(`${id}-heading`)) return;
    const frame = requestAnimationFrame(() => {
      scrolled.current = id;
      focusSection(id);
    });
    return () => cancelAnimationFrame(frame);
  }, [me, pushAvailable, location.hash, redirect]);

  const zoneValue = zone ?? me?.timezone ?? '';
  const zoneMessage = (zoneTouched ? zoneError(zoneValue) : null) ?? zoneServerError;
  const suggestZone = !!me && isValidTimeZone(browserZone) && (!me.timezoneSet || me.timezone !== browserZone);
  const graceShown = grace ?? me?.graceDaysPerWeek ?? 0;
  const secondShown = second ?? me?.secondReminderMinutes ?? 0;

  const notice = discordNotice && (
    <Notice tone={discordNotice.tone}>
      <p>{discordNotice.text}</p>
    </Notice>
  );

  if (!current) {
    return (
      <div className="flex max-w-[40rem] flex-col gap-6 2xl:mx-auto 2xl:w-full">
        <PageHeader title={t('title.settings')} />
        {notice}
        <nav aria-label={t('settings.groupsLabel')}>
          <ul className="flex flex-col divide-y divide-border overflow-hidden rounded-xl border-[length:var(--border-width)] border-border bg-card text-card-foreground shadow-card">
            {groups.map((group) => (
              <li key={group.slug}>
                <Link
                  to={`/settings/${group.slug}`}
                  state={{ fromList: true }}
                  aria-labelledby={`group-${group.slug}-name`}
                  aria-describedby={`group-${group.slug}-summary`}
                  className="flex min-h-[64px] items-center gap-4 px-4 py-3 text-card-foreground no-underline hover:bg-accent hover:text-accent-foreground focus-visible:-outline-offset-3"
                >
                  <group.icon aria-hidden className="size-[22px] shrink-0 text-primary" />
                  <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                    <span id={`group-${group.slug}-name`} className="font-semibold">
                      {t(group.nameKey)}
                    </span>
                    <span id={`group-${group.slug}-summary`} className="text-[0.875rem] text-muted-foreground">
                      {group.summaryKey ? t(group.summaryKey) : group.cards.map((card) => card.title).join(' · ')}
                    </span>
                  </span>
                  <ChevronRight aria-hidden className="size-[18px] shrink-0 text-muted-foreground" />
                </Link>
              </li>
            ))}
            <li>
              <Link
                to="/whats-new"
                aria-labelledby="whats-new-name"
                aria-describedby="whats-new-summary"
                className="flex min-h-[64px] items-center gap-4 px-4 py-3 text-card-foreground no-underline hover:bg-accent hover:text-accent-foreground focus-visible:-outline-offset-3"
              >
                <Sparkles aria-hidden className="size-[22px] shrink-0 text-primary" />
                <span className="flex min-w-0 flex-1 flex-col gap-0.5">
                  <span id="whats-new-name" className="font-semibold">
                    {t('title.whatsNew')}
                  </span>
                  <span id="whats-new-summary" className="text-[0.875rem] text-muted-foreground">
                    {t('settings.whatsNewSummary')}
                  </span>
                </span>
                <ChevronRight aria-hidden className="size-[18px] shrink-0 text-muted-foreground" />
              </Link>
            </li>
          </ul>
        </nav>
      </div>
    );
  }

  return (
    <SettingsStatusProvider status={status}>
      <div className={cn('flex flex-col gap-6', wide && 'grid grid-cols-[14rem_minmax(0,40rem)] items-start gap-8 2xl:mx-auto')}>
        {wide && (
          <nav
            aria-labelledby="settings-rail-label"
            className="sticky top-[calc(56px+env(safe-area-inset-top)+1.5rem)] flex flex-col gap-2"
          >
            <p id="settings-rail-label" className="px-3 text-[0.875rem] font-semibold text-muted-foreground">
              {t('title.settings')}
            </p>
            <ul className="flex flex-col gap-1">
              {groups.map((group) => (
                <li key={group.slug}>
                  <Link
                    to={`/settings/${group.slug}`}
                    aria-current={group.slug === current.slug ? 'page' : undefined}
                    className={cn(
                      'flex min-h-[44px] items-center gap-3 rounded-md px-3 text-muted-foreground no-underline hover:bg-accent hover:text-accent-foreground',
                      group.slug === current.slug &&
                        'bg-secondary font-semibold text-secondary-foreground hover:bg-secondary hover:text-secondary-foreground',
                    )}
                  >
                    <group.icon aria-hidden className="size-[18px] shrink-0" />
                    {t(group.nameKey)}
                  </Link>
                </li>
              ))}
              <li>
                <Link
                  to="/whats-new"
                  className="flex min-h-[44px] items-center gap-3 rounded-md px-3 text-muted-foreground no-underline hover:bg-accent hover:text-accent-foreground"
                >
                  <Sparkles aria-hidden className="size-[18px] shrink-0" />
                  {t('title.whatsNew')}
                </Link>
              </li>
            </ul>
          </nav>
        )}

        <div className="flex min-w-0 flex-col gap-6">
          {!wide && (
            <BackLink to="/settings" label={t('title.settings')} name={t('settings.backToList')} viaHistory={fromList} />
          )}
          <PageHeader title={t(current.nameKey)} />
          {notice}
          {me ? (
            <>
              {shown('device') && (
                <SettingsCard id="device" title={title('device')}>
                  <DeviceDataSettings />
                </SettingsCard>
              )}

              {shown('account') && <AccountCard title={title('account')} me={me} />}

              {shown('appearance') && <AppearanceCard title={title('appearance')} me={me} local={local} />}

              {shown('push') && <PushCard title={title('push')} destinations={destinations} />}

              {shown('dm-reminders') && <DmRemindersCard title={title('dm-reminders')} me={me} />}

              {shown('phone-reminders') && <PhoneRemindersCard title={title('phone-reminders')} local={local} />}

              {shown('second-reminder') && (
                <SettingsCard id="second-reminder" title={title('second-reminder')}>
                  <FormField label={t('settings.card.secondReminder')} hint={t('settings.second.hint')}>
                    {(field) => (
                      <NativeSelect
                        {...field}
                        value={String(secondShown)}
                        onChange={(event) => changeSecond(Number(event.target.value) as SecondReminderMinutes)}
                      >
                        {secondReminderChoices.map((n) => (
                          <option key={n} value={n}>
                            {secondReminderLabel(n)}
                          </option>
                        ))}
                      </NativeSelect>
                    )}
                  </FormField>
                  {savedStatus('second')}
                  {outcome('second')}
                </SettingsCard>
              )}

              {shown('routines') && (
                <SettingsCard id="routines" title={title('routines')}>
                  <p>{t('settings.routines.intro')}</p>
                  <div>
                    <Button asChild variant="secondary">
                      <Link to="/routines">{t('settings.routines.link')}</Link>
                    </Button>
                  </div>
                </SettingsCard>
              )}

              {shown('streaks') && (
                <SettingsCard id="streaks" title={title('streaks')}>
                  <div className="flex min-h-[44px] items-center justify-between gap-4">
                    <Label htmlFor="show-streaks" className="min-w-0 font-normal">
                      {t('settings.streaks.label')}
                    </Label>
                    <Switch
                      id="show-streaks"
                      aria-describedby="show-streaks-hint"
                      checked={streaksShown}
                      onCheckedChange={changeStreaks}
                    />
                  </div>
                  <p id="show-streaks-hint" className="text-[0.875rem] text-muted-foreground">
                    {t('settings.streaks.hint')}
                  </p>
                  {savedStatus('streaks')}
                  {outcome('streaks')}
                </SettingsCard>
              )}

              {shown('habit-notes') && (
                <SettingsCard id="habit-notes" title={title('habit-notes')}>
                  <div className="flex min-h-[44px] items-center justify-between gap-4">
                    <Label htmlFor="offer-habit-note" className="min-w-0 font-normal">
                      {t('settings.habitNotes.label')}
                    </Label>
                    <Switch
                      id="offer-habit-note"
                      aria-describedby="offer-habit-note-hint"
                      checked={offerNote ?? me.offerHabitNote}
                      onCheckedChange={changeOfferNote}
                    />
                  </div>
                  <p id="offer-habit-note-hint" className="text-[0.875rem] text-muted-foreground">
                    {t('settings.habitNotes.hint')}
                  </p>
                  {savedStatus('habitNotes')}
                  {outcome('habitNotes')}
                </SettingsCard>
              )}

              {shown('grace') && (
                <SettingsCard id="grace" title={title('grace')}>
                  <FormField
                    label={t('settings.grace.label')}
                    hint={t('settings.grace.hint')}
                  >
                    {(field) => (
                      <NativeSelect
                        {...field}
                        value={String(graceShown)}
                        onChange={(event) => changeGrace(Number(event.target.value))}
                      >
                        {graceOptions.map((n) => (
                          <option key={n} value={n}>
                            {n === 0 ? t('settings.grace.none') : t('settings.grace.perWeek', { count: n })}
                          </option>
                        ))}
                      </NativeSelect>
                    )}
                  </FormField>
                  <p className="text-[0.875rem] text-muted-foreground">{t('settings.grace.note')}</p>
                  {savedStatus('grace')}
                  {outcome('grace')}
                </SettingsCard>
              )}

              {shown('timezone') && (
                <SettingsCard id="timezone" title={title('timezone')}>
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
              )}

              {shown('moods') && (
                <SettingsCard id="moods" title={title('moods')}>
                  <MoodManager />
                </SettingsCard>
              )}

              {shown('fireside') && (
                <SettingsCard id="fireside" title={title('fireside')}>
                  <FiresideSettings />
                </SettingsCard>
              )}

              {shown('two-factor') && (
                <SettingsCard id="two-factor" title={title('two-factor')}>
                  <TwoFactorSettings me={me} />
                </SettingsCard>
              )}

              {shown('discord') && <DiscordCard title={title('discord')} me={me} />}

              {shown('app-updates') && (
                <SettingsCard id="app-updates" title={title('app-updates')}>
                  <AppUpdatesSettings />
                </SettingsCard>
              )}

              {shown('journal') && (
                <SettingsCard id="journal" title={title('journal')}>
                  <JournalSettings me={me} />
                </SettingsCard>
              )}

              {shown('moments') && (
                <SettingsCard id="moments" title={title('moments')}>
                  <p>{t('settings.moments.intro')}</p>
                  <div>
                    <TapLink to="/moments">{t('settings.moments.open')}</TapLink>
                  </div>
                </SettingsCard>
              )}

              {shown('helps') && (
                <SettingsCard id="helps" title={title('helps')}>
                  <HelpsSettings />
                </SettingsCard>
              )}

              {shown('consent') && me && (
                <SettingsCard id="consent" title={title('consent')}>
                  {me.consent.given ? (
                    <>
                      <p>{t('settings.privacy.given')}</p>
                      {!confirmWithdraw && (
                        <div>
                          <Button ref={withdrawButton} type="button" variant="secondary" onClick={() => askWithdraw(true)}>
                            {t('settings.privacy.withdraw')}
                          </Button>
                        </div>
                      )}
                      {confirmWithdraw && (
                        <div className="flex flex-col gap-3 rounded-md border-[length:var(--border-width)] border-border p-3">
                          <p ref={withdrawText} tabIndex={-1} className="focus:outline-none">
                            {t('settings.privacy.withdrawConfirm')}
                          </p>
                          <div className="flex flex-wrap gap-3">
                            <BusyButton
                              type="button"
                              variant="destructive"
                              busy={busy === 'consent'}
                              busyLabel={t('auth.saving')}
                              onClick={() => void withdrawConsent()}
                            >
                              {t('settings.privacy.withdrawYes')}
                            </BusyButton>
                            <Button type="button" variant="ghost" onClick={() => askWithdraw(false)}>
                              {t('settings.privacy.withdrawNo')}
                            </Button>
                          </div>
                        </div>
                      )}
                    </>
                  ) : (
                    <>
                      <p>
                        {!me.consent.withdrawnAt
                          ? t('settings.privacy.missing')
                          : me.consent.deletesAt
                            ? t('settings.privacy.withdrawn', { date: formatInstantDate(me.consent.deletesAt, me.timezone) })
                            : t('settings.privacy.withdrawnNoData')}
                      </p>
                      <div>
                        <TapLink to="/consent">{t('settings.privacy.give')}</TapLink>
                      </div>
                    </>
                  )}
                  {outcome('consent')}
                </SettingsCard>
              )}

              {shown('export') && (
                <SettingsCard id="export" title={title('export')}>
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
              )}

              {shown('delete') && (
                <SettingsCard id="delete" title={title('delete')} farewell>
                  {local ? (
                    <DeleteDeviceData />
                  ) : (
                    <>
                      <p>{t('settings.delete.intro')}</p>
                      <form
                        ref={deleteForm}
                        noValidate
                        onSubmit={(event) => void deleteAccount(event)}
                        className="flex flex-col gap-3"
                      >
                        {me.hasPassword && (
                          <>
                            <input
                              className="sr-only"
                              type="email"
                              name="username"
                              autoComplete="username"
                              value={me.email ?? ''}
                              readOnly
                              tabIndex={-1}
                              aria-hidden="true"
                            />
                            <FormField label={t('settings.yourPassword')} error={password.error ?? deleteErrors.password ?? null}>
                              {(field) => (
                                <PasswordInput {...field} {...password.input} autoComplete="current-password" />
                              )}
                            </FormField>
                          </>
                        )}
                        <FormField
                          label={t('settings.delete.confirmLabel', { word: t('delete.word') })}
                          error={confirm.error ?? deleteErrors.confirm ?? null}
                        >
                          {(field) => (
                            <Input
                              {...field}
                              {...confirm.input}
                              type="text"
                              autoComplete="off"
                              autoCapitalize="characters"
                              spellCheck={false}
                            />
                          )}
                        </FormField>
                        {outcome('delete')}
                        <div>
                          <BusyButton
                            type="submit"
                            variant="destructive"
                            busy={busy === 'delete'}
                            busyLabel={t('settings.delete.deleting')}
                          >
                            {t('settings.delete.submit')}
                          </BusyButton>
                        </div>
                      </form>
                    </>
                  )}
                </SettingsCard>
              )}

              {shown('feedback') && (
                <SettingsCard id="feedback" title={title('feedback')}>
                  <FeedbackForm />
                </SettingsCard>
              )}

              {shown('help') && (
                <SettingsCard id="help" title={title('help')}>
                  <CrisisLines />
                  <p>
                    <TapLink to="/about">{t('about.link')}</TapLink>
                  </p>
                </SettingsCard>
              )}
            </>
          ) : (
            <>
              <p role="status" className="sr-only">
                {t('settings.loading')}
              </p>
              <Skeleton className="h-[12rem] rounded-xl" />
              <Skeleton className="h-[8rem] rounded-xl" />
            </>
          )}
        </div>
      </div>
    </SettingsStatusProvider>
  );
}
