using Ember.Core.Models;

namespace Ember.Core.Rules;

/// <summary>
/// Habit rules shared by Core, the API and the bot. The web app's local mode mirrors them in <c>web/src/lib/local/rules.ts</c>,
/// checked by <c>docs/contracts/fixtures/rules-v1.json</c>.
/// </summary>
public static class HabitRules
{
    /// <summary>Check-ins per week a weekly habit needs when <see cref="Models.Habit.WeeklyTarget"/> is not set.</summary>
    public const int DefaultWeeklyTarget = 3;

    /// <summary>
    /// The habit's <see cref="DayState"/> and raw log on the user's local <paramref name="date"/>, given the user's local
    /// <paramref name="today"/>, timezone and low days. Needs the habit's log of that date loaded, if there is one.
    /// State precedence: before start, paused, low day, done, partial (some check-ins, not done), offday (not
    /// <see cref="IsScheduledOn"/>), pending (open), not done.
    /// </summary>
    public static HabitDay DayOn(Habit habit, DateOnly date, DateOnly today, TimeZoneInfo tz, IReadOnlySet<DateOnly> lowDays)
    {
        var log = habit.Logs.FirstOrDefault(l => l.Date == date);
        var partial = log is { Completed: false, Count: > 0 };
        var state = date < StartDate(habit, tz) ? DayState.BeforeStart
            : IsPausedOn(habit, date) ? DayState.Paused
            : lowDays.Contains(date) ? DayState.LowDay
            : log?.Completed == true ? DayState.Done
            : partial ? DayState.Partial
            : !IsScheduledOn(habit, date) ? DayState.Offday
            : IsOpen(date, today) ? DayState.Pending
            : DayState.NotDone;
        return new HabitDay(date, state, partial ? null : log?.Completed, log?.Count ?? 0);
    }

    /// <summary>
    /// One local day across <paramref name="habits"/> (with that day's logs loaded): <c>Scheduled</c> counts the habits expected
    /// that day (from their <see cref="StartDate"/>, not paused, <see cref="IsScheduledOn"/>; low days don't change it, and a weekly
    /// habit counts only on a day it has a check-in, since any day can serve its target), <c>Done</c> and <c>Partial</c> the habits
    /// whose log reached each state (offdays included),
    /// and <c>ShowedUp</c> is true when any habit has a check-in that day. <see cref="HabitKind.LetGo"/> habits are left out.
    /// </summary>
    public static DayStats DayStatsOn(IEnumerable<Habit> habits, DateOnly date, TimeZoneInfo tz)
    {
        int scheduled = 0, done = 0, partial = 0;
        var showedUp = false;
        foreach (var habit in habits.Where(h => h.Kind == HabitKind.Build))
        {
            var log = habit.Logs.FirstOrDefault(l => l.Date == date);
            if (date >= StartDate(habit, tz) && !IsPausedOn(habit, date) && IsScheduledOn(habit, date)
                && (habit.FrequencyType != FrequencyType.Weekly || log is { Completed: true } or { Count: > 0 }))
                scheduled++;
            if (log is null) continue;
            if (log.Completed) done++;
            else if (log.Count > 0) partial++;
            showedUp |= log.Completed || log.Count > 0;
        }
        return new DayStats(date, scheduled, done, partial, showedUp);
    }

    /// <summary>
    /// Returns how many of the last <paramref name="days"/> days were kept, using a rolling window ending on the
    /// user's local <paramref name="today"/>. A day is kept when it was completed (offdays included) or is one of the user's
    /// <paramref name="lowDays"/> (null counts completed days only); low days only count from the habit's local start day
    /// (<see cref="StartDate"/> in <paramref name="tz"/>), outside its paused range and on scheduled days (<see cref="IsScheduledOn"/>).
    /// </summary>
    public static int FlexibleStreak(Habit habit, DateOnly today, int days, IReadOnlySet<DateOnly>? lowDays, TimeZoneInfo tz)
    {
        var cutoff = today.AddDays(-(days - 1));
        var kept = habit.Logs.Where(l => l.Completed && l.Date >= cutoff).Select(l => l.Date).ToHashSet();
        if (lowDays is not null)
        {
            var start = StartDate(habit, tz);
            var from = start > cutoff ? start : cutoff;
            kept.UnionWith(lowDays.Where(d => d >= from && d <= today && !IsPausedOn(habit, d) && IsScheduledOn(habit, d)));
        }
        return kept.Count;
    }

