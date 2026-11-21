namespace Ember.Core.Rules;

/// <summary>
/// What the database says about a habit on the user's local today, for <see cref="ReminderPolicy"/>: whether it is completed,
/// how many completed logs its Monday–Sunday week has, whether today is a stored low day and whether today is skipped
/// (<see cref="Models.HabitLog.Skipped"/>).
/// </summary>
public readonly record struct ReminderDayFacts(bool CompletedToday, int CompletedInWeek, bool LowDay, bool SkippedToday);
