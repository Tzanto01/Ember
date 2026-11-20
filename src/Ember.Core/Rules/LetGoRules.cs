using Ember.Core.Models;

namespace Ember.Core.Rules;

/// <summary>What one local day means for a <see cref="HabitKind.LetGo"/> habit (see <see cref="LetGoRules.DayOn"/>).</summary>
public enum LetGoDayState
{
    /// <summary>Before the habit's local start day.</summary>
    BeforeStart,

    /// <summary>No slip that day and not paused.</summary>
    Free,

    /// <summary>At least one slip that day, paused or not.</summary>
    Slipped,

    /// <summary>Inside the habit's paused range without a slip: neither free nor slipped.</summary>
    Paused,
}

/// <summary>
/// One local day of a let-go habit: its <see cref="LetGoDayState"/>, that day's slip count (0 without slips) and whether a
/// <see cref="MadeItDay"/> marks it (only ever true on a <see cref="LetGoDayState.Free"/> day).
/// </summary>
public sealed record LetGoDay(DateOnly Date, LetGoDayState State, int Slips, bool MadeIt);

/// <summary>A let-go habit's numbers as the clients show them (see <see cref="LetGoRules.StatsOn"/>).</summary>
public sealed record LetGoStats(int DaysFree, int LongestStretch, int FreeDaysThisMonth, int SlipDaysLast30);

/// <summary>Let-go habit rules: slips instead of check-ins, days free instead of streaks.</summary>
public static class LetGoRules
{
    /// <summary>
    /// The let-go habit's state on the local <paramref name="date"/>. Precedence: before start (<see cref="HabitRules.StartDate"/> in
    /// <paramref name="tz"/>), slipped (a slip that day, even on a paused day), paused (<see cref="HabitRules.IsPausedOn"/>), free.
    /// <c>MadeIt</c> is true only on a free day with a <see cref="MadeItDay"/>; a mark on any other day is kept but reads false.
    /// Needs the habit's slip and made-it day of that date loaded, if there are any.
    /// </summary>
    public static LetGoDay DayOn(Habit habit, DateOnly date, TimeZoneInfo tz)
    {
        var slips = habit.Slips.FirstOrDefault(s => s.Date == date)?.Count ?? 0;
        var state = date < HabitRules.StartDate(habit, tz) ? LetGoDayState.BeforeStart
            : slips > 0 ? LetGoDayState.Slipped
            : HabitRules.IsPausedOn(habit, date) ? LetGoDayState.Paused
            : LetGoDayState.Free;
        var madeIt = state == LetGoDayState.Free && habit.MadeItDays.Any(m => m.Date == date);
        return new LetGoDay(date, state, slips, madeIt);
    }

    /// <summary>
    /// The let-go habit's stats on the user's local <paramref name="today"/>, by the day states of <see cref="DayOn"/> from its start
    /// through today: <c>DaysFree</c> counts the free days after the last slip (or from the start), today included while it is free;
    /// paused days are skipped (neither counted nor breaking the run). <c>LongestStretch</c> is the longest such run ever, the current one
    /// included. <c>FreeDaysThisMonth</c> counts the free days of today's calendar month, <c>SlipDaysLast30</c> the slipped days in the 30
    /// days ending today. Needs all of the habit's slips loaded.
    /// </summary>
    public static LetGoStats StatsOn(Habit habit, DateOnly today, TimeZoneInfo tz)
    {
        var slipped = habit.Slips.Where(s => s.Count > 0).Select(s => s.Date).ToHashSet();
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var windowStart = today.AddDays(-29);
        int run = 0, longest = 0, month = 0, last30 = 0;
        for (var date = HabitRules.StartDate(habit, tz); date <= today; date = date.AddDays(1))
        {
            if (slipped.Contains(date))
            {
                run = 0;
                if (date >= windowStart) last30++;
            }
            else if (!HabitRules.IsPausedOn(habit, date))
            {
                longest = Math.Max(longest, ++run);
                if (date >= monthStart) month++;
            }
        }
        return new LetGoStats(run, longest, month, last30);
    }

    /// <summary>Throws when a let-go habit wouldn't be daily and once a day, without weekly target or days, with at most one reminder time.</summary>
    internal static void ThrowIfInvalid(HabitKind kind, FrequencyType frequency, int timesPerDay, int? weeklyTarget, ScheduleDays? days,
        int reminderTimes)
    {
        if (kind == HabitKind.LetGo && (frequency != FrequencyType.Daily || timesPerDay != 1 || weeklyTarget is not null
            || days is not null || reminderTimes > 1))
            throw new ArgumentException("A let-go habit is daily, once a day, with at most one reminder time.");
    }
}