    /// <summary>
    /// How many local days stay open: today and yesterday. An open day that isn't done yet is pending,
    /// never missed; a day only counts as a gap once it has closed.
    /// </summary>
    public const int OpenDays = 2;

    /// <summary>Whether <paramref name="date"/> is still open (see <see cref="OpenDays"/>) on the user's local <paramref name="today"/>.</summary>
    public static bool IsOpen(DateOnly date, DateOnly today)
        => date <= today && date > today.AddDays(-OpenDays);

    /// <summary>
    /// Returns the current streak of completed days ending at the user's local <paramref name="today"/>,
    /// with up to <paramref name="graceDays"/> forgiven gaps, and how many grace days that streak used.
    /// Open days (<see cref="OpenDays"/>: today and yesterday) that aren't done yet are pending: they never
    /// break the streak and use no grace. Paused days, the user's <paramref name="lowDays"/> and an unlogged
    /// <see cref="TimeZones.SwitchoverDate"/> are protected: they neither break nor extend the streak. So is an offday
    /// (not <see cref="IsScheduledOn"/>) that isn't done, so grace only ever covers scheduled days; a done offday extends it.
    /// A partial day counts as not done. Streaks are computed from the logs, never stored, so backfilling an older day heals the streak retroactively.
    /// </summary>
    public static (int streak, int graceUsed) ConsecutiveStreak(Habit habit, DateOnly today, int graceDays,
        IReadOnlySet<DateOnly>? lowDays)
    {
        var completed = CompletedDays(habit);
        return completed.Count == 0 ? (0, 0) : StreakEndingAt(habit, today, today, graceDays, lowDays, completed);
    }

    /// <summary>Returns whether the habit is paused on the given date: inside [<see cref="Habit.PausedFrom"/>, <see cref="Habit.PausedUntil"/>].</summary>
    public static bool IsPausedOn(Habit habit, DateOnly date)
        => habit.PausedUntil.HasValue && date <= habit.PausedUntil.Value
            && (habit.PausedFrom is null || date >= habit.PausedFrom.Value);

    /// <summary>Returns whether the habit is scheduled on the given date: always, unless it is a <see cref="FrequencyType.Weekdays"/> habit and the date's weekday isn't in <see cref="Habit.Days"/>.</summary>
    public static bool IsScheduledOn(Habit habit, DateOnly date)
        => habit.FrequencyType != FrequencyType.Weekdays
            || (habit.Days.GetValueOrDefault() & (ScheduleDays)(1 << (((int)date.DayOfWeek + 6) % 7))) != 0;

    private static bool IsCoveredOn(Habit habit, DateOnly date, HashSet<DateOnly> completed, IReadOnlySet<DateOnly>? lowDays)
        => IsPausedOn(habit, date) || lowDays?.Contains(date) == true
            || (!completed.Contains(date) && (date == TimeZones.SwitchoverDate || !IsScheduledOn(habit, date)));

