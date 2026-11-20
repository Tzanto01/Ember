using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using FluentAssertions;

namespace Ember.Tests;

public class StreakRulesTests
{
    private static readonly DateOnly Today = HabitFactory.Today;

    public sealed record ConsecutiveCase(string Name, Func<Habit> Habit, DateOnly Today, int GraceDays, int Streak, int? GraceUsed = null)
    {
        public override string ToString() => Name;
    }

    public sealed record BestCase(string Name, Func<Habit> Habit, DateOnly Today, int GraceDays, int Best, IReadOnlySet<DateOnly>? LowDays = null)
    {
        public override string ToString() => Name;
    }

    private static Habit DoneEveryDayExcept(int days, params int[] gapsDaysAgo)
        => HabitFactory.Make(Enumerable.Range(0, days).Where(d => !gapsDaysAgo.Contains(d)).Select(d => (d, true)).ToArray());

    private static Habit DoneOnDays(IEnumerable<int> daysAgo)
        => HabitFactory.Make(daysAgo.Select(d => (d, true)).ToArray());

    private static Habit PausedRange(int pausedFromDaysAgo, int pausedUntilDaysAgo, params int[] doneDaysAgo)
    {
        var habit = DoneOnDays(doneDaysAgo);
        habit.PausedFrom = Today.AddDays(-pausedFromDaysAgo);
        habit.PausedUntil = Today.AddDays(-pausedUntilDaysAgo);
        return habit;
    }

    public static TheoryData<ConsecutiveCase> ConsecutiveCases() => new()
    {
        new("A habit without logs has no streak", () => HabitFactory.Make(), Today, 0, 0, 0),
        new("Five completed days in a row count five", () => HabitFactory.Make((0, true), (1, true), (2, true), (3, true), (4, true)), Today, 0, 5, 0),
        new("Today not checked in yet does not count as a missed day", () => HabitFactory.Make((1, true), (2, true)), Today, 0, 2, 0),
        new("Yesterday not logged yet is pending, not missed", () => HabitFactory.Make((2, true), (3, true)), Today, 0, 2, 0),
        new("Yesterday logged as not done stays open today", () => HabitFactory.Make((1, false), (2, true), (3, true)), Today, 0, 2),
        new("Yesterday logged as not done breaks the streak once it closes", () => HabitFactory.Make((1, false), (2, true), (3, true)), Today.AddDays(1), 0, 0),
        new("A gap the day before yesterday is closed and breaks the streak", () => HabitFactory.Make((3, true), (4, true)), Today, 0, 0),
        new("One closed missed day is forgiven with one grace day", () => HabitFactory.Make((0, true), (1, true), (3, true), (4, true)), Today, 1, 4, 1),
        new("The second missed day breaks the streak when only one grace day exists", () => HabitFactory.Make((0, true), (1, true), (4, true), (5, true)), Today, 1, 2, 1),
        new("Two missed days are forgiven with two grace days", () => HabitFactory.Make((0, true), (1, true), (4, true), (5, true)), Today, 2, 4, 2),
        new("A single closed missed day breaks the streak without grace days", () => HabitFactory.Make((0, true), (1, true), (3, true), (4, true)), Today, 0, 2),
        new("No grace is used when the streak has no misses", () => HabitFactory.Make((0, true), (1, true), (2, true)), Today, 2, 3, 0),
        new("Paused days are skipped without consuming grace", () => HabitFactory.Make(pausedUntilDaysAgo: 1, FrequencyType.Daily, weeklyTarget: null, (0, true), (2, true), (3, true)), Today, 0, 1, 0),
        new("Grace days apply per rolling week so gaps more than a week apart are both forgiven", () => DoneEveryDayExcept(16, 3, 11), Today, 1, 14, 2),
        new("No week may hold more forgiven gaps than allowed (one grace day)", () => DoneEveryDayExcept(16, 3, 8), Today, 1, 7, 1),
        new("No week may hold more forgiven gaps than allowed (two grace days)", () => DoneEveryDayExcept(16, 3, 8), Today, 2, 14, 2),
        new("A pause excludes only the paused range", () => PausedRange(5, 3, 0, 1, 2, 6, 7, 8), Today, 0, 6),
        new("A gap just before the paused range still counts", () => PausedRange(5, 3, 0, 1, 2, 7, 8), Today, 0, 3),
    };

    public static TheoryData<BestCase> BestCases() => new()
    {
        new("A habit without logs has best streak zero", () => HabitFactory.Make(), Today, 0, 0),
        new("A single completed day is a best streak of one", () => HabitFactory.Make((0, true)), Today, 0, 1),
        new("Five completed days in a row are a best streak of five", () => HabitFactory.Make((0, true), (1, true), (2, true), (3, true), (4, true)), Today, 0, 5),
        new("The longest run wins after a break", () => HabitFactory.Make((0, true), (1, true), (2, true), (4, true), (5, true)), Today, 0, 3),
        new("Skipped logs do not count towards a streak", () => HabitFactory.Make((0, true), (1, false), (2, true), (3, true)), Today.AddDays(7), 0, 2),
        new("Several equal runs give that run length", () => HabitFactory.Make((0, true), (1, true), (3, true), (4, true), (6, true), (7, true)), Today, 0, 2),
        new("Best streak without grace stops at the gap", () => DoneOnDays(Enumerable.Range(20, 11).Where(d => d != 25)), Today, 0, 5),
        new("Best streak bridges the gap with one grace day", () => DoneOnDays(Enumerable.Range(20, 11).Where(d => d != 25)), Today, 1, 10),
        new("Best streak is never below the current streak", () => DoneEveryDayExcept(16, 3, 11), Today, 1, 14),
        new("Best streak without low days, pause or grace stops at each gap", SwitchoverHabit, SwitchoverToday, 0, 5),
        new("Best streak honors low days, the pause and the switchover date", SwitchoverHabit, SwitchoverToday, 0, 8, SwitchoverLowDays),
    };

    private static DateOnly SwitchoverToday => TimeZones.SwitchoverDate.AddDays(30);

    private static IReadOnlySet<DateOnly> SwitchoverLowDays
        => new HashSet<DateOnly> { TimeZones.SwitchoverDate.AddDays(-4), TimeZones.SwitchoverDate.AddDays(-2) };

    private static Habit SwitchoverHabit()
    {
        var s = TimeZones.SwitchoverDate;
        var habit = new Habit { Name = "Test", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        foreach (var d in new[] { -6, -5, -3, -1, 1, 2, 4, 5 })
            habit.Logs.Add(new HabitLog { Date = s.AddDays(d), Completed = true });
        habit.PausedFrom = s.AddDays(3);
        habit.PausedUntil = s.AddDays(3);
        return habit;
    }

    [Theory]
    [MemberData(nameof(ConsecutiveCases))]
    public void ConsecutiveStreak_FollowsTheRules(ConsecutiveCase c)
    {
        var (streak, graceUsed) = HabitRules.ConsecutiveStreak(c.Habit(), c.Today, c.GraceDays, null);

        streak.Should().Be(c.Streak);
        if (c.GraceUsed is { } expectedGrace)
            graceUsed.Should().Be(expectedGrace);
    }

    [Theory]
    [MemberData(nameof(BestCases))]
    public void BestStreak_FollowsTheRules(BestCase c)
    {
        HabitRules.BestStreak(c.Habit(), c.Today, c.GraceDays, c.LowDays).Should().Be(c.Best);
    }
}
