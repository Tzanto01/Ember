import { useQuery } from '@tanstack/react-query';
import { ChevronRight, Info, type LucideIcon, Sparkles } from 'lucide-react';
import { Fragment, type ReactNode, useEffect, useRef, useState, useSyncExternalStore } from 'react';
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
import { DiagnosticsCard } from '@/components/settings/diagnostics-card';
import { DiscordCard } from '@/components/settings/discord-card';
import { ExportCard } from '@/components/settings/export-card';
import { FeedbackForm } from '@/components/settings/feedback-form';
import { GraceCard } from '@/components/settings/grace-card';
import { HabitNotesCard } from '@/components/settings/habit-notes-card';
import { HelpsSettings } from '@/components/settings/helps-settings';
import { JournalSettings } from '@/components/settings/journal-settings';
import { LanguageCard } from '@/components/settings/language-card';
import { PhoneRemindersCard } from '@/components/settings/phone-reminders-card';
import { PushCard } from '@/components/settings/push-card';
import { SecondReminderCard } from '@/components/settings/second-reminder-card';
import { SettingsCard } from '@/components/settings/settings-card';
import {
  type SettingsMessage,
  SettingsStatusProvider,
  useSettingsStatusState,
} from '@/components/settings/settings-status';
import {
  type SettingsGroup,
  settingsBlocks,
  settingsGroupOf,
  settingsGroups,
  settingsSlugAliases,
} from '@/components/settings/settings-groups';
import { StreaksCard } from '@/components/settings/streaks-card';
import { TimerCard } from '@/components/settings/timer-card';
import { TimezoneCard } from '@/components/settings/timezone-card';
import { TwoFactorSettings } from '@/components/settings/two-factor-settings';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import { Skeleton } from '@/components/ui/skeleton';
import { useMePatchField } from '@/hooks/use-me-patch';
import { env } from '@/lib/env';
import { diagnosticsAvailable } from '@/lib/native/diagnostics';
import { updaterAvailable } from '@/lib/native/updater';
import { api, auth, localMode, push } from '@/lib/services';
import type { Me, SecondReminderMinutes } from '@/lib/models';
import { useStore } from '@/lib/store';
import { applyCalm, calmMode } from '@/lib/theme';
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

const linkItems = {
  'whats-new': { to: '/whats-new', icon: Sparkles, summaryKey: 'settings.whatsNewSummary' },
  about: { to: '/about', icon: Info, summaryKey: 'settings.aboutSummary' },
} as const;

const listItemClass =
  'flex min-h-[64px] items-center gap-4 px-4 py-3 no-underline focus-visible:-outline-offset-3';
const listClass =
  'flex flex-col divide-y divide-border overflow-hidden rounded-xl border-[length:var(--border-width)] border-border bg-card text-card-foreground shadow-card';
const helpListClass =
  'overflow-hidden rounded-xl border-[length:var(--border-width)] border-transparent bg-lowday text-lowday-foreground';
const helpHover = 'hover:bg-[color-mix(in_oklch,var(--lowday)_88%,var(--lowday-foreground))]';

/** One entry of the phone Settings list: icon, name and a one-line summary, linking to `to`. */
function ListLink({
  id,
  to,
  icon: Icon,
  name,
  summary,
  apart = false,
  fromList = false,
}: {
  id: string;
  to: string;
  icon: LucideIcon;
  name: string;
  summary: string;
  apart?: boolean;
  fromList?: boolean;
}) {
  return (
    <Link
      to={to}
      state={fromList ? { fromList: true } : undefined}
      aria-labelledby={`${id}-name`}
      aria-describedby={`${id}-summary`}
      className={cn(
        listItemClass,
        apart
          ? cn('text-lowday-foreground hover:text-lowday-foreground', helpHover)
          : 'text-card-foreground hover:bg-accent hover:text-accent-foreground',
      )}
    >
      <Icon aria-hidden className={cn('size-[22px] shrink-0', !apart && 'text-primary')} />
      <span className="flex min-w-0 flex-1 flex-col gap-0.5">
        <span id={`${id}-name`} className="font-semibold">
          {name}
        </span>
        <span id={`${id}-summary`} className={cn('text-[0.875rem]', !apart && 'text-muted-foreground')}>
          {summary}
        </span>
      </span>
      <ChevronRight aria-hidden className={cn('size-[18px] shrink-0', !apart && 'text-muted-foreground')} />
    </Link>
  );
}

