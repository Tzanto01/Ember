using System.Globalization;
using Ember.Core.Models;

namespace Ember.Core.Rules;

/// <summary>
/// One habit on the home screen widget: its <c>State</c> (<c>done</c>, <c>partial</c>, <c>open</c>, or <c>rest</c> on a low
/// day), the day's check-in <c>Count</c> and the habit's <c>TimesPerDay</c>.
/// </summary>
public sealed record WidgetRow(int Id, string Name, string State, int Count, int TimesPerDay);

/// <summary>
/// One day on the widget: its first <see cref="WidgetDays.Rows"/> <c>Habits</c>, how many <c>More</c> follow, and <c>Done</c> of
/// <c>Total</c> over all of them.
/// </summary>
public sealed record WidgetDay(DateOnly Date, IReadOnlyList<WidgetRow> Habits, int More, int Done, int Total);

/// <summary>
/// The widget's day by the Today rules: the For today section of <c>web/src/lib/today-plan.ts</c> (<c>planToday</c>,
/// <c>widgetDay</c>) and the rows of <c>web/src/lib/native/widget.ts</c> (<c>widgetSnapshot</c>), checked by the
/// <c>widgetDays</c> cases of <c>docs/contracts/fixtures/rules-v1.json</c>.
/// </summary>
public static class WidgetDays
{
    /// <summary>Habits a widget day lists before <see cref="WidgetDay.More"/> (the most an account can have).</summary>
    public const int Rows = 25;

    private static readonly CompareInfo Names = CultureInfo.InvariantCulture.CompareInfo;

    private const CompareOptions BaseLetters =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

    private sealed record Item(string Key, int Id, string Name, int? Minute, IReadOnlyList<Habit> Habits);

    /// <summary>
    /// The widget day for the local <paramref name="date"/>, given the user's local <paramref name="today"/>: For today's build
    /// habits in the account's <paramref name="order"/> (<see cref="User.TodayOrder"/>, null for automatic), routines flattened in
    /// member order, done ones kept, let-go habits and Not for today (paused, a rest day of a weekdays habit, before the start on a
    /// day other than its own today, a weekly target met before <paramref name="date"/>) left out, and none before its start. Rows
    /// carry the day state on <paramref name="today"/> and are open with count 0 on any other date. Needs the habits' logs of
    /// today's week loaded and <paramref name="routines"/> in the user's order with their member habits.
    /// </summary>
    public static WidgetDay On(IEnumerable<Habit> habits, IEnumerable<Routine> routines, IReadOnlyList<string>? order,
        DateOnly date, DateOnly today, TimeZoneInfo tz, IReadOnlySet<DateOnly> lowDays)
    {
        var build = habits.Where(h => h.Kind == HabitKind.Build).ToList();
        var byId = build.ToDictionary(h => h.Id);
        var placed = new HashSet<int>();
        var items = new List<Item>();
        foreach (var routine in routines)
        {
            var members = new List<Habit>();
            foreach (var id in routine.Habits.OrderBy(h => h.RoutinePosition).Select(h => h.Id))
            {
                if (!byId.TryGetValue(id, out var habit) || !placed.Add(id)) continue;
                if (IsForDay(habit, date, today, tz)) members.Add(habit);
            }
            if (members.Count > 0)
                items.Add(new Item($"r:{routine.Id}", routine.Id, routine.Name, Minute(routine.ReminderTime), members));
        }
        foreach (var habit in build)
        {
            if (placed.Contains(habit.Id) || !IsForDay(habit, date, today, tz)) continue;
            items.Add(new Item($"h:{habit.Id}", habit.Id, habit.Name, Minute(habit.ReminderTime), [habit]));
        }

        var shown = Ordered(items, order)
            .SelectMany(item => item.Habits)
            .Where(habit => date >= HabitRules.StartDate(habit, tz))
            .Select(habit => Row(habit, date, today, tz, lowDays))
            .ToList();
        return new WidgetDay(date, shown.Take(Rows).ToList(), Math.Max(0, shown.Count - Rows),
            shown.Count(row => row.State == "done"), shown.Count);
    }

    private static WidgetRow Row(Habit habit, DateOnly date, DateOnly today, TimeZoneInfo tz, IReadOnlySet<DateOnly> lowDays)
    {
        if (date != today) return new WidgetRow(habit.Id, habit.Name, "open", 0, habit.TimesPerDay);
        var day = HabitRules.DayOn(habit, today, today, tz, lowDays);
        var state = day.State == DayState.Done || day.Count >= habit.TimesPerDay ? "done"
            : day.State == DayState.LowDay ? "rest"
            : day.Count > 0 ? "partial"
            : "open";
        return new WidgetRow(habit.Id, habit.Name, state, day.Count, habit.TimesPerDay);
    }

    private static bool IsForDay(Habit habit, DateOnly date, DateOnly today, TimeZoneInfo tz)
    {
        if (date < HabitRules.StartDate(habit, tz)) return date == today;
        if (HabitRules.IsPausedOn(habit, date)) return false;
        if (habit.Days is not null && !HabitRules.IsScheduledOn(habit, date)) return false;
        if (habit.FrequencyType != FrequencyType.Weekly) return true;
        var (done, target) = HabitRules.WeeklyProgress(habit, today);
        var doneBefore = date == today ? done - (TodayCount(habit, today) > 0 ? 1 : 0)
            : HabitRules.WeekStart(date) == HabitRules.WeekStart(today) ? done
            : 0;
        return doneBefore < target;
    }

    private static int TodayCount(Habit habit, DateOnly today)
        => habit.Logs.FirstOrDefault(l => l.Date == today)?.Count ?? 0;

    private static int? Minute(TimeOnly? time) => time is { } t ? t.Hour * 60 + t.Minute : null;

    private static List<Item> Ordered(List<Item> items, IReadOnlyList<string>? saved)
    {
        var sorted = items.Order(Comparer<Item>.Create(Automatic)).ToList();
        if (saved is null) return sorted;
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < saved.Count; i++) rank[saved[i]] = i;
        var placed = sorted.Where(item => rank.ContainsKey(item.Key)).OrderBy(item => rank[item.Key]).ToList();
        foreach (var item in sorted)
        {
            if (rank.ContainsKey(item.Key)) continue;
            var later = item.Minute is not { } minute ? -1 : placed.FindIndex(p => p.Minute > minute);
            placed.Insert(later == -1 ? placed.Count : later, item);
        }
        return placed;
    }

    private static int Automatic(Item a, Item b)
    {
        if (a.Minute != b.Minute)
        {
            if (a.Minute is null) return 1;
            if (b.Minute is null) return -1;
            return a.Minute < b.Minute ? -1 : 1;
        }
        var byName = Names.Compare(a.Name, b.Name, BaseLetters);
        if (byName != 0) return byName;
        return a.Id != b.Id ? a.Id.CompareTo(b.Id) : string.CompareOrdinal(a.Key, b.Key);
    }
}
