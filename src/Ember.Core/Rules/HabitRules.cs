namespace Ember.Core.Rules;

/// <summary>
/// Habit rules shared by Core, the API and the bot. The web app's local mode mirrors them in <c>web/src/lib/local/rules.ts</c>,
/// checked by <c>docs/contracts/fixtures/rules-v1.json</c>.
/// </summary>
public static class HabitRules
{
    /// <summary>Check-ins per week a weekly habit needs when <see cref="Models.Habit.WeeklyTarget"/> is not set.</summary>
    public const int DefaultWeeklyTarget = 3;
}
