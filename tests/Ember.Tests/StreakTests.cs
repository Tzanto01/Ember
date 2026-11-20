using Ember.Core.Models;
using Ember.Core.Rules;
using FluentAssertions;

namespace Ember.Tests;

public class StreakTests
{
    // ── FlexibleStreak ────────────────────────────────────────────────────────

    [Fact]
    public void FlexibleStreak_NoLogs_ReturnsZero()
    {
        var habit = HabitFactory.Make();
        HabitRules.FlexibleStreak(habit, HabitFactory.Today, 7, null, TimeZoneInfo.Utc).Should().Be(0);
    }

    [Fact]
    public void FlexibleStreak_AllCompletedLastSevenDays_ReturnsSeven()
    {
        var habit = HabitFactory.Make(
            (0, true), (1, true), (2, true), (3, true), (4, true), (5, true), (6, true));
        HabitRules.FlexibleStreak(habit, HabitFactory.Today, 7, null, TimeZoneInfo.Utc).Should().Be(7);
    }

    [Fact]
    public void FlexibleStreak_ThreeOutOfSevenCompleted_ReturnsThree()
    {
        var habit = HabitFactory.Make(
            (0, true), (1, false), (2, true), (3, false), (4, true), (5, false), (6, false));
        HabitRules.FlexibleStreak(habit, HabitFactory.Today, 7, null, TimeZoneInfo.Utc).Should().Be(3);
    }

    [Fact]
    public void FlexibleStreak_IgnoresLogsOlderThanWindow()
    {
        var habit = HabitFactory.Make(
            (0, true), (8, true), (9, true)); // days 8 & 9 are outside the 7-day window
        HabitRules.FlexibleStreak(habit, HabitFactory.Today, 7, null, TimeZoneInfo.Utc).Should().Be(1);
    }

    [Fact]
    public void FlexibleStreak_OnlySkippedLogs_ReturnsZero()
    {
        var habit = HabitFactory.Make((0, false), (1, false), (2, false));
        HabitRules.FlexibleStreak(habit, HabitFactory.Today, 7, null, TimeZoneInfo.Utc).Should().Be(0);
    }

    [Fact]
    public void FlexibleStreak_RespectsCustomWindow()
    {
        var habit = HabitFactory.Make(
            (0, true), (1, true), (2, true),
            (8, true), (9, true)); // 3 in last 7 days, 5 in last 30 days
        HabitRules.FlexibleStreak(habit, HabitFactory.Today, 7, null, TimeZoneInfo.Utc).Should().Be(3);
        HabitRules.FlexibleStreak(habit, HabitFactory.Today, 30, null, TimeZoneInfo.Utc).Should().Be(5);
    }
}
