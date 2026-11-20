import { matchPath } from 'react-router';
import type { en } from '../i18n';
import { firesideLimits } from '../fireside-limits';
import { limits } from '../local/limits';
import { graceOptions } from '../models';

type Key = keyof typeof en;
type GuideKey = Extract<Key, `guide.${string}`>;

/** One question of a screen's guide. */
export interface GuideEntry {
  /** Unique across all guides; also the key in `guide-fingerprints.json`. */
  id: string;
  questionKey: GuideKey;
  answerKey: GuideKey;
  /** Interpolated into the answer, taken from the code's own constants. */
  values?: Record<string, number>;
  /** "Show me": an element id, else a `data-guide` value, on the screen the guide explains. */
  target?: string;
  /** The 1 to 3 repository files (paths from the repository root) whose behavior the answer describes. */
  covers: string[];
}

/** The guide for one screen: "{name}, explained", a one-line lede and its questions. */
export interface Guide {
  nameKey: Key;
  ledeKey: GuideKey;
  entries: GuideEntry[];
}

const core = 'src/Ember.Core/Services/HabitService.cs';
const rules = 'src/Ember.Core/Rules/HabitRules.cs';
const letGoRules = 'src/Ember.Core/Rules/LetGoRules.cs';
const dispatch = 'src/Ember.Bot/Jobs/ReminderDispatchJob.cs';
const plan = 'web/src/lib/native/reminder-plan.ts';
const settingsPage = 'web/src/pages/settings.tsx';
const groups = 'web/src/components/settings/settings-groups.ts';
const appearanceCard = 'web/src/components/settings/appearance-card.tsx';
const deviceData = 'web/src/components/local/device-data-settings.tsx';
const journalSettings = 'web/src/components/settings/journal-settings.tsx';

/** The screens with a guide. */
export type GuideScreen =
  | 'today'
  | 'history'
  | 'habit'
  | 'routines'
  | 'settings'
  | 'reminders'
  | 'habitsDays'
  | 'appearance'
  | 'privacy'
  | 'fireside';

