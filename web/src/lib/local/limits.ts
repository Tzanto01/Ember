/** Core limits the local backend enforces like the API. */
export const limits = {
  habits: 25,
  routines: 10,
  activeMoods: 20,
  habitName: 100,
  routineName: 50,
  moodLabel: 24,
  noteText: 2000,
  habitNoteText: 280,
  todayOrder: 500,
  timesPerDay: 10,
  weeklyTarget: 6,
  rangeDays: 366,
} as const;