    /// <summary>
    /// Returns the longest streak the habit ever had, by the same rules as <see cref="ConsecutiveStreak"/>:
    /// grace days per rolling 7-day window, open days, low days, pauses and the switchover date. Never below the current streak.
    /// </summary>
    public static int BestStreak(Habit habit, DateOnly today, int graceDays, IReadOnlySet<DateOnly>? lowDays)
    {
        var completed = CompletedDays(habit);
        return completed
            .Where(d => d == today || (d < today && !completed.Contains(d.AddDays(1))))
            .Select(end => StreakEndingAt(habit, end, today, graceDays, lowDays, completed).streak)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static HashSet<DateOnly> CompletedDays(Habit habit)
        => habit.Logs.Where(l => l.Completed).Select(l => l.Date).ToHashSet();

    /// <summary>
    /// Walks back from <paramref name="end"/> to the earliest completed day. A closed gap is forgiven only while no
    /// 7-day window would hold more than <paramref name="graceDays"/> forgiven gaps; the first gap that can't be forgiven ends the streak.
    /// </summary>
    private static (int streak, int graceUsed) StreakEndingAt(Habit habit, DateOnly end, DateOnly today, int graceDays,
        IReadOnlySet<DateOnly>? lowDays, HashSet<DateOnly> completed)
    {
        var floor = completed.Min();
        var streak = 0;
        var forgiven = new List<DateOnly>();

        for (var day = end; day >= floor; day = day.AddDays(-1))
        {
            if (IsCoveredOn(habit, day, completed, lowDays) || (IsOpen(day, today) && !completed.Contains(day)))
                continue;

            if (completed.Contains(day))
                streak++;
            else if (forgiven.Count(g => g.DayNumber - day.DayNumber < 7) < graceDays)
                forgiven.Add(day);
            else
                break;
        }

        return (streak, forgiven.Count);
    }

    /// <summary>
    /// For weekly habits: returns (checkedInThisWeek, target) where the week
    /// is Monday–Sunday containing the user's local <paramref name="today"/>.
    /// </summary>
    public static (int done, int target) WeeklyProgress(Habit habit, DateOnly today)
    {
        var monday = WeekStart(today);
        var sunday = monday.AddDays(6);

        var done = habit.Logs.Count(l => l.Completed && l.Date >= monday && l.Date <= sunday);
        var target = habit.WeeklyTarget ?? DefaultWeeklyTarget;
        return (done, target);
    }

    /// <summary>
    /// True for a weekly habit whose <see cref="Habit.WeeklyTarget"/> (or <see cref="DefaultWeeklyTarget"/>) is reached by
    /// <paramref name="completedInWeek"/>, the completed logs of its Monday–Sunday week.
    /// </summary>
    public static bool IsWeekDone(Habit habit, int completedInWeek)
        => habit.FrequencyType == FrequencyType.Weekly && completedInWeek >= (habit.WeeklyTarget ?? DefaultWeeklyTarget);

    /// <summary>
    /// The habit's stats on the user's local <paramref name="today"/>: <see cref="FlexibleStreak"/> over the last 7 days, plus
    /// <see cref="WeeklyProgress"/> for weekly habits, or <see cref="ConsecutiveStreak"/> and <see cref="BestStreak"/> for the others.
    /// </summary>
    public static HabitStats StatsOn(Habit habit, DateOnly today, int graceDays, IReadOnlySet<DateOnly> lowDays, TimeZoneInfo tz)
    {
        var last7 = FlexibleStreak(habit, today, 7, lowDays, tz);
        if (habit.FrequencyType == FrequencyType.Weekly)
            return new HabitStats(null, null, null, last7, WeeklyProgress(habit, today));

        var (streak, graceUsed) = ConsecutiveStreak(habit, today, graceDays, lowDays);
        return new HabitStats(streak, graceUsed, BestStreak(habit, today, graceDays, lowDays), last7, null);
    }

    /// <summary>Returns the Monday of the Monday–Sunday week containing <paramref name="day"/>.</summary>
    public static DateOnly WeekStart(DateOnly day)
        => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>
    /// Whether <paramref name="date"/> lies strictly after the habit's <see cref="StartDate"/> in the user's <paramref name="tz"/>.
    /// The weekly summary and the missed-day reflection count a habit only from then: on its creation day the user had no full
    /// day to complete it. Logging and the day states use the inclusive <see cref="StartDate"/> instead.
    /// </summary>
    public static bool IsAfterStartDay(Habit habit, DateOnly date, TimeZoneInfo tz)
        => StartDate(habit, tz) < date;

    /// <summary>The habit's creation day in <paramref name="tz"/> (the user's current timezone): the first day that can be logged.</summary>
    public static DateOnly StartDate(Habit habit, TimeZoneInfo tz)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(habit.CreatedAt, DateTimeKind.Utc), tz));

    /// <summary>
    /// The reminder times sorted ascending without repeats, keeping the earliest <paramref name="max"/>
    /// (a habit's <see cref="Habit.TimesPerDay"/>).
    /// </summary>
    public static List<TimeOnly> NormalizeReminderTimes(IEnumerable<TimeOnly> times, int max)
        => times.Distinct().Order().Take(max).ToList();

    /// <summary>The habit's next reminder after <paramref name="nowUtc"/> among its <see cref="Habit.ReminderTimes"/> in <paramref name="tz"/>, or null without any.</summary>
    public static DateTime? NextReminderUtc(Habit habit, TimeZoneInfo tz, DateTime nowUtc)
        => TimeZones.NextOccurrenceUtc(habit.ReminderTimes, tz, nowUtc);

    /// <summary>The routine's next reminder after <paramref name="nowUtc"/> at its <see cref="Routine.ReminderTime"/> in <paramref name="tz"/>, or null without one.</summary>
    public static DateTime? NextReminderUtc(Routine routine, TimeZoneInfo tz, DateTime nowUtc)
        => routine.ReminderTime is { } time
            ? TimeZones.NextOccurrenceUtc(time, tz, nowUtc)
            : null;

    /// <summary>
    /// The routine members a reminder on the user's local <paramref name="today"/> is for: those whose day (<see cref="DayOn"/>)
    /// is pending or partial, in <see cref="Habit.RoutinePosition"/> order. Done, offday, paused, low and not-started days and let-go habits are left out.
    /// Needs each habit's log of <paramref name="today"/> loaded, if there is one.
    /// </summary>
    public static List<Habit> PendingRoutineHabits(IEnumerable<Habit> members, DateOnly today, TimeZoneInfo tz, IReadOnlySet<DateOnly> lowDays)
        => members
            .OrderBy(h => h.RoutinePosition)
            .Where(h => h.Kind == HabitKind.Build && DayOn(h, today, today, tz, lowDays).State is DayState.Pending or DayState.Partial)
            .ToList();

    /// <summary>
    /// Whether a routine reminder for the user's local <paramref name="date"/> has nothing left to remind of, given the user's local
    /// <paramref name="today"/>: the routine has build members and every one due that day is done. A member whose day
    /// (<see cref="DayOn"/>) is pending, partial or not done is still due; offday, paused, low and not-started days are not.
    /// On <paramref name="today"/> this is <see cref="PendingRoutineHabits"/> coming back empty. Needs each habit's log of
    /// <paramref name="date"/> loaded, if there is one.
    /// </summary>
    public static bool IsRoutineDone(IEnumerable<Habit> members, DateOnly date, DateOnly today, TimeZoneInfo tz, IReadOnlySet<DateOnly> lowDays)
        => members.Where(h => h.Kind == HabitKind.Build).ToList() is { Count: > 0 } build
            && build.All(h => DayOn(h, date, today, tz, lowDays).State is not (DayState.Pending or DayState.Partial or DayState.NotDone));

    /// <summary>
    /// Whether a daily or weekdays habit is active, scheduled and unpaused on <paramref name="date"/> but not logged as done yet
    /// (a partial day is still open). Never for a let-go habit. The missed-day reflection offers to log such a habit.
    /// </summary>
    public static bool IsOpenDaily(Habit habit, DateOnly date, TimeZoneInfo tz)
        => habit.Kind != HabitKind.LetGo
            && habit.FrequencyType != FrequencyType.Weekly
            && IsScheduledOn(habit, date)
            && IsAfterStartDay(habit, date, tz)
            && !IsPausedOn(habit, date)
            && !habit.Logs.Any(l => l.Date == date && l.Completed);

    /// <summary>
    /// For the missed-day reflection on the day after <paramref name="yesterday"/>: a weekly habit's progress (completed logs from its
    /// Monday through <paramref name="yesterday"/>, and its target) when it still needs check-ins, its week has one or two days left
    /// after <paramref name="yesterday"/>, it wasn't done <paramref name="yesterday"/>, which lies after its start day
    /// (<see cref="IsAfterStartDay"/>), and it isn't paused then. Null otherwise, and for habits that aren't weekly.
    /// </summary>
    public static (int Done, int Target)? WeeklyCatchUp(Habit habit, DateOnly yesterday, TimeZoneInfo tz)
    {
        if (habit.FrequencyType != FrequencyType.Weekly || !IsAfterStartDay(habit, yesterday, tz) || IsPausedOn(habit, yesterday))
            return null;

        var weekStart = WeekStart(yesterday);
        var daysLeft = weekStart.AddDays(6).DayNumber - yesterday.DayNumber;
        var done = habit.Logs.Count(l => l.Completed && l.Date >= weekStart && l.Date <= yesterday);
        var target = habit.WeeklyTarget ?? DefaultWeeklyTarget;
        return target - done > 0 && daysLeft is > 0 and <= 2 && !habit.Logs.Any(l => l.Date == yesterday && l.Completed)
            ? (done, target)
            : null;
    }
}
