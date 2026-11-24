namespace Ember.Core.Rules;

/// <summary>
/// The habit and routine limits that the API, the bot and the database all enforce. The web app's local mode mirrors them in
/// <c>web/src/lib/local/limits.ts</c>.
/// </summary>
public static class Limits
{
    /// <summary>Most habits a single user can track.</summary>
    public const int HabitsPerUser = 25;

    /// <summary>Most routines a single user can have.</summary>
    public const int RoutinesPerUser = 10;

    /// <summary>Most check-ins a daily or weekdays habit can need per day (<see cref="Models.Habit.TimesPerDay"/>).</summary>
    public const int TimesPerDay = 10;

    /// <summary>Most check-ins per week a weekly habit can target (<see cref="Models.Habit.WeeklyTarget"/>), from 1.</summary>
    public const int WeeklyTarget = 6;

    /// <summary>Longest timer a build habit can have, in minutes (<see cref="Models.Habit.TimerMinutes"/>), from 1.</summary>
    public const int TimerMinutes = 240;

    /// <summary>Most characters in a habit name, after trimming.</summary>
    public const int HabitNameLength = 100;

    /// <summary>Most characters in a routine name, after trimming.</summary>
    public const int RoutineNameLength = 50;
}