/** Every guide, by screen. */
export const guides: Record<GuideScreen, Guide> = {
  today: {
    nameKey: 'title.today',
    ledeKey: 'guide.today.lede',
    entries: [
      {
        id: 'today-check-in',
        questionKey: 'guide.today.checkIn.q',
        answerKey: 'guide.today.checkIn.a',
        target: 'check-in',
        covers: ['web/src/components/today/check-button.tsx', 'web/src/pages/today.tsx'],
      },
      {
        id: 'today-weekly',
        questionKey: 'guide.today.weekly.q',
        answerKey: 'guide.today.weekly.a',
        covers: [rules, 'web/src/components/today/habit-row.tsx'],
      },
      {
        id: 'today-low-day',
        questionKey: 'guide.today.lowDay.q',
        answerKey: 'guide.today.lowDay.a',
        target: 'mark-low-day',
        covers: [rules, dispatch, plan],
      },
      {
        id: 'today-reminders',
        questionKey: 'guide.today.reminders.q',
        answerKey: 'guide.today.reminders.a',
        covers: [dispatch, plan, rules],
      },
      {
        id: 'today-moment',
        questionKey: 'guide.today.moment.q',
        answerKey: 'guide.today.moment.a',
        target: 'moment',
        covers: ['web/src/pages/moment.tsx', 'web/src/components/moment/reflect.tsx'],
      },
    ],
  },
  history: {
    nameKey: 'title.history',
    ledeKey: 'guide.history.lede',
    entries: [
      {
        id: 'history-fill-in',
        questionKey: 'guide.history.fillIn.q',
        answerKey: 'guide.history.fillIn.a',
        target: 'calendar',
        covers: ['web/src/lib/calendar.ts', core],
      },
      {
        id: 'history-low-day',
        questionKey: 'guide.history.lowDay.q',
        answerKey: 'guide.history.lowDay.a',
        covers: ['web/src/components/history/day-panel.tsx', core],
      },
      {
        id: 'history-kept',
        questionKey: 'guide.history.kept.q',
        answerKey: 'guide.history.kept.a',
        target: 'stats',
        covers: ['web/src/components/history/kept-days.ts', 'web/src/components/history/stat-tiles.tsx'],
      },
      {
        id: 'history-streaks',
        questionKey: 'guide.history.streaks.q',
        answerKey: 'guide.history.streaks.a',
        covers: [rules, 'web/src/components/history/stat-tiles.tsx'],
      },
      {
        id: 'history-year',
        questionKey: 'guide.history.year.q',
        answerKey: 'guide.history.year.a',
        target: 'history-views',
        covers: ['web/src/pages/year.tsx', 'web/src/components/history/view-switch.tsx'],
      },
    ],
  },
  habit: {
    nameKey: 'guide.habit.name',
    ledeKey: 'guide.habit.lede',
    entries: [
      {
        id: 'habit-frequency',
        questionKey: 'guide.habit.frequency.q',
        answerKey: 'guide.habit.frequency.a',
        target: 'frequency',
        covers: ['web/src/components/habits/habit-fields.tsx', rules],
      },
      {
        id: 'habit-times-a-day',
        questionKey: 'guide.habit.timesADay.q',
        answerKey: 'guide.habit.timesADay.a',
        values: { max: limits.timesPerDay },
        covers: [
          'web/src/components/habits/habit-fields.tsx',
          'web/src/components/today/check-button.tsx',
          'web/src/lib/local/limits.ts',
        ],
      },
      {
        id: 'habit-limits',
        questionKey: 'guide.habit.limits.q',
        answerKey: 'guide.habit.limits.a',
        values: { habits: limits.habits, name: limits.habitName },
        covers: ['web/src/lib/local/limits.ts', 'web/src/components/habits/habit-fields.tsx', 'src/Ember.Core/Rules/Limits.cs'],
      },
      {
        id: 'habit-pause',
        questionKey: 'guide.habit.pause.q',
        answerKey: 'guide.habit.pause.a',
        target: 'pause',
        covers: ['web/src/components/habits/habit-edit-sections.tsx', rules, plan],
      },
      {
        id: 'habit-let-go',
        questionKey: 'guide.habit.letGo.q',
        answerKey: 'guide.habit.letGo.a',
        covers: [
          'web/src/components/letgo/letgo-section.tsx',
          'web/src/components/habits/habit-fields.tsx',
          letGoRules,
        ],
      },
    ],
  },
  routines: {
    nameKey: 'title.routines',
    ledeKey: 'guide.routines.lede',
    entries: [
      {
        id: 'routines-what',
        questionKey: 'guide.routines.what.q',
        answerKey: 'guide.routines.what.a',
        covers: ['web/src/pages/routines.tsx', 'web/src/pages/today.tsx'],
      },
      {
        id: 'routines-reminder',
        questionKey: 'guide.routines.reminder.q',
        answerKey: 'guide.routines.reminder.a',
        covers: [dispatch, plan],
      },
      {
        id: 'routines-one',
        questionKey: 'guide.routines.one.q',
        answerKey: 'guide.routines.one.a',
        covers: ['web/src/components/routines/routine-habits.tsx', 'src/Ember.Core/Services/HabitService.Routines.cs'],
      },
      {
        id: 'routines-limits',
        questionKey: 'guide.routines.limits.q',
        answerKey: 'guide.routines.limits.a',
        values: { routines: limits.routines, name: limits.routineName },
        covers: [
          'web/src/lib/local/limits.ts',
          'src/Ember.Core/Services/HabitService.Routines.cs',
          'src/Ember.Core/Data/EmberDbContext.cs',
        ],
      },
      {
        id: 'routines-order',
        questionKey: 'guide.routines.order.q',
        answerKey: 'guide.routines.order.a',
        covers: ['web/src/lib/today-plan.ts', 'web/src/pages/today.tsx', 'web/src/components/today/use-reorder.ts'],
      },
    ],
  },
  settings: {
    nameKey: 'title.settings',
    ledeKey: 'guide.settings.lede',
    entries: [
      {
        id: 'settings-streaks',
        questionKey: 'guide.settings.streaks.q',
        answerKey: 'guide.settings.streaks.a',
        covers: [groups, settingsPage, 'web/src/components/settings/streaks-card.tsx'],
      },
      {
        id: 'settings-looks',
        questionKey: 'guide.settings.looks.q',
        answerKey: 'guide.settings.looks.a',
        covers: [groups, appearanceCard],
      },
      {
        id: 'settings-help-right-now',
        questionKey: 'guide.settings.helpRightNow.q',
        answerKey: 'guide.settings.helpRightNow.a',
        covers: [groups, 'web/src/components/moment/crisis-lines.tsx'],
      },
      {
        id: 'settings-sync',
        questionKey: 'guide.settings.sync.q',
        answerKey: 'guide.settings.sync.a',
        covers: ['web/src/lib/local/mode.ts', deviceData],
      },
      {
        id: 'settings-down',
        questionKey: 'guide.settings.down.q',
        answerKey: 'guide.settings.down.a',
        covers: ['web/src/lib/notices.ts', 'web/src/lib/problem.ts', 'web/src/lib/offline-queue.ts'],
      },
    ],
  },
  reminders: {
    nameKey: 'settings.group.reminders',
    ledeKey: 'guide.reminders.lede',
    entries: [
      {
        id: 'reminders-when',
        questionKey: 'guide.reminders.when.q',
        answerKey: 'guide.reminders.when.a',
        covers: [dispatch, plan],
      },
      {
        id: 'reminders-where',
        questionKey: 'guide.reminders.where.q',
        answerKey: 'guide.reminders.where.a',
        target: 'push',
        covers: ['src/Ember.Core/Services/ReminderDestinations.cs', plan],
      },
      {
        id: 'reminders-tap',
        questionKey: 'guide.reminders.tap.q',
        answerKey: 'guide.reminders.tap.a',
        covers: [
          'web/src/lib/device-sync.ts',
          'web/src/lib/native/reminders.ts',
          'src/Ember.Bot/Services/WebPushReminderChannel.cs',
        ],
      },
      {
        id: 'reminders-second',
        questionKey: 'guide.reminders.second.q',
        answerKey: 'guide.reminders.second.a',
        target: 'second-reminder',
        covers: [dispatch, plan],
      },
    ],
  },
  habitsDays: {
    nameKey: 'settings.group.habits',
    ledeKey: 'guide.habitsDays.lede',
    entries: [
      {
        id: 'habits-days-streaks',
        questionKey: 'guide.habitsDays.streaks.q',
        answerKey: 'guide.habitsDays.streaks.a',
        target: 'streaks',
        covers: ['web/src/components/today/habit-row.tsx', 'web/src/components/history/stat-tiles.tsx', rules],
      },
      {
        id: 'habits-days-grace',
        questionKey: 'guide.habitsDays.grace.q',
        answerKey: 'guide.habitsDays.grace.a',
        values: { max: Math.max(...graceOptions) },
        target: 'grace',
        covers: [rules, 'web/src/lib/models.ts', 'web/src/components/settings/grace-card.tsx'],
      },
      {
        id: 'habits-days-kinds',
        questionKey: 'guide.habitsDays.kinds.q',
        answerKey: 'guide.habitsDays.kinds.a',
        covers: [rules],
      },
      {
        id: 'habits-days-timezone',
        questionKey: 'guide.habitsDays.timezone.q',
        answerKey: 'guide.habitsDays.timezone.a',
        target: 'timezone',
        covers: ['src/Ember.Core/Services/TimezoneHelper.cs', plan],
      },
      {
        id: 'habits-days-slip',
        questionKey: 'guide.habitsDays.slip.q',
        answerKey: 'guide.habitsDays.slip.a',
        covers: [
          'web/src/components/letgo/slip-sheet.tsx',
          'web/src/components/letgo/try-instead.tsx',
          letGoRules,
        ],
      },
    ],
  },
  appearance: {
    nameKey: 'settings.group.appearance',
    ledeKey: 'guide.appearance.lede',
    entries: [
      {
        id: 'appearance-themes',
        questionKey: 'guide.appearance.themes.q',
        answerKey: 'guide.appearance.themes.a',
        target: 'appearance',
        covers: ['web/src/lib/theme.ts', appearanceCard],
      },
      {
        id: 'appearance-light-dark',
        questionKey: 'guide.appearance.lightDark.q',
        answerKey: 'guide.appearance.lightDark.a',
        target: 'color-scheme',
        covers: ['web/src/components/settings/color-scheme-picker.tsx', 'web/src/lib/theme.ts'],
      },
      {
        id: 'appearance-calm',
        questionKey: 'guide.appearance.calm.q',
        answerKey: 'guide.appearance.calm.a',
        target: 'calm-mode',
        covers: ['web/src/calm.css', 'web/src/components/moment/breathing-player.tsx', appearanceCard],
      },
    ],
  },
  privacy: {
    nameKey: 'settings.group.privacy',
    ledeKey: 'guide.privacy.lede',
    entries: [
      {
        id: 'privacy-journal',
        questionKey: 'guide.privacy.journal.q',
        answerKey: 'guide.privacy.journal.a',
        target: 'journal',
        covers: [journalSettings, 'web/src/lib/journal/journal.ts'],
      },
      {
        id: 'privacy-passphrase',
        questionKey: 'guide.privacy.passphrase.q',
        answerKey: 'guide.privacy.passphrase.a',
        covers: [journalSettings],
      },
      {
        id: 'privacy-helps',
        questionKey: 'guide.privacy.helps.q',
        answerKey: 'guide.privacy.helps.a',
        target: 'helps',
        covers: [
          'web/src/components/settings/helps-settings.tsx',
          'src/Ember.Core/Services/JournalService.Helps.cs',
          'web/src/pages/moment.tsx',
        ],
      },
      {
        id: 'privacy-export',
        questionKey: 'guide.privacy.export.q',
        answerKey: 'guide.privacy.export.a',
        target: 'export',
        covers: ['web/src/components/settings/export-card.tsx', deviceData, 'src/Ember.Core/Services/ExportBuilder.cs'],
      },
      {
        id: 'privacy-delete',
        questionKey: 'guide.privacy.delete.q',
        answerKey: 'guide.privacy.delete.a',
        target: 'delete',
        covers: ['web/src/components/settings/delete-card.tsx', 'web/src/components/settings/consent-card.tsx', deviceData],
      },
    ],
  },
  fireside: {
    nameKey: 'fireside.title',
    ledeKey: 'guide.fireside.lede',
    entries: [
      {
        id: 'fireside-what',
        questionKey: 'guide.fireside.what.q',
        answerKey: 'guide.fireside.what.a',
        values: { friends: firesideLimits.friends },
        covers: [
          'web/src/pages/fireside.tsx',
          'web/src/components/fireside/fireside-settings.tsx',
          'web/src/lib/fireside-limits.ts',
        ],
      },
      {
        id: 'fireside-sees',
        questionKey: 'guide.fireside.sees.q',
        answerKey: 'guide.fireside.sees.a',
        covers: ['web/src/pages/fireside-friend.tsx', 'src/Ember.Core/Services/FiresideService.cs'],
      },
      {
        id: 'fireside-codes',
        questionKey: 'guide.fireside.codes.q',
        answerKey: 'guide.fireside.codes.a',
        values: { length: firesideLimits.codeLength, minutes: firesideLimits.codeMinutes },
        covers: ['web/src/components/fireside/add-friend.tsx', 'src/Ember.Core/Services/FiresideService.cs'],
      },
    ],
  },
};

