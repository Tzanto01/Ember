using Ember.Bot.Modules;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

/// <summary>A skip on the user's local today ends that habit's reminders for the day; a count of 0 or a cleared day does not.</summary>
[Collection("Integration")]
public class ReminderSkipTests
{
    private const ulong DiscordUser = 10;
    private static readonly DateTimeOffset NineBerlin = ReminderDispatchTests.Now;
    private static readonly DateTimeOffset EveningBerlin = new(2027, 1, 15, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = ReminderDispatchTests.Today;

    private static async Task<(IntegrationTestHost Host, int HabitId)> SeedAsync(FakeTimeProvider time, int timesPerDay = 1, params TimeOnly[] reminderTimes)
    {
        var host = await ReminderDispatchTests.CreateHostAsync(time, null, null);
        var habitId = await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.Users.Add(new User { Id = TestUsers.Id(DiscordUser), DiscordReminders = true, Logins = TestUsers.Discord(DiscordUser), Timezone = "Europe/Berlin" });
            await db.SaveChangesAsync();
            await sp.GetRequiredService<ConsentService>().GiveHealthDataConsentAsync(await TestUsers.ResolveOrCreateAsync(sp, DiscordUser));
            return (await sp.GetRequiredService<HabitService>().AddHabitAsync(await TestUsers.UserIdAsync(sp, DiscordUser), "Stretch", null,
                timesPerDay: timesPerDay, reminderTimes: reminderTimes)).Habit!.Id;
        });
        return (host, habitId);
    }

    private static Task WithHabitsAsync(IntegrationTestHost host, Func<HabitService, Guid, Task> action)
        => host.WithScopeAsync(async sp => await action(sp.GetRequiredService<HabitService>(), await TestUsers.UserIdAsync(sp, DiscordUser)));

    [Fact]
    public async Task Skipping_today_silences_that_evenings_reminder_and_the_next_day_reminds_again()
    {
        var time = new FakeTimeProvider(NineBerlin);
        var (host, habitId) = await SeedAsync(time, reminderTimes: new TimeOnly(20, 0));
        await using var _ = host;

        await WithHabitsAsync(host, (habits, userId) => habits.CheckInAsync(userId, habitId, Today, completed: false));
        time.SetUtcNow(EveningBerlin);
        await ReminderDispatchTests.RunJobAsync(host);

        host.Discord.Messages.Should().BeEmpty();

        time.SetUtcNow(EveningBerlin.AddDays(1));
        await ReminderDispatchTests.RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.Text.Should().Contain("Stretch");
    }

    [Fact]
    public async Task Skipping_from_the_reminder_dm_after_a_partial_check_in_silences_the_later_reminder_times()
    {
        var time = new FakeTimeProvider(NineBerlin.AddMinutes(-30));
        var (host, habitId) = await SeedAsync(time, 2, new TimeOnly(8, 0), new TimeOnly(20, 0));
        await using var _ = host;

        await WithHabitsAsync(host, (habits, userId) => habits.AddCheckInAsync(userId, habitId, Today));
        await host.WithScopeAsync(async sp => await CheckInButtonModule.LogReminderDayAsync(sp.GetRequiredService<HabitService>(),
            sp.GetRequiredService<UserSettingsService>(), await TestUsers.UserIdAsync(sp, DiscordUser), habitId,
            time.GetUtcNow().ToUnixTimeSeconds().ToString(), completed: false, guildId: null));
        time.SetUtcNow(EveningBerlin);
        await ReminderDispatchTests.RunJobAsync(host);

        host.Discord.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Counting_the_day_down_to_zero_is_not_a_skip_and_the_evening_reminder_is_still_sent()
    {
        var time = new FakeTimeProvider(NineBerlin);
        var (host, habitId) = await SeedAsync(time, reminderTimes: new TimeOnly(20, 0));
        await using var _ = host;

        await WithHabitsAsync(host, (habits, userId) => habits.SetCountAsync(userId, habitId, Today, 0));
        time.SetUtcNow(EveningBerlin);
        await ReminderDispatchTests.RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.Text.Should().Contain("Stretch");
    }

    [Fact]
    public async Task Clearing_a_skipped_day_brings_the_evening_reminder_back()
    {
        var time = new FakeTimeProvider(NineBerlin);
        var (host, habitId) = await SeedAsync(time, reminderTimes: new TimeOnly(20, 0));
        await using var _ = host;

        await WithHabitsAsync(host, (habits, userId) => habits.CheckInAsync(userId, habitId, Today, completed: false));
        time.SetUtcNow(NineBerlin.AddHours(1));
        await WithHabitsAsync(host, (habits, userId) => habits.ClearDayAsync(userId, habitId, Today));
        time.SetUtcNow(EveningBerlin);
        await ReminderDispatchTests.RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.Text.Should().Contain("Stretch");
    }
}
