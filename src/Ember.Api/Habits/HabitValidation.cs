using Ember.Api.Hosting;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;

namespace Ember.Api.Habits;

/// <summary>The field checks of a habit or routine write, shared by the habit, routine and import endpoints.</summary>
internal static class HabitValidation
{
    internal const string FrequencyMessage = "Must be daily, weekly or weekdays.";

    internal const string KindMessage = "Must be build or letGo.";

    internal static string? ValidName(string? name, Dictionary<string, string[]> errors, int maxLength = Limits.HabitNameLength)
    {
        var trimmed = name?.Trim();
        if (trimmed is { Length: >= 1 } && trimmed.Length <= maxLength) return trimmed;
        errors["name"] = [$"Must be 1 to {maxLength} characters."];
        return null;
    }

    internal static TimeOnly? ParseReminder(string? value, Dictionary<string, string[]> errors)
    {
        if (value is null) return null;
        if (Formats.TryParseTime(value, out var time)) return time;
        errors["reminderTime"] = ["Must be a time as HH:mm."];
        return null;
    }

    /// <summary>
    /// The reminder times in <paramref name="values"/> sorted ascending, or null when <paramref name="values"/> is null or invalid:
    /// each must be HH:mm, without repeats, and at most <paramref name="timesPerDay"/> of them. Empty clears the reminder.
    /// </summary>
    internal static List<TimeOnly>? ParseReminderTimes(string?[]? values, int timesPerDay, Dictionary<string, string[]> errors)
    {
        if (values is null) return null;
        var times = new List<TimeOnly>();
        foreach (var value in values)
        {
            if (value is null || !Formats.TryParseTime(value, out var time))
            {
                errors["reminderTimes"] = ["Each must be a time as HH:mm."];
                return null;
            }
            times.Add(time);
        }

        var max = Math.Min(timesPerDay, Limits.TimesPerDay);
        if (times.Distinct().Count() != times.Count) errors["reminderTimes"] = ["Each time can appear only once."];
        else if (times.Count > max) errors["reminderTimes"] = [$"At most {max}, the habit's timesPerDay."];
        else return times.Order().ToList();
        return null;
    }

    /// <summary>
    /// The distinct <paramref name="values"/>, or null when <paramref name="values"/> is null or invalid: each must be the id of one of
    /// the user's current reminder destinations.
    /// </summary>
    internal static async Task<List<string>?> ParseReminderExclusionsAsync(string?[]? values, ReminderService reminders, Guid userId,
        Dictionary<string, string[]> errors)
    {
        if (values is null) return null;
        var ids = await reminders.GetUserWithDestinationsAsync(userId) is { } user ? ReminderDestinations.Ids(user) : [];
        if (values.All(v => v is not null && ids.Contains(v))) return values.Select(v => v!).Distinct().ToList();
        errors["reminderExclusions"] = ["Each must be the id of one of your reminder destinations."];
        return null;
    }

    /// <summary>The habit kind of a request's <c>kind</c>: <c>build</c> (also when missing) or <c>letGo</c>; null when unknown.</summary>
    internal static HabitKind? ParseKind(string? value) => value switch
    {
        null or "build" => HabitKind.Build,
        "letGo" => HabitKind.LetGo,
        _ => null,
    };

    /// <summary>
    /// The let-go constraints: daily, <paramref name="timesPerDay"/> 1 when given, no <paramref name="weeklyTarget"/> or
    /// <paramref name="days"/>, at most one of <paramref name="reminderTimes"/>. Each violation replaces the field's error.
    /// </summary>
    internal static void ValidateLetGo(FrequencyType? frequency, int? timesPerDay, int? weeklyTarget, string?[]? days, int? reminderTimes,
        Dictionary<string, string[]> errors)
    {
        if (frequency is not null and not FrequencyType.Daily) errors["frequency"] = ["Let-go habits are daily."];
        if (timesPerDay is not null and not 1) errors["timesPerDay"] = ["Always 1 for let-go habits."];
        if (weeklyTarget is not null) errors["weeklyTarget"] = ["Not for let-go habits."];
        if (days is not null) errors["days"] = ["Not for let-go habits."];
        if (reminderTimes > 1) errors["reminderTimes"] = ["At most one for let-go habits."];
    }

    internal static FrequencyType? ParseFrequency(string? value) => value switch
    {
        "daily" => FrequencyType.Daily,
        "weekly" => FrequencyType.Weekly,
        "weekdays" => FrequencyType.Weekdays,
        _ => null,
    };

    /// <summary>
    /// The days for <paramref name="frequency"/>: the parsed <paramref name="values"/> (1–6 distinct names, weekdays habits only),
    /// else <paramref name="current"/> for a weekdays habit. Null for daily and weekly habits.
    /// </summary>
    internal static ScheduleDays? ValidateDays(string?[]? values, FrequencyType? frequency, ScheduleDays? current,
        Dictionary<string, string[]> errors)
    {
        if (frequency is null) return null;
        if (values is null)
        {
            if (frequency != FrequencyType.Weekdays) return null;
            if (current is not null and not ScheduleDays.None) return current;
            errors["days"] = ["Required for weekdays habits."];
            return null;
        }

        if (frequency != FrequencyType.Weekdays) errors["days"] = ["Only for weekdays habits."];
        else if (!ScheduleDayNames.TryParse(values, out var days)) errors["days"] = ["Each must be one of mon, tue, wed, thu, fri, sat, sun, without repeats."];
        else if (values.Length == 7) errors["days"] = ["All seven days: use daily instead."];
        else if (values.Length == 0) errors["days"] = ["Pick 1 to 6 days."];
        else return days;
        return null;
    }

    internal static int ValidateTimesPerDay(int timesPerDay, FrequencyType? frequency, Dictionary<string, string[]> errors)
    {
        if (frequency == FrequencyType.Weekly && timesPerDay != 1) errors["timesPerDay"] = ["Always 1 for weekly habits."];
        else if (timesPerDay is < 1 or > Limits.TimesPerDay) errors["timesPerDay"] = [$"Must be between 1 and {Limits.TimesPerDay}."];
        return timesPerDay;
    }

    internal static void ValidateWeeklyTarget(int? weeklyTarget, FrequencyType? frequency, Dictionary<string, string[]> errors)
    {
        if (weeklyTarget is null) return;
        if (frequency != FrequencyType.Weekly) errors["weeklyTarget"] = ["Only for weekly habits."];
        else if (weeklyTarget is < 1 or > Limits.WeeklyTarget) errors["weeklyTarget"] = [$"Must be between 1 and {Limits.WeeklyTarget}."];
    }
}