/**
 * The app routes with a guide, in match order. A route not listed here, like `/history/year`, gets none. On wide
 * screens `/settings` opens its first group, so Account & security shows the Settings guide.
 */
export const guideRoutes: { path: string; screen: GuideScreen | null }[] = [
  { path: '/today', screen: 'today' },
  { path: '/history', screen: 'history' },
  { path: '/history/year', screen: null },
  { path: '/history/:habitId', screen: 'history' },
  { path: '/habits/new', screen: 'habit' },
  { path: '/habits/:id/edit', screen: 'habit' },
  { path: '/routines', screen: 'routines' },
  { path: '/settings', screen: 'settings' },
  { path: '/settings/account', screen: 'settings' },
  { path: '/settings/reminders', screen: 'reminders' },
  { path: '/settings/habits', screen: 'habitsDays' },
  { path: '/settings/appearance', screen: 'appearance' },
  { path: '/settings/privacy', screen: 'privacy' },
  { path: '/settings/people', screen: 'fireside' },
  { path: '/fireside', screen: 'fireside' },
  { path: '/fireside/:id', screen: 'fireside' },
];

/** The guide for the screen at `pathname`, or null when that screen has none. */
export function guideFor(pathname: string): Guide | null {
  const screen = guideRoutes.find((route) => matchPath(route.path, pathname))?.screen;
  return screen ? guides[screen] : null;
}
