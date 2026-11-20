namespace Ember.Bot.Discord.CustomIds;

/// <summary>The days-and-times menu of a habit: <c>freq:{action}:{habitId}[:{weeklyTarget}]</c>.</summary>
internal static class FrequencyIds
{
    public const string WeeklyPattern = "freq:weekly:*:*";
    public const string MenuPattern = "freq:menu:*";
    public const string WeekdaysPattern = "freq:weekdays:*";
    public const string DaysPattern = "freq:days:*";
    public const string TimesPattern = "freq:times:*";
    public const string TimesPerDayPattern = "freq:tpd:*";

    public static string Weekly(int habitId, int target) => $"freq:weekly:{habitId}:{target}";

    public static string Menu(int habitId) => $"freq:menu:{habitId}";

    public static string Weekdays(int habitId) => $"freq:weekdays:{habitId}";

    /// <summary>The weekday select menu.</summary>
    public static string Days(int habitId) => $"freq:days:{habitId}";

    public static string Times(int habitId) => $"freq:times:{habitId}";

    /// <summary>The times-per-day select menu.</summary>
    public static string TimesPerDay(int habitId) => $"freq:tpd:{habitId}";
}