/** One group in the wide side rail; `apart` gives Help right now its own soft surface. */
function RailLink({
  group,
  current,
  apart = false,
}: {
  group: Omit<SettingsGroup, 'cards'>;
  current: boolean;
  apart?: boolean;
}) {
  const { t } = useTranslation();
  return (
    <Link
      to={`/settings/${group.slug}`}
      aria-current={current ? 'page' : undefined}
      className={cn(
        'flex min-h-[44px] items-center gap-3 rounded-md px-3 no-underline focus-visible:-outline-offset-3',
        apart
          ? cn(
              'bg-lowday text-lowday-foreground hover:text-lowday-foreground',
              helpHover,
              current && 'font-semibold ring-1 ring-lowday-foreground ring-inset',
            )
          : cn(
              'text-muted-foreground hover:bg-accent hover:text-accent-foreground',
              current &&
                'bg-secondary font-semibold text-secondary-foreground hover:bg-secondary hover:text-secondary-foreground',
            ),
      )}
    >
      <group.icon aria-hidden className="size-[18px] shrink-0" />
      {t(group.nameKey)}
    </Link>
  );
}

/** Splits the shown groups into the labelled blocks, in order, and the groups shown apart after them. */
function blocksOf<T extends Pick<SettingsGroup, 'block'>>(groups: T[]) {
  return {
    blocks: settingsBlocks
      .map((block) => ({ ...block, groups: groups.filter((group) => group.block === block.id) }))
      .filter((block) => block.groups.length > 0),
    apart: groups.filter((group) => group.block === null),
  };
}

