using Discord;
using Ember.Bot.Services;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static Ember.Tests.ReminderDispatchTests;

namespace Ember.Tests;

/// <summary>The production database is Postgres: the reminder job and the double-tap races run here against a real one, as in the SQLite suites.</summary>
[Collection("Postgres")]
public class PostgresBehaviorTests(PostgresFixture postgres)
{
    private static readonly DateOnly Day = new(2027, 1, 14);

    private async Task<IntegrationTestHost> HostAsync(FakeTimeProvider time, params Habit[] habits)
        => await CreateHostAsync(time, null, await postgres.CreateDatabaseAsync(), habits);

    [DockerFact]
    public async Task DueReminder_IsSentWithCheckInButtons_AndAdvancesToTheNextLocalOccurrence()
    {
        await using var host = await HostAsync(new FakeTimeProvider(Now), DueHabit(1, 10));

        await RunJobAsync(host);
        await RunJobAsync(host);

        var dm = host.Discord.Messages.Should().ContainSingle().Subject;
        dm.UserId.Should().Be(10);
        dm.Components!.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().Select(b => b.CustomId)
            .Should().Equal($"checkin:done:1:{Now.ToUnixTimeSeconds()}", $"checkin:skip:1:{Now.ToUnixTimeSeconds()}",
                $"checkin:snooze:1:{Now.ToUnixTimeSeconds()}", "discordreminders:stop:10");
        (await NextReminderAsync(host, 1)).Should().Be(TomorrowNineBerlin);
    }

    [DockerFact]
    public async Task ReminderOverdueByMoreThanTwoHours_IsAdvancedWithoutSending()
    {
        await using var host = await HostAsync(new FakeTimeProvider(Now),
            DueHabit(1, 10, Now.UtcDateTime.AddHours(-2).AddMinutes(-1)), DueHabit(2, 20, Now.UtcDateTime.AddHours(-2)));

        await RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.UserId.Should().Be(20);
        (await NextReminderAsync(host, 1)).Should().Be(TomorrowNineBerlin);
        (await NextReminderAsync(host, 2)).Should().Be(TomorrowNineBerlin);
    }

    [DockerFact]
    public async Task PausedOrAlreadyCompletedToday_IsAdvancedWithoutSending()
    {
        var paused = DueHabit(1, 10);
        paused.PausedUntil = Today;
        var done = DueHabit(2, 10);
        done.Logs.Add(new HabitLog { Date = Today, Completed = true });
        await using var host = await HostAsync(new FakeTimeProvider(Now), paused, done);

        await RunJobAsync(host);

        host.Discord.Messages.Should().BeEmpty();
        (await NextReminderAsync(host, 1)).Should().Be(TomorrowNineBerlin);
        (await NextReminderAsync(host, 2)).Should().Be(TomorrowNineBerlin);
    }

    [DockerFact]
    public async Task TransientSendFailure_DoesNotBlockOthers_AndIsRetriedFiveMinutesLaterOnce()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await HostAsync(time, DueHabit(1, 10), DueHabit(2, 20));
        host.Discord.Deliver = userId => userId == 10 ? throw new HttpRequestException("boom") : DeliveryOutcome.Delivered;

        await RunJobAsync(host);

        (await NextReminderAsync(host, 1)).Should().Be(Now.UtcDateTime);
        (await NextReminderAsync(host, 2)).Should().Be(TomorrowNineBerlin);
        (await UserAsync(host, 10)).DmFailureCount.Should().Be(0);

        host.Discord.Deliver = _ => DeliveryOutcome.Delivered;
        time.Advance(TimeSpan.FromMinutes(1));
        await RunJobAsync(host);
        time.Advance(TimeSpan.FromMinutes(4));
        await RunJobAsync(host);
        time.Advance(TimeSpan.FromMinutes(5));
        await RunJobAsync(host);

