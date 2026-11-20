import type { Habit, LetGoHabit, Routine, WeeklyStats } from './models';

/** Query key of Today's data ({@link TodayData}); its first part also marks Today as a day screen. */
export const todayKey = ['today'] as const;

/** What {@link todayKey} holds. */
export interface TodayData {
  /** The build habits; every count on Today reads these only. */
  habits: Habit[];
  letGo: LetGoHabit[];
  /** Null when the low-day lookup failed; the low-day action is hidden then. */
  lowDayToday: boolean | null;
  weekly: WeeklyStats | null;
  /** Null when routines didn't load; habits then show ungrouped. */
  routines: Routine[] | null;
}

/** Query key prefix of every History read (habits, month days, the stats window, slips). */
export const historyKey = ['history'] as const;

/** Query key prefix of every Year read. */
export const yearKey = ['year'] as const;

/** Query key of one habit as habit edit loads it. */
export const habitQueryKey = (id: number) => ['habit', id] as const;

/** Query key of the routine list, shared by Today, Routines, the routine form and habit edit. */
export const routinesKey = ['routines'] as const;

/** Query key of the habits the routine pages offer. */
export const routineHabitsKey = [...routinesKey, 'habits'] as const;

/** Query key prefix of every journal read, so writes elsewhere can refresh them. */
export const journalKey = ['journal'] as const;

/** Whether the account has any saved day note (the private-journal tip); a journal save sets it. */
export const anyNoteKey = [...journalKey, 'any'] as const;
