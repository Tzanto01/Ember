using Discord;
using Ember.Bot.Jobs;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

[Collection("Integration")]
public class HealthDataDeletionTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private static async Task SeedWithdrawnUserAsync(IntegrationTestHost host, long id)
    {
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User
            {
                Id = TestUsers.Id(id),
                Logins = TestUsers.Discord(id),
                Timezone = "UTC",
                DiscordReminders = true,
                GraceDaysPerWeek = 2,
                HealthDataConsentAt = Now.UtcDateTime.AddDays(-60),
                ConsentPolicyVersion = HabitService.CurrentConsentPolicyVersion,
            };
            var habit = new Habit { Name = "Take meds", CreatedAt = Now.UtcDateTime.AddDays(-60) };
            habit.Logs.Add(new HabitLog { Date = new DateOnly(2027, 1, 10), Completed = true });
            habit.Slips.Add(new HabitSlip { Date = new DateOnly(2027, 1, 11), Count = 1, Reasons = ["stress"] });
            user.Habits.Add(habit);
            user.DayMarks.Add(new DayMark { Date = new DateOnly(2027, 1, 12) });
            user.DayNotes.Add(new DayNote { Date = new DateOnly(2027, 1, 12), Text = "Rough day" });
            user.Moods.Add(new Mood { Label = "Tired" });
            user.Helps = new UserHelps { Encrypted = "c3ludGhldGlj", Version = 1, UpdatedAt = Now.UtcDateTime };
            user.Routines.Add(new Routine { Name = "Morning" });
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });
        (await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().WithdrawHealthDataConsentAsync(TestUsers.Id(id))))
            .Should().BeTrue();
    }

    private static Task RunJobAsync(IntegrationTestHost host)
        => ActivatorUtilities.CreateInstance<HealthDataDeletionJob>(host.Host.Services).RunAsync();

    private static Task<(int Habits, int Logs, int Slips, int Marks, int Notes, int Moods, int Helps, int Routines, int GraceDays)> CountsAsync(
        IntegrationTestHost host, long id)
        => host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var userId = TestUsers.Id(id);
            return (await db.Habits.CountAsync(h => h.UserId == userId),
                await db.HabitLogs.CountAsync(l => l.Habit.UserId == userId),
                await db.HabitSlips.CountAsync(s => s.Habit.UserId == userId),
                await db.DayMarks.CountAsync(m => m.UserId == userId),
                await db.DayNotes.CountAsync(n => n.UserId == userId),
                await db.Moods.CountAsync(m => m.UserId == userId),
                await db.UserHelps.CountAsync(h => h.UserId == userId),
                await db.Routines.CountAsync(r => r.UserId == userId),
                await db.Users.Where(u => u.Id == userId).Select(u => u.GraceDaysPerWeek).SingleAsync());
        });

    private static IEnumerable<string?> ButtonIds(MessageComponent? components)
        => components!.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().Select(b => b.CustomId);

    [Fact]
    public async Task Withdrawn_user_is_reminded_once_at_27_days_and_their_health_data_is_deleted_at_30_days_keeping_account_and_routines()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await SeedWithdrawnUserAsync(host, 10);

        time.Advance(TimeSpan.FromDays(27) - TimeSpan.FromMinutes(1));
        await RunJobAsync(host);
        host.Discord.Messages.Should().BeEmpty();

        time.Advance(TimeSpan.FromMinutes(1));
        await RunJobAsync(host);
        await RunJobAsync(host);
        var dm = host.Discord.Messages.Should().ContainSingle().Subject;
        dm.UserId.Should().Be(10UL);
        dm.Text.Should().Contain("February 14, 2027").And.Contain("/export");
        ButtonIds(dm.Components).Should().Equal("consent:agree:10", "consent:decline:10");
        (await CountsAsync(host, 10)).Should().Be((1, 1, 1, 1, 1, 1, 1, 1, 2));

        time.Advance(TimeSpan.FromDays(3));
        await RunJobAsync(host);

        (await CountsAsync(host, 10)).Should().Be((0, 0, 0, 0, 0, 0, 0, 1, 2));
        host.Discord.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Deletion_waits_three_days_after_a_late_reminder()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await SeedWithdrawnUserAsync(host, 11);

        time.Advance(TimeSpan.FromDays(31));
        await RunJobAsync(host);
        host.Discord.Messages.Should().ContainSingle();
        (await CountsAsync(host, 11)).Habits.Should().Be(1, "the reminder only just went out");

        time.Advance(TimeSpan.FromDays(3));
        await RunJobAsync(host);
        (await CountsAsync(host, 11)).Habits.Should().Be(0);
    }

    [Fact]
    public async Task Agreeing_again_before_30_days_cancels_the_deletion()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await SeedWithdrawnUserAsync(host, 12);
        time.Advance(TimeSpan.FromDays(27));
        await RunJobAsync(host);

        await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().GiveHealthDataConsentAsync(TestUsers.Id(12)));
        time.Advance(TimeSpan.FromDays(10));
        await RunJobAsync(host);

        (await CountsAsync(host, 12)).Should().Be((1, 1, 1, 1, 1, 1, 1, 1, 2));
        host.Discord.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteHealthData_does_nothing_once_the_user_agreed_again()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await SeedWithdrawnUserAsync(host, 13);
        time.Advance(TimeSpan.FromDays(40));
        await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().GiveHealthDataConsentAsync(TestUsers.Id(13)));

        var deleted = await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>()
            .DeleteHealthDataAsync(TestUsers.Id(13), time.GetUtcNow().UtcDateTime - HabitService.WithdrawnDataLifetime));

        deleted.Should().BeFalse();
        (await CountsAsync(host, 13)).Habits.Should().Be(1);
    }
}