        host.Discord.Messages.Select(m => m.UserId).Should().BeEquivalentTo([10UL, 20UL, 10UL]);
        (await NextReminderAsync(host, 1)).Should().Be(TomorrowNineBerlin);
    }

    [DockerFact]
    public async Task ThreeUndeliverableReminders_PauseDmReminders_UntilTheUserIsReachableAgain()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await HostAsync(time, DueHabit(1, 10));
        host.Discord.Deliver = _ => DeliveryOutcome.Undeliverable;

        for (var day = 0; day < 4; day++)
        {
            await RunJobAsync(host);
            time.Advance(TimeSpan.FromDays(1));
        }

        host.Discord.Messages.Should().HaveCount(3);
        (await UserAsync(host, 10)).DmRemindersPausedAt.Should().NotBeNull();

        host.Discord.Deliver = _ => DeliveryOutcome.Delivered;
        await host.WithScopeAsync(sp => sp.GetRequiredService<ReminderService>().ResumeDmRemindersAsync(TestUsers.Id(10)));
        await RunJobAsync(host);

        host.Discord.Messages.Should().HaveCount(4);
        (await UserAsync(host, 10)).DmRemindersPausedAt.Should().BeNull();
    }

    private async Task<(IntegrationTestHost Host, DoubleTapTests.RaceInterceptor Race, string ConnectionString)> RaceHostAsync(long discordId)
    {
        var race = new DoubleTapTests.RaceInterceptor();
        var connectionString = await postgres.CreateDatabaseAsync();
        var host = await IntegrationTestHost.CreateAsync(time: new FakeTimeProvider(new DateTimeOffset(2027, 1, 14, 12, 0, 0, TimeSpan.Zero)),
            configureDb: o => o.AddInterceptors(race), postgresConnectionString: connectionString);
        return (host, race, connectionString);
    }

    private static async Task InsertFromOtherRequestAsync(string connectionString, Action<EmberDbContext> add)
    {
        await using var other = new EmberDbContext(new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options);
        add(other);
        await other.SaveChangesAsync();
    }

    private static async Task<int> SeedConsentedHabitAsync(IntegrationTestHost host, long discordId)
        => await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var habit = new Habit
            {
                User = new User
                {
                    Id = TestUsers.Id(discordId),
                    Logins = TestUsers.Discord(discordId),
                    HealthDataConsentAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ConsentPolicyVersion = ConsentService.CurrentConsentPolicyVersion,
                },
                Name = "Stretch",
                CreatedAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            db.Habits.Add(habit);
            await db.SaveChangesAsync();
            return habit.Id;
        });

    [DockerFact]
    public async Task ResolveOrCreateDiscordUser_WhenAnotherRequestInsertedTheUserFirst_ReturnsThatUserAndKeepsOneRow()
    {
        var (host, race, cs) = await RaceHostAsync(700);
        await using var _ = host;
        race.Competitor = _ => InsertFromOtherRequestAsync(cs, db => db.Users.Add(new User { Id = TestUsers.Id(700), Logins = TestUsers.Discord(700) }));

        var userId = await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().ResolveOrCreateDiscordUserAsync(700));

        userId.Should().Be(TestUsers.Id(700));
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.CountAsync(u => u.Id == TestUsers.Id(700)))).Should().Be(1);
    }

    [DockerFact]
    public async Task CheckIn_WhenAnotherRequestLoggedTheSameDayFirst_UpdatesThatRowInsteadOfThrowing()
    {
        var (host, race, cs) = await RaceHostAsync(701);
        await using var _ = host;
        var habitId = await SeedConsentedHabitAsync(host, 701);
        race.Competitor = _ => InsertFromOtherRequestAsync(cs, db => db.HabitLogs.Add(new HabitLog { HabitId = habitId, Date = Day, Completed = false }));

        var result = await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().CheckInAsync(await TestUsers.UserIdAsync(sp, 701), habitId, Day, completed: true));

        result.Should().Be(DayEditResult.Saved);
        var logs = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().HabitLogs.Where(l => l.HabitId == habitId).ToListAsync());
        logs.Should().ContainSingle().Which.Completed.Should().BeTrue();
    }

    [DockerFact]
    public async Task LowDay_WhenAnotherRequestMarkedTheSameDayFirst_StillSavesAndKeepsOneMark()
    {
        var (host, race, cs) = await RaceHostAsync(702);
        await using var _ = host;
        await SeedConsentedHabitAsync(host, 702);
        race.Competitor = _ => InsertFromOtherRequestAsync(cs, db => db.DayMarks.Add(new DayMark { UserId = TestUsers.Id(702), Date = Day, Kind = DayMarkKind.LowDay }));

        var result = await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().SetLowDayAsync(await TestUsers.UserIdAsync(sp, 702), Day));

        result.Should().Be(DayEditResult.Saved);
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().DayMarks.CountAsync(m => m.Date == Day))).Should().Be(1);
    }

    [DockerFact]
    public async Task WithdrawalEmptiesEveryHealthTable()
    {
        var time = new FakeTimeProvider(HealthDataDeletionScopeTests.Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time, postgresConnectionString: await postgres.CreateDatabaseAsync());

        await HealthDataDeletionScopeTests.AssertWithdrawalEmptiesEveryHealthTableAsync(host, time);
    }
}
