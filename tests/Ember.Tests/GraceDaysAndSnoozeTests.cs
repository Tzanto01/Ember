using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

[Collection("Integration")]
public class GraceDaysAndSnoozeTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = TestUsers.Id(1);

    private static async Task SeedUserAsync(IntegrationTestHost host, params Habit[] habits)
        => await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User { Id = UserId, Logins = TestUsers.Discord(1), Timezone = "UTC" };
            foreach (var habit in habits) user.Habits.Add(habit);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });

    [Fact]
    public async Task GraceDays_OfAnUnknownUser_DefaultToOne_AndSettingThemCreatesNoUser()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));

        await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<UserSettingsService>();
            await service.SetGraceDaysAsync(UserId, 2);
            (await service.GetGraceDaysAsync(UserId)).Should().Be(1);
        });

        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.CountAsync())).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-3)]
    [InlineData(99)]
    public async Task SetGraceDays_StoresTheValueAsGiven_WithoutRangeChecks_CurrentBehavior(int graceDays)
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        await SeedUserAsync(host);

        await host.WithScopeAsync(sp => sp.GetRequiredService<UserSettingsService>().SetGraceDaysAsync(UserId, graceDays));

        (await host.WithScopeAsync(sp => sp.GetRequiredService<UserSettingsService>().GetGraceDaysAsync(UserId))).Should().Be(graceDays);
    }

    [Fact]
    public async Task Snooze_OfALetGoHabitWithAReminder_IsRefusedAndLeavesTheScheduleAlone()
    {
        var due = Now.UtcDateTime.AddMinutes(-1);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        await SeedUserAsync(host, new Habit
        {
            Name = "Smoking",
            Kind = HabitKind.LetGo,
            CreatedAt = Now.UtcDateTime.AddDays(-30),
            ReminderTime = new TimeOnly(9, 0),
            NextReminderUtc = due,
        });
        var habitId = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Habits.Select(h => h.Id).SingleAsync());

        var snoozed = await host.WithScopeAsync(sp => sp.GetRequiredService<ReminderService>().SnoozeReminderAsync(UserId, habitId, Now));

        snoozed.Should().BeFalse();
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Habits.AsNoTracking().SingleAsync())).NextReminderUtc.Should().Be(due);
    }
}
