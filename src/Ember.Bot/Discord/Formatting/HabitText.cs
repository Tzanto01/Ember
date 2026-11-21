using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Rules;

namespace Ember.Bot.Discord.Formatting;

/// <summary>The habit text shared by the bot's commands, buttons and jobs.</summary>
internal static class HabitText
{
    /// <summary>The "Reminder:" line with the reminder times as set, in the user's Ember timezone (not the viewer's device timezone).</summary>
    internal static string FormatReminder(Texts t, IReadOnlyList<TimeOnly> localTimes)
        => localTimes.Count switch
        {
            0 => t.Get("habit.reminder.none"),
            1 => t.Format("habit.reminder.single", FormatTimes(localTimes)),
            _ => t.Format("habit.reminder.several", FormatTimes(localTimes)),
        };

    /// <summary>The times as <c>08:00, 13:00, 20:00</c>.</summary>
    internal static string FormatTimes(IEnumerable<TimeOnly> times)
        => string.Join(", ", times.Select(t => t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>"Streak: **1** day" or "Streak: **n** days".</summary>
    internal static string StreakText(Texts t, int streak) => t.Plural("habit.streak", streak);

    internal static string FrequencyLabel(Texts t, Habit h)
    {
        if (h.Kind == HabitKind.LetGo) return t.Get("habit.letgo.label");
        var times = h.TimesPerDay > 1 ? " · " + t.Format("habit.freq.per-day", h.TimesPerDay) : "";
        return h.FrequencyType switch
        {
            FrequencyType.Weekly => t.Format("habit.freq.weekly", h.WeeklyTarget ?? HabitRules.DefaultWeeklyTarget),
            FrequencyType.Weekdays => string.Join(" ", ScheduleDayNames.ToNames(h.Days.GetValueOrDefault()).Select(n => DayName(t, n))) + times,
            _ => t.Get("habit.freq.daily") + times,
        };
    }

    /// <summary>The short name of a <see cref="ScheduleDayNames"/> day (<c>mon</c> → "Mon" / "Mo").</summary>
    internal static string DayName(Texts t, string name) => t.Get("weekday." + name);

    /// <summary>The habit's status on <paramref name="day"/>: ✅ done, 🔸 2/3 partly done, 💤 rest day (not scheduled), ⬜ otherwise.</summary>
    internal static string DayMark(Habit h, DateOnly day)
    {
        var log = h.Logs.FirstOrDefault(l => l.Date == day);
        return log?.Completed == true ? "✅"
            : log is { Count: > 0 } ? $"🔸 {log.Count}/{h.TimesPerDay}"
            : !HabitRules.IsScheduledOn(h, day) ? "💤"
            : "⬜";
    }

    /// <summary>"{n} days free" of a let-go habit.</summary>
    internal static string DaysFreeText(Texts t, int daysFree) => t.Plural("habit.letgo.days-free", daysFree);

    /// <summary>The gentle line after "I slipped": the longest stretch so far, or a new start while there was none.</summary>
    internal static string SlipNotedText(Texts t, int longestStretch)
        => longestStretch == 0 ? t.Get("habit.letgo.noted.first") : t.Plural("habit.letgo.noted", longestStretch);

    /// <summary>The reply wherever Discord would create or edit a let-go habit: that happens on the web or in the app.</summary>
    internal static string LetGoWebOnlyText(Texts t) => t.Get("habit.letgo.web-only");

    /// <summary>A let-go day's soft mark: 🌿 free, 🍂 a slip noted, ⏸️ paused, ▫️ before the start.</summary>
    internal static string LetGoMark(LetGoDayState state) => state switch
    {
        LetGoDayState.Free => "🌿",
        LetGoDayState.Slipped => "🍂",
        LetGoDayState.Paused => "⏸️",
        _ => "▫️",
    };

    /// <summary>
    /// The reply after a check-in that added one: <paramref name="doneText"/> once the day is done (with the count for
    /// multi-count habits), else the count so far, for today or for <paramref name="dayLabel"/>.
    /// </summary>
    internal static string CheckInText(Texts t, Habit? habit, DateOnly day, string doneText, string? dayLabel = null)
    {
        if (habit is null || habit.TimesPerDay == 1) return doneText;
        var log = habit.Logs.FirstOrDefault(l => l.Date == day);
        var count = $"{log?.Count ?? 0}/{habit.TimesPerDay}";
        return log?.Completed == true
            ? $"{doneText} ({count})"
            : dayLabel is null ? t.Format("habit.checkin.count.today", count) : t.Format("habit.checkin.count.day", count, dayLabel);
    }

    /// <summary>The <c>/habit list</c> progress line of <paramref name="h"/>; without <paramref name="showStreaks"/> it has no streak.</summary>
    internal static string ListProgress(Texts t, Habit h, DateOnly today, int grace, IReadOnlySet<DateOnly> lowDays, TimeZoneInfo tz, bool showStreaks)
    {
        if (h.Kind == HabitKind.LetGo) return DaysFreeText(t, LetGoRules.StatsOn(h, today, tz).DaysFree);
        if (h.FrequencyType == FrequencyType.Weekly)
        {
            var (done, target) = HabitRules.WeeklyProgress(h, today);
            return t.Format("habit.progress.weekly", done, target, HabitRules.FlexibleStreak(h, today, 28, lowDays: null, TimeZoneInfo.Utc));
        }

        var last7 = t.Format("habit.progress.last7", HabitRules.FlexibleStreak(h, today, 7, lowDays, tz));
        if (!showStreaks) return last7;
        var (streak, graceUsed) = HabitRules.ConsecutiveStreak(h, today, grace, lowDays);
        var graceStr = graceUsed > 0 ? " " + t.Plural("habit.progress.grace", graceUsed) : "";
        return $"{StreakText(t, streak)}{graceStr} · {last7}";
    }
}