/**
 * `/settings` and `/settings/:group`: every settings card, in the groups of `settingsGroups`. On phones `/settings`
 * lists the groups in their blocks, Help right now apart at the end, and `/settings/:group` shows one with a way back;
 * from `md` a side rail picks the group and `/settings` opens the first. A `#card-id` in the address (also the older
 * `/settings#card-id`) opens that card's group, scrolls to the card and focuses its heading; an older group slug opens
 * the group that took its cards; `?discord=` opens the Discord card's group. Reloads `me` on entry. Every
 * `PATCH /api/me` runs one at a time, in order.
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
  const { fail, patch } = status;

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
  const [calm, changeCalm] = useMePatchField((value: boolean) => patch('calm', { calmMode: value }), {
    onError: (error) => fail('calm', error),
    apply: (value) => {
      const before = calmMode.get();
      applyCalm(value);
      return () => applyCalm(before);
    },
  });
  const [offerNote, changeOfferNote] = useMePatchField(
    (value: boolean) => patch('habitNotes', { offerHabitNote: value }),
    { onError: (error) => fail('habitNotes', error) },
  );
  const [grace, changeGrace] = useMePatchField((value: number) => patch('grace', { graceDaysPerWeek: value }), {
    onError: (error) => fail('grace', error),
  });
  const [second, changeSecond] = useMePatchField(
    (value: SecondReminderMinutes) => patch('second', { secondReminderMinutes: value }),
    { onError: (error) => fail('second', error) },
  );

  const showSecond = !local || env.native;
  const streaksShown = streaks ?? me?.showStreaks !== false;
  const sections = [
    ...(local
      ? [{ id: 'device', title: t('settings.card.device') }]
      : [
          { id: 'account', title: t('settings.card.account') },
          { id: 'two-factor', title: t('settings.card.twoFactor') },
          { id: 'discord', title: t('settings.card.discord') },
        ]),
    { id: 'language', title: t('language.label') },
    { id: 'timezone', title: t('settings.card.timezone') },
    ...(local
      ? []
      : [
          { id: 'consent', title: t('settings.card.privacy') },
          { id: 'export', title: t('settings.card.export') },
        ]),
    { id: 'delete', title: local ? t('settings.card.deleteDevice') : t('settings.card.deleteAccount') },
    ...(local ? [] : [{ id: 'push', title: t('settings.card.push') }]),
    ...(env.native ? [{ id: 'phone-reminders', title: t('settings.card.phoneReminders') }] : []),
    ...(showSecond ? [{ id: 'second-reminder', title: t('settings.card.secondReminder') }] : []),
    { id: 'timer', title: t('settings.card.timer') },
    { id: 'streaks', title: t('settings.card.streaks') },
    ...(streaksShown ? [{ id: 'grace', title: t('settings.card.grace') }] : []),
    { id: 'habit-notes', title: t('settings.card.habitNotes') },
    { id: 'routines', title: t('settings.card.routines') },
    ...(local ? [] : [{ id: 'journal', title: t('settings.card.journal') }]),
    { id: 'moods', title: t('settings.card.moods') },
    { id: 'helps', title: t('settings.card.helps') },
    ...(local ? [] : [{ id: 'moments', title: t('settings.card.moments') }]),
    { id: 'appearance', title: t('settings.card.appearance') },
    ...(local ? [] : [{ id: 'fireside', title: t('fireside.title') }]),
    { id: 'feedback', title: t('settings.card.feedback') },
    ...(diagnosticsAvailable() ? [{ id: 'diagnostics', title: t('settings.card.diagnostics') }] : []),
    ...(updaterAvailable() ? [{ id: 'app-updates', title: t('settings.card.appUpdates') }] : []),
    { id: 'whats-new', title: t('title.whatsNew') },
    { id: 'about', title: t('title.about') },
    { id: 'help', title: t('settings.card.help') },
  ];
  const title = (id: string) => sections.find((section) => section.id === id)?.title ?? '';
  const groups = settingsGroups
    .map((group) => ({
      ...group,
      cards: group.cards.flatMap((id) => sections.filter((section) => section.id === id)),
    }))
    .filter((group) => group.cards.length > 0);
  const listed = blocksOf(groups);
  const summaryOf = (group: Omit<SettingsGroup, 'cards'>) => t((local && group.localSummaryKey) || group.summaryKey);
  const visibleGroup = (slug?: string) => groups.find((group) => group.slug === slug);
  const query = new URLSearchParams(location.search);
  const discordPending = !!discordNotice && query.has('discord');
  const slug = params['group'];
  const current =
    visibleGroup(settingsGroupOf(location.hash.slice(1))?.slug) ??
    visibleGroup(slug && (settingsSlugAliases[slug] ?? slug)) ??
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

  function renderCard(id: string, me: Me): ReactNode {
    switch (id) {
      case 'device':
        return (
          <SettingsCard id="device" title={title('device')}>
            <DeviceDataSettings />
          </SettingsCard>
        );
      case 'account':
        return <AccountCard title={title('account')} me={me} />;
      case 'two-factor':
        return (
          <SettingsCard id="two-factor" title={title('two-factor')}>
            <TwoFactorSettings me={me} />
          </SettingsCard>
        );
      case 'discord':
        return <DiscordCard title={title('discord')} me={me} />;
      case 'language':
        return <LanguageCard title={title('language')} local={local} />;
      case 'timezone':
        return <TimezoneCard title={title('timezone')} me={me} />;
      case 'consent':
        return <ConsentCard title={title('consent')} me={me} />;
      case 'export':
        return <ExportCard title={title('export')} />;
      case 'delete':
        return (
          <DeleteCard
            title={title('delete')}
            me={me}
            local={local}
            more={
              !local && (
                <p>
                  <TapLink to="/delete-account">{t('deleteAccount.settingsLink')}</TapLink>
                </p>
              )
            }
          />
        );
      case 'push':
        return <PushCard title={title('push')} me={me} destinations={destinations} />;
      case 'phone-reminders':
        return <PhoneRemindersCard title={title('phone-reminders')} local={local} />;
      case 'second-reminder':
        return (
          <SecondReminderCard
            title={title('second-reminder')}
            value={second ?? me.secondReminderMinutes}
            onChange={changeSecond}
          />
        );
      case 'timer':
        return <TimerCard title={title('timer')} />;
      case 'streaks':
        return <StreaksCard title={title('streaks')} checked={streaksShown} onCheckedChange={changeStreaks} />;
      case 'grace':
        return <GraceCard title={title('grace')} value={grace ?? me.graceDaysPerWeek} onChange={changeGrace} />;
      case 'habit-notes':
        return (
          <HabitNotesCard
            title={title('habit-notes')}
            checked={offerNote ?? me.offerHabitNote}
            onCheckedChange={changeOfferNote}
          />
        );
      case 'routines':
        return (
          <SettingsCard id="routines" title={title('routines')}>
            <p>{t('settings.routines.intro')}</p>
            <div>
              <Button asChild variant="secondary">
                <Link to="/routines">{t('settings.routines.link')}</Link>
              </Button>
            </div>
          </SettingsCard>
        );
      case 'journal':
        return (
          <SettingsCard id="journal" title={title('journal')}>
            <JournalSettings me={me} />
          </SettingsCard>
        );
      case 'moods':
        return (
          <SettingsCard id="moods" title={title('moods')}>
            <MoodManager />
          </SettingsCard>
        );
      case 'helps':
        return (
          <SettingsCard id="helps" title={title('helps')}>
            <HelpsSettings />
          </SettingsCard>
        );
      case 'moments':
        return (
          <SettingsCard id="moments" title={title('moments')}>
            <p>{t('settings.moments.intro')}</p>
            <div>
              <TapLink to="/moments">{t('settings.moments.open')}</TapLink>
            </div>
          </SettingsCard>
        );
      case 'appearance':
        return (
          <AppearanceCard
            title={title('appearance')}
            local={local}
            calm={calm ?? me.calmMode === true}
            onCalmChange={changeCalm}
          />
        );
      case 'fireside':
        return (
          <SettingsCard id="fireside" title={title('fireside')}>
            <FiresideSettings />
          </SettingsCard>
        );
      case 'feedback':
        return (
          <SettingsCard id="feedback" title={title('feedback')}>
            <FeedbackForm />
          </SettingsCard>
        );
      case 'diagnostics':
        return <DiagnosticsCard title={title('diagnostics')} />;
      case 'app-updates':
        return (
          <SettingsCard id="app-updates" title={title('app-updates')}>
            <AppUpdatesSettings />
          </SettingsCard>
        );
      case 'help':
        return (
          <SettingsCard id="help" title={title('help')}>
            <CrisisLines />
          </SettingsCard>
        );
      default:
        return null;
    }
  }

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
        <nav aria-label={t('settings.groupsLabel')} className="flex flex-col gap-6">
          {listed.blocks.map((block) => (
            <div key={block.id} className="flex flex-col gap-2">
              <h2
                id={`settings-block-${block.id}`}
                className="px-1 text-[0.875rem] font-semibold text-muted-foreground"
              >
                {t(block.nameKey)}
              </h2>
              <ul aria-labelledby={`settings-block-${block.id}`} className={listClass}>
                {block.groups.map((group) => (
                  <li key={group.slug}>
                    <ListLink
                      id={`group-${group.slug}`}
                      to={`/settings/${group.slug}`}
                      icon={group.icon}
                      name={t(group.nameKey)}
                      summary={summaryOf(group)}
                      fromList
                    />
                  </li>
                ))}
              </ul>
            </div>
          ))}
          {listed.apart.length > 0 && (
            <ul className={helpListClass}>
              {listed.apart.map((group) => (
                <li key={group.slug}>
                  <ListLink
                    id={`group-${group.slug}`}
                    to={`/settings/${group.slug}`}
                    icon={group.icon}
                    name={t(group.nameKey)}
                    summary={summaryOf(group)}
                    apart
                    fromList
                  />
                </li>
              ))}
            </ul>
          )}
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
            className="sticky top-[calc(56px+env(safe-area-inset-top)+1.5rem)] flex max-h-[calc(100dvh-56px-env(safe-area-inset-top)-3rem)] flex-col gap-2 overflow-y-auto"
          >
            <h2 id="settings-rail-label" className="px-3 text-[1rem] font-semibold">
              {t('title.settings')}
            </h2>
            {listed.blocks.map((block) => (
              <div key={block.id} className="flex flex-col gap-1">
                <h3
                  id={`settings-rail-block-${block.id}`}
                  className="px-3 pt-2 text-[0.8125rem] font-semibold text-muted-foreground"
                >
                  {t(block.nameKey)}
                </h3>
                <ul aria-labelledby={`settings-rail-block-${block.id}`} className="flex flex-col gap-1">
                  {block.groups.map((group) => (
                    <li key={group.slug}>
                      <RailLink group={group} current={group.slug === current.slug} />
                    </li>
                  ))}
                </ul>
              </div>
            ))}
            {listed.apart.length > 0 && (
              <ul className="mt-3 flex flex-col gap-1">
                {listed.apart.map((group) => (
                  <li key={group.slug}>
                    <RailLink group={group} current={group.slug === current.slug} apart />
                  </li>
                ))}
              </ul>
            )}
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
              {current.cards
                .filter((card) => !(card.id in linkItems))
                .map((card) => (
                  <Fragment key={card.id}>{renderCard(card.id, me)}</Fragment>
                ))}
              {current.cards.some((card) => card.id in linkItems) && (
                <ul className={listClass}>
                  {current.cards
                    .filter((card) => card.id in linkItems)
                    .map((card) => {
                      const item = linkItems[card.id as keyof typeof linkItems];
                      return (
                        <li key={card.id} id={card.id}>
                          <ListLink
                            id={`${card.id}-link`}
                            to={item.to}
                            icon={item.icon}
                            name={card.title}
                            summary={t(item.summaryKey)}
                          />
                        </li>
                      );
                    })}
                </ul>
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
