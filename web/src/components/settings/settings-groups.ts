import {
  Bell,
  BookHeart,
  Globe,
  LifeBuoy,
  type LucideIcon,
  Lock,
  MessageSquare,
  Palette,
  ShieldCheck,
  Sprout,
  Users,
} from 'lucide-react';
import type { en } from '@/lib/i18n';

type SettingsKey = keyof typeof en;

/** One labelled block of the Settings group list. */
export type SettingsBlock = 'you' | 'ember' | 'support';

/**
 * One entry of the Settings group list: its `/settings/<slug>` route, its name's key, the card ids it holds, in
 * order, and its block (`null` for Help right now, which stands apart after the blocks). `summaryKey` is its one-line
 * summary; `localSummaryKey` replaces it without an account, where some of its cards are hidden, and
 * `localWebSummaryKey` without an account on the web, where fewer still show.
 */
export interface SettingsGroup {
  slug: string;
  nameKey: SettingsKey;
  icon: LucideIcon;
  cards: string[];
  block: SettingsBlock | null;
  summaryKey: SettingsKey;
  localSummaryKey?: SettingsKey;
  localWebSummaryKey?: SettingsKey;
}

/** The blocks of the Settings group list, in order. */
export const settingsBlocks: { id: SettingsBlock; nameKey: SettingsKey }[] = [
  { id: 'you', nameKey: 'settings.block.you' },
  { id: 'ember', nameKey: 'settings.block.ember' },
  { id: 'support', nameKey: 'settings.block.support' },
];

/**
 * The Settings groups, in list order. Every settings card id belongs to exactly one group; a group whose cards are all
 * hidden in the current mode is left out of the list.
 */
export const settingsGroups: SettingsGroup[] = [
  {
    slug: 'account',
    nameKey: 'settings.group.account',
    icon: ShieldCheck,
    cards: ['device', 'account', 'two-factor', 'discord'],
    block: 'you',
    summaryKey: 'settings.group.accountSummary',
    localSummaryKey: 'settings.group.accountSummaryLocal',
  },
  {
    slug: 'language',
    nameKey: 'settings.group.language',
    icon: Globe,
    cards: ['language', 'timezone'],
    block: 'you',
    summaryKey: 'settings.group.languageSummary',
  },
  {
    slug: 'privacy',
    nameKey: 'settings.group.privacy',
    icon: Lock,
    cards: ['consent', 'export', 'delete'],
    block: 'you',
    summaryKey: 'settings.group.privacySummary',
    localSummaryKey: 'settings.group.privacySummaryLocal',
  },
  {
    slug: 'reminders',
    nameKey: 'settings.group.reminders',
    icon: Bell,
    cards: ['push', 'phone-reminders', 'second-reminder', 'timer'],
    block: 'ember',
    summaryKey: 'settings.group.remindersSummary',
    localSummaryKey: 'settings.group.remindersSummaryLocal',
    localWebSummaryKey: 'settings.group.remindersSummaryLocalWeb',
  },
  {
    slug: 'habits',
    nameKey: 'settings.group.habits',
    icon: Sprout,
    cards: ['streaks', 'grace', 'habit-notes', 'routines'],
    block: 'ember',
    summaryKey: 'settings.group.habitsSummary',
  },
  {
    slug: 'journal',
    nameKey: 'settings.group.journal',
    icon: BookHeart,
    cards: ['journal', 'moods', 'helps', 'moments'],
    block: 'ember',
    summaryKey: 'settings.group.journalSummary',
    localSummaryKey: 'settings.group.journalSummaryLocal',
  },
  {
    slug: 'appearance',
    nameKey: 'settings.group.appearance',
    icon: Palette,
    cards: ['appearance'],
    block: 'ember',
    summaryKey: 'settings.group.appearanceSummary',
  },
  {
    slug: 'people',
    nameKey: 'fireside.title',
    icon: Users,
    cards: ['fireside'],
    block: 'ember',
    summaryKey: 'settings.group.peopleSummary',
  },
  {
    slug: 'support',
    nameKey: 'settings.group.support',
    icon: MessageSquare,
    cards: ['feedback', 'diagnostics', 'app-updates', 'whats-new', 'about'],
    block: 'support',
    summaryKey: 'settings.group.supportSummary',
  },
  {
    slug: 'help',
    nameKey: 'settings.group.help',
    icon: LifeBuoy,
    cards: ['help'],
    block: null,
    summaryKey: 'settings.group.helpSummary',
  },
];

/** Older group slugs and the group each now opens. */
export const settingsSlugAliases: Record<string, string> = { feedback: 'support' };

const legacyCardIds: Record<string, string> = { 'dm-reminders': 'push' };

/** The current id of a settings card: an older id (`dm-reminders`) gives the card that took its place. */
export function settingsCardId(cardId: string): string {
  return legacyCardIds[cardId] ?? cardId;
}

/** Addresses inside a card, and the card that holds them. */
const innerAddresses: Record<string, string> = {
  'this-device': 'appearance',
  calm: 'appearance',
  gentle: 'appearance',
};

/** The group holding the settings card with this id (or an older id of it, or an address inside it), if any. */
export function settingsGroupOf(cardId: string): SettingsGroup | undefined {
  const id = settingsCardId(cardId);
  const card = innerAddresses[id] ?? id;
  return settingsGroups.find((group) => group.cards.includes(card));
}
