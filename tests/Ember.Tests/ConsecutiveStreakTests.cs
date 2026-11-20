using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using FluentAssertions;

namespace Ember.Tests;

public class ConsecutiveStreakTests
{
    // ── Local-day switchover ───────────────────────────────────────────────────

    private static Habit HabitWithLogsOn(params DateOnly[] dates)
    {
        var habit = new Habit { Name = "Test", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        foreach (var d in dates)
            habit.Logs.Add(new HabitLog { Date = d, Completed = true });
        return habit;
    }

    [Fact]
    public void GapOnSwitchoverDate_DoesNotBreakStreak()
    {
        var s = TimeZones.SwitchoverDate;
        var habit = HabitWithLogsOn(s.AddDays(1), s.AddDays(-1), s.AddDays(-2));

        var (streak, graceUsed) = HabitRules.ConsecutiveStreak(habit, s.AddDays(1), graceDays: 0, null);

        streak.Should().Be(3);
        graceUsed.Should().Be(0);
    }

    [Fact]
    public void GapOnAnotherDateAroundSwitchover_StillBreaksStreak()
    {
        var s = TimeZones.SwitchoverDate;
        var habit = HabitWithLogsOn(s.AddDays(3), s.AddDays(2), s, s.AddDays(-1));

        var (streak, _) = HabitRules.ConsecutiveStreak(habit, s.AddDays(3), graceDays: 0, null);

        streak.Should().Be(2);
    }
}
