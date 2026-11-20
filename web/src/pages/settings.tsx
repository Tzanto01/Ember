import { useQuery } from '@tanstack/react-query';
import { ChevronRight, Sparkles } from 'lucide-react';
import { useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { useTranslation } from 'react-i18next';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import { TapLink } from '@/components/auth/busy-button';
import { BackLink } from '@/components/back-link';
import { FiresideSettings } from '@/components/fireside/fireside-settings';
import { reminderDestinationsKey } from '@/components/habits/device-reminders';
import { MoodManager } from '@/components/journal/mood-manager';
import { CrisisLines } from '@/components/moment/crisis-lines';
import { DeviceDataSettings } from '@/components/local/device-data-settings';
import { PageHeader } from '@/components/page-header';
import { AccountCard } from '@/components/settings/account-card';
import { AppearanceCard } from '@/components/settings/appearance-card';
import { AppUpdatesSettings } from '@/components/settings/app-updates-settings';
import { ConsentCard } from '@/components/settings/consent-card';
import { DeleteCard } from '@/components/settings/delete-card';
import { DiscordCard } from '@/components/settings/discord-card';
import { DmRemindersCard } from '@/components/settings/dm-reminders-card';
import { ExportCard } from '@/components/settings/export-card';
import { FeedbackForm } from '@/components/settings/feedback-form';
import { GraceCard } from '@/components/settings/grace-card';
import { HabitNotesCard } from '@/components/settings/habit-notes-card';
import { HelpsSettings } from '@/components/settings/helps-settings';
import { JournalSettings } from '@/components/settings/journal-settings';
import { PhoneRemindersCard } from '@/components/settings/phone-reminders-card';
import { PushCard } from '@/components/settings/push-card';
import { SecondReminderCard } from '@/components/settings/second-reminder-card';
import { SettingsCard } from '@/components/settings/settings-card';
import {
  type SettingsMessage,
  SettingsStatusProvider,
  useSettingsStatusState,
} from '@/components/settings/settings-status';
import { settingsGroupOf, settingsGroups } from '@/components/settings/settings-groups';
import { StreaksCard } from '@/components/settings/streaks-card';
import { TimezoneCard } from '@/components/settings/timezone-card';
import { TwoFactorSettings } from '@/components/settings/two-factor-settings';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import { Skeleton } from '@/components/ui/skeleton';
import { useMePatchField } from '@/hooks/use-me-patch';
import { env } from '@/lib/env';
import { updaterAvailable } from '@/lib/native/updater';
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

const wideQuery = '(min-width: 48rem)';

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
  const { message, fail, patch } = status;

  const scrolled = useRef<string | null>(null);
  const claimed = useRef<Promise<void> | null>(null);

  const [discordOutcome] = useState<DiscordOutcome | null>(() => {
    const outcome = new URLSearchParams(location.search).get('discord');
    return outcome && Object.hasOwn(discordOutcomes, outcome) ? (outcome as DiscordOutcome) : null;
  });
  const discordNotice = discordOutcome && discordMessage(discordOutcome);

  const [claimOnEntry] = useState(!local);

  useEffect(() => {
    const linked = discordOutcome === 'linked';
    void (linked ? auth.refreshAntiforgery() : Promise.resolve()).then(() => auth.loadMe(true)).catch(() => null);
  }, [discordOutcome]);

  useEffect(() => {
    if (claimOnEntry) claimed.current = push.claim();
  }, [claimOnEntry]);

  const [streaks, changeStreaks] = useMePatchField((value: boolean) => patch('streaks', { showStreaks: value }), {
    onError: (error) => fail('streaks', error),
  });

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
  }, [navigate, redirect]);

  useEffect(() => {
    const id = location.hash.slice(1);
    if (redirect || !me || !id || scrolled.current === id || !document.getElementById(`${id}-heading`)) return;
    const frame = requestAnimationFrame(() => {
      scrolled.current = id;
      focusSection(id);
    });
    return () => cancelAnimationFrame(frame);
  }, [me, pushAvailable, location.hash, redirect]);

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

              {shown('second-reminder') && <SecondReminderCard title={title('second-reminder')} me={me} />}

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
                <StreaksCard title={title('streaks')} checked={streaksShown} onCheckedChange={changeStreaks} />
              )}

              {shown('habit-notes') && <HabitNotesCard title={title('habit-notes')} me={me} />}

              {shown('grace') && <GraceCard title={title('grace')} me={me} />}

              {shown('timezone') && <TimezoneCard title={title('timezone')} me={me} />}

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

              {shown('consent') && <ConsentCard title={title('consent')} me={me} />}

              {shown('export') && <ExportCard title={title('export')} />}

              {shown('delete') && <DeleteCard title={title('delete')} me={me} local={local} />}

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
