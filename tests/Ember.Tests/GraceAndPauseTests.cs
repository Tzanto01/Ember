using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

public class GraceAndPauseTests
{
    [Fact]
    public void IsPausedOn_CoversExactlyThePausedRange()
    {
        var habit = HabitFactory.Make();
        habit.PausedFrom = HabitFactory.Today.AddDays(-5);
        habit.PausedUntil = HabitFactory.Today.AddDays(-3);

        HabitRules.IsPausedOn(habit, HabitFactory.Today.AddDays(-6)).Should().BeFalse();
        HabitRules.IsPausedOn(habit, HabitFactory.Today.AddDays(-5)).Should().BeTrue();
        HabitRules.IsPausedOn(habit, HabitFactory.Today.AddDays(-3)).Should().BeTrue();
        HabitRules.IsPausedOn(habit, HabitFactory.Today.AddDays(-2)).Should().BeFalse();
    }
}

[Collection("Integration")]
public class PauseServiceTests
{
    private const ulong UserId = 970;

    [Fact]
    public async Task Pausing_StartsTheRangeToday_AndResumingKeepsThePastPausedDaysProtected()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero));
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        var habitId = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await sp.GetRequiredService<ConsentService>().GiveHealthDataConsentAsync(await TestUsers.ResolveOrCreateAsync(sp, UserId));
            return (await service.AddHabitAsync(await TestUsers.UserIdAsync(sp, UserId), "Stretch", null)).Habit!.Id;
        });

        time.Advance(TimeSpan.FromDays(5));
        var paused = await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().PauseHabitAsync(await TestUsers.UserIdAsync(sp, UserId), habitId, new DateOnly(2027, 1, 21)));
        paused!.PausedFrom.Should().Be(new DateOnly(2027, 1, 15));

        time.Advance(TimeSpan.FromDays(3));
        var resumed = await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().PauseHabitAsync(await TestUsers.UserIdAsync(sp, UserId), habitId, null));

        resumed!.PausedFrom.Should().Be(new DateOnly(2027, 1, 15));
        resumed.PausedUntil.Should().Be(new DateOnly(2027, 1, 17));
        HabitRules.IsPausedOn(resumed, new DateOnly(2027, 1, 18)).Should().BeFalse();
    }

    [Fact]
    public async Task PausingAndResumingOnTheSameDay_LeavesNoPausedRange()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero));
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        var resumed = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await sp.GetRequiredService<ConsentService>().GiveHealthDataConsentAsync(await TestUsers.ResolveOrCreateAsync(sp, UserId));
            var habit = (await service.AddHabitAsync(await TestUsers.UserIdAsync(sp, UserId), "Stretch", null)).Habit!;
            await service.PauseHabitAsync(await TestUsers.UserIdAsync(sp, UserId), habit.Id, new DateOnly(2027, 1, 10));
            return await service.PauseHabitAsync(await TestUsers.UserIdAsync(sp, UserId), habit.Id, null);
        });

        resumed!.PausedFrom.Should().BeNull();
        resumed.PausedUntil.Should().BeNull();
    }

    private static async Task<(IntegrationTestHost Host, FakeTimeProvider Time, int HabitId)> HostWithHabitAsync(DateTimeOffset now)
    {
        var time = new FakeTimeProvider(now);
        var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        var habitId = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await sp.GetRequiredService<ConsentService>().GiveHealthDataConsentAsync(await TestUsers.ResolveOrCreateAsync(sp, UserId));
            return (await service.AddHabitAsync(await TestUsers.UserIdAsync(sp, UserId), "Stretch", null)).Habit!.Id;
        });
        return (host, time, habitId);
    }

    private static Task<Habit?> PauseAsync(IntegrationTestHost host, int habitId, DateOnly? until)
        => host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().PauseHabitAsync(await TestUsers.UserIdAsync(sp, UserId), habitId, until));

    [Fact]
    public async Task PausingAgainWhilePaused_KeepsTheOriginalStart_AndMovesTheEnd()
    {
        var (host, time, habitId) = await HostWithHabitAsync(new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero));
        await using var _ = host;
        await PauseAsync(host, habitId, new DateOnly(2027, 1, 12));
        time.Advance(TimeSpan.FromDays(1));

        var extended = await PauseAsync(host, habitId, new DateOnly(2027, 1, 20));

        (extended!.PausedFrom, extended.PausedUntil).Should().Be((new DateOnly(2027, 1, 10), new DateOnly(2027, 1, 20)));
    }

    [Fact]
    public async Task PausingAfterAnEarlierPauseEnded_StartsANewRangeToday()
    {
        var (host, time, habitId) = await HostWithHabitAsync(new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero));
        await using var _ = host;
        await PauseAsync(host, habitId, new DateOnly(2027, 1, 12));
        time.Advance(TimeSpan.FromDays(5));

        var again = await PauseAsync(host, habitId, new DateOnly(2027, 1, 18));

        (again!.PausedFrom, again.PausedUntil).Should().Be((new DateOnly(2027, 1, 15), new DateOnly(2027, 1, 18)));
    }

    [Fact]
    public async Task ResumingAfterThePauseAlreadyEnded_KeepsTheRecordedRange()
    {
        var (host, time, habitId) = await HostWithHabitAsync(new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero));
        await using var _ = host;
        await PauseAsync(host, habitId, new DateOnly(2027, 1, 12));
        time.Advance(TimeSpan.FromDays(5));

        var resumed = await PauseAsync(host, habitId, null);

        (resumed!.PausedFrom, resumed.PausedUntil).Should().Be((new DateOnly(2027, 1, 10), new DateOnly(2027, 1, 12)));
    }

    [Fact]
    public async Task ResumingAHabitThatWasNeverPaused_ChangesNothing()
    {
        var (host, _, habitId) = await HostWithHabitAsync(new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero));
        await using var _ = host;

        var resumed = await PauseAsync(host, habitId, null);

        (resumed!.PausedFrom, resumed.PausedUntil).Should().Be(((DateOnly?)null, (DateOnly?)null));
    }

    [Fact]
    public async Task PausingAnotherUsersHabit_ReturnsNull()
    {
        var (host, _, habitId) = await HostWithHabitAsync(new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero));
        await using var _ = host;

        var paused = await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().PauseHabitAsync(await TestUsers.UserIdAsync(sp, UserId + 1), habitId, new DateOnly(2027, 1, 12)));

        paused.Should().BeNull();
    }

    [Fact]
    public async Task PausedDays_AreProtectedInTheStreak_AndResumingKeepsThemProtected()
    {
        var (host, time, habitId) = await HostWithHabitAsync(new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero));
        await using var _ = host;
        time.Advance(TimeSpan.FromDays(1));
        await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().CheckInAsync(await TestUsers.UserIdAsync(sp, UserId), habitId, new DateOnly(2027, 1, 11), true));
        time.Advance(TimeSpan.FromDays(1));
        await PauseAsync(host, habitId, new DateOnly(2027, 1, 14));
        time.Advance(TimeSpan.FromDays(4));
        await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().CheckInAsync(await TestUsers.UserIdAsync(sp, UserId), habitId, new DateOnly(2027, 1, 16), true));
        var today = new DateOnly(2027, 1, 16);

        async Task<(int Streak, int GraceUsed)> StreakAsync() => await host.WithScopeAsync(async sp =>
        {
            var habit = (await sp.GetRequiredService<HabitService>().GetHabitAsync(await TestUsers.UserIdAsync(sp, UserId), habitId))!;
            return HabitRules.ConsecutiveStreak(habit, today, graceDays: 0, null);
        });

        (await StreakAsync()).Should().Be((2, 0));
        await PauseAsync(host, habitId, null);
        (await StreakAsync()).Should().Be((2, 0));
    }
}
