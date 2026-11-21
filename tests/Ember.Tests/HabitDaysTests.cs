using Ember.Core.Data;
using Ember.Core.Rules;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

[Collection("Integration")]
public class HabitDaysTests
{
    private const ulong UserId = 990;

    [Fact]
    public async Task GetHabitDays_ReportsEachLocalDayOldestFirst()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero));
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        var habitId = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await sp.GetRequiredService<ConsentService>().GiveHealthDataConsentAsync(await TestUsers.ResolveOrCreateAsync(sp, UserId));
            return (await service.AddHabitAsync(await TestUsers.UserIdAsync(sp, UserId), "Stretch", null)).Habit!.Id;
        });
        time.SetUtcNow(new DateTimeOffset(2027, 1, 18, 12, 0, 0, TimeSpan.Zero));
        await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await service.CheckInAsync(await TestUsers.UserIdAsync(sp, UserId), habitId, new DateOnly(2027, 1, 10), completed: true);
            await service.CheckInAsync(await TestUsers.UserIdAsync(sp, UserId), habitId, new DateOnly(2027, 1, 11), completed: false);
            await service.SetLowDayAsync(await TestUsers.UserIdAsync(sp, UserId), new DateOnly(2027, 1, 13));
            await service.CheckInAsync(await TestUsers.UserIdAsync(sp, UserId), habitId, new DateOnly(2027, 1, 17), completed: false);
            await sp.GetRequiredService<EmberDbContext>().Habits.Where(h => h.Id == habitId).ExecuteUpdateAsync(s => s
                .SetProperty(h => h.PausedFrom, new DateOnly(2027, 1, 14))
                .SetProperty(h => h.PausedUntil, new DateOnly(2027, 1, 15)));
        });

        var days = await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().GetHabitDaysAsync(await TestUsers.UserIdAsync(sp, UserId), habitId, 10));

        days.Should().Equal(
            new HabitDay(new DateOnly(2027, 1, 9), DayState.BeforeStart, null, 0),
            new HabitDay(new DateOnly(2027, 1, 10), DayState.Done, true, 1),
            new HabitDay(new DateOnly(2027, 1, 11), DayState.NotDone, false, 0, Skipped: true),
            new HabitDay(new DateOnly(2027, 1, 12), DayState.NotDone, null, 0),
            new HabitDay(new DateOnly(2027, 1, 13), DayState.LowDay, null, 0),
            new HabitDay(new DateOnly(2027, 1, 14), DayState.Paused, null, 0),
            new HabitDay(new DateOnly(2027, 1, 15), DayState.Paused, null, 0),
            new HabitDay(new DateOnly(2027, 1, 16), DayState.NotDone, null, 0),
            new HabitDay(new DateOnly(2027, 1, 17), DayState.Pending, false, 0, Skipped: true),
            new HabitDay(new DateOnly(2027, 1, 18), DayState.Pending, null, 0));
    }

    [Fact]
    public async Task GetHabitDays_ForSomeoneElsesHabit_ReturnsNull()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(new DateTimeOffset(2027, 1, 10, 12, 0, 0, TimeSpan.Zero)));
        var habitId = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await sp.GetRequiredService<ConsentService>().GiveHealthDataConsentAsync(await TestUsers.ResolveOrCreateAsync(sp, UserId));
            return (await service.AddHabitAsync(await TestUsers.UserIdAsync(sp, UserId), "Stretch", null)).Habit!.Id;
        });

        (await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().GetHabitDaysAsync(await TestUsers.UserIdAsync(sp, UserId + 1), habitId, 14))).Should().BeNull();
    }
}
