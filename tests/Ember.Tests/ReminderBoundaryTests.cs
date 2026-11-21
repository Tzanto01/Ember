using System.Security.Cryptography;
using Discord;
using Ember.Bot.Discord.Messages;
using Ember.Bot.Jobs;
using Ember.Bot.Modules;
using Ember.Bot.Notifications;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using static Ember.Tests.WebPushTestKeys;

namespace Ember.Tests;

[Collection("Integration")]
public class ReminderBoundaryTests
{
    private const long DiscordUser = 10;
    private const string Berlin = "Europe/Berlin";
    private const string Kolkata = "Asia/Kolkata";
    private static readonly ulong Channel = TestDiscordDmSender.ChannelOf(DiscordUser);

    // Friday 2027-01-15, 09:00 in Berlin (UTC+1).
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static DateTime Utc(int month, int day, int hour, int minute = 0, int second = 0)
        => new(2027, month, day, hour, minute, second, DateTimeKind.Utc);

    private static async Task<IntegrationTestHost> CreateHostAsync(FakeTimeProvider time, string timezone, int secondReminderMinutes, params Habit[] habits)
    {
        var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.Users.Add(new User
            {
                Id = TestUsers.Id(DiscordUser),
                Logins = TestUsers.Discord(DiscordUser),
                DiscordReminders = true,
                Timezone = timezone,
                SecondReminderMinutes = secondReminderMinutes,
                HealthDataConsentAt = Now.UtcDateTime,
                ConsentPolicyVersion = ConsentService.CurrentConsentPolicyVersion,
            });
            db.Habits.AddRange(habits);
            await db.SaveChangesAsync();
        });
        return host;
    }

    private static Habit DueHabit(int id, DateTime nextUtc, TimeOnly? reminderTime = null) => new()
    {
        Id = id,
        UserId = TestUsers.Id(DiscordUser),
        Name = $"Habit {id}",
        ReminderTime = reminderTime ?? new TimeOnly(9, 0),
        NextReminderUtc = nextUtc,
        CreatedAt = Now.UtcDateTime.AddDays(-30),
    };

    private static Task RunJobAsync(IntegrationTestHost host)
        => ActivatorUtilities.CreateInstance<ReminderDispatchJob>(host.Host.Services).RunAsync();

    private static Task<(DateTime? Next, DateTime? Second)> RemindersAsync(IntegrationTestHost host, int habitId)
        => host.WithScopeAsync(async sp =>
        {
            var r = await sp.GetRequiredService<EmberDbContext>().Habits.Where(h => h.Id == habitId)
                .Select(h => new { h.NextReminderUtc, h.SecondReminderUtc }).SingleAsync();
            return (r.NextReminderUtc, r.SecondReminderUtc);
        });

    private static Task<List<ReminderMessage>> OpenMessagesAsync(IntegrationTestHost host)
        => host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().ReminderMessages.AsNoTracking().ToListAsync());

    private static Task SetSecondReminderAsync(IntegrationTestHost host, int habitId, DateTime at)
        => host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Habits.Where(h => h.Id == habitId)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.SecondReminderUtc, at)));

    [Fact]
    public async Task ReminderOverdueByTwoHoursAndOneSecond_IsAdvancedWithoutSending_WhileExactlyTwoHoursStillSends()
    {
        await using var host = await CreateHostAsync(new FakeTimeProvider(Now), Berlin, 0,
            DueHabit(1, Now.UtcDateTime.AddHours(-2)),
            DueHabit(2, Now.UtcDateTime.AddHours(-2).AddSeconds(-1)));

        await RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.Text.Should().Contain("Habit 1");
        (await RemindersAsync(host, 1)).Next.Should().Be(Utc(1, 16, 8));
        (await RemindersAsync(host, 2)).Next.Should().Be(Utc(1, 16, 8));
    }

    [Fact]
    public async Task SecondReminderOverdueByTwoHoursAndOneSecond_IsDroppedWithoutSending_WhileExactlyTwoHoursStillSends()
    {
        await using var host = await CreateHostAsync(new FakeTimeProvider(Now), Berlin, 30,
            DueHabit(1, Utc(1, 16, 8)),
            DueHabit(2, Utc(1, 16, 8)));
        await SetSecondReminderAsync(host, 1, Now.UtcDateTime.AddHours(-2));
        await SetSecondReminderAsync(host, 2, Now.UtcDateTime.AddHours(-2).AddSeconds(-1));

        await RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.Text.Should().Contain("Habit 1").And.Contain("One more nudge");
        (await RemindersAsync(host, 1)).Second.Should().BeNull();
        (await RemindersAsync(host, 2)).Second.Should().BeNull();
    }

    [Fact]
    public async Task WeeklyHabit_CountsOnlyTheLocalMondayToSundayWeek_SoNeighbouringSundayAndMondayLogsNeverTipItOver()
    {
        Habit Weekly(int id, params int[] days)
        {
            var habit = DueHabit(id, Now.UtcDateTime);
            habit.FrequencyType = FrequencyType.Weekly;
            habit.WeeklyTarget = 3;
            foreach (var day in days)
                habit.Logs.Add(new HabitLog { Date = new DateOnly(2027, 1, day), Completed = true });
            return habit;
        }
        await using var host = await CreateHostAsync(new FakeTimeProvider(Now), Berlin, 0,
            Weekly(1, 10, 12, 13, 18),
            Weekly(2, 11, 13, 17),
            Weekly(3, 10, 11, 12, 18));

        await RunJobAsync(host);

        host.Discord.Messages.Select(m => m.Text).Should().HaveCount(2)
            .And.Contain(t => t!.Contains("Habit 1")).And.Contain(t => t!.Contains("Habit 3"));
        (await RemindersAsync(host, 2)).Next.Should().Be(Utc(1, 16, 8));
    }

    [Fact]
    public async Task WeeklyHabit_OnTheLocalMondayWhileUtcIsStillSunday_StartsANewWeekAndSends()
    {
        var now = new DateTimeOffset(2027, 1, 17, 22, 0, 0, TimeSpan.Zero);
        var habit = DueHabit(1, now.UtcDateTime);
        habit.FrequencyType = FrequencyType.Weekly;
        habit.WeeklyTarget = 3;
        foreach (var day in new[] { 15, 16, 17 })
            habit.Logs.Add(new HabitLog { Date = new DateOnly(2027, 1, day), Completed = true });
        await using var host = await CreateHostAsync(new FakeTimeProvider(now), "Pacific/Auckland", 0, habit);

        await RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.Text.Should().Contain("Habit 1");
    }

    [Fact]
    public async Task HabitPausedUntilYesterday_IsRemindedToday()
    {
        var paused = DueHabit(1, Now.UtcDateTime);
        paused.PausedUntil = new DateOnly(2027, 1, 14);
        await using var host = await CreateHostAsync(new FakeTimeProvider(Now), Berlin, 0, paused);

        await RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.Text.Should().Contain("Habit 1");
    }

    [Fact]
    public async Task ALetGoReminder_IgnoresADoneLogAndALowDay_ButNotAPause()
    {
        Habit LetGo(int id, DateOnly? pausedUntil = null)
        {
            var habit = DueHabit(id, Now.UtcDateTime);
            habit.Kind = HabitKind.LetGo;
            habit.PausedUntil = pausedUntil;
            habit.Logs.Add(new HabitLog { Date = new DateOnly(2027, 1, 15), Completed = true });
            return habit;
        }
        await using var host = await CreateHostAsync(new FakeTimeProvider(Now), Berlin, 0, LetGo(1), LetGo(2, new DateOnly(2027, 1, 15)));
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.DayMarks.Add(new DayMark { UserId = TestUsers.Id(DiscordUser), Date = new DateOnly(2027, 1, 15), Kind = DayMarkKind.LowDay });
            await db.SaveChangesAsync();
        });

        await RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.Text.Should().Contain("Habit 1");
    }

    [Fact]
    public async Task ADiscordReminderOfDayD_StaysOpenUntil1000LocalOnTheNextDay_InAHalfHourZone()
    {
        var sentAt = new DateTimeOffset(2027, 1, 15, 14, 30, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(sentAt);
        await using var host = await CreateHostAsync(time, Kolkata, 0, DueHabit(1, sentAt.UtcDateTime, new TimeOnly(20, 0)));
        await RunJobAsync(host);
        var first = host.Discord.Messages.Should().ContainSingle().Subject;
        (await OpenMessagesAsync(host)).Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2027, 1, 15));

        time.SetUtcNow(new DateTimeOffset(2027, 1, 16, 4, 29, 59, TimeSpan.Zero));
        await RunJobAsync(host);
        host.Discord.Changes.Should().BeEmpty();
        (await OpenMessagesAsync(host)).Should().ContainSingle();

        time.Advance(TimeSpan.FromSeconds(1));
        await RunJobAsync(host);

        host.Discord.Changes.Should().Equal(new ChangedDiscordDm(Channel, first.MessageId, ReminderDm.ClosedLine(Ember.Bot.BotTexts.English)));
        (await OpenMessagesAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task DoneOrSkipOnAReminderOfDayD_IsExpiredFrom1000LocalOnTheNextDay_InAHalfHourZone()
    {
        var sentAt = new DateTimeOffset(2027, 1, 15, 14, 30, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(sentAt);
        await using var host = await CreateHostAsync(time, Kolkata, 0, DueHabit(1, Utc(1, 20, 0)));
        var sent = sentAt.ToUnixTimeSeconds().ToString();
        var buttons = new ComponentBuilder()
            .WithButton("Done ✅", $"checkin:done:1:{sent}", ButtonStyle.Success)
            .WithButton("Skip for today", $"checkin:skip:1:{sent}", ButtonStyle.Secondary)
            .Build().Components;
        Task<string?> ExpiredLine() => host.WithScopeAsync(sp =>
            CheckInButtonModule.ExpiredLineAsync(Ember.Bot.BotTexts.English, sp.GetRequiredService<HabitService>(), sp.GetRequiredService<UserSettingsService>(), time,
                TestUsers.Id(DiscordUser), sent, buttons));

        time.SetUtcNow(new DateTimeOffset(2027, 1, 16, 4, 29, 59, TimeSpan.Zero));
        (await ExpiredLine()).Should().BeNull();

        time.Advance(TimeSpan.FromSeconds(1));
        (await ExpiredLine()).Should().Be(ReminderDm.ClosedLine(Ember.Bot.BotTexts.English));
    }

    [Fact]
    public async Task TheWebPushDoneToken_ExpiresAt1000LocalOnTheNextLocalDay_InAHalfHourZone()
    {
        var sentAt = new DateTimeOffset(2027, 1, 15, 20, 0, 0, TimeSpan.Zero);
        var push = new FakePushService();
        var (publicKey, privateKey) = NewKeyPair();
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(sentAt),
            args: [$"--WebPush:PublicKey={publicKey}", $"--WebPush:PrivateKey={privateKey}", "--WebPush:Subject=mailto:test@example.test"],
            configureServices: services => services.AddHttpClient(WebPushSender.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => push));
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User { Id = TestUsers.Id(DiscordUser), Timezone = Kolkata, UserName = "web@example.com", DiscordReminders = false };
            db.Users.Add(user);
            db.Habits.Add(DueHabit(1, sentAt.UtcDateTime, new TimeOnly(1, 30)));
            var session = new AuthSession { Id = Guid.NewGuid(), UserId = user.Id, CreatedUtc = sentAt, ExpiresUtc = sentAt.AddDays(14) };
            db.AuthSessions.Add(session);
            var (clientKey, _) = NewKeyPair();
            db.PushSubscriptions.Add(new Ember.Core.Models.PushSubscription
            {
                UserId = user.Id, SessionId = session.Id, Endpoint = "https://fcm.googleapis.com/send/kolkata", P256dh = clientKey,
                Auth = Base64Url(RandomNumberGenerator.GetBytes(16)),
            });
            await db.SaveChangesAsync();
        });

        await RunJobAsync(host);

        push.Count.Should().Be(1);
        var token = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().ReminderActionTokens.SingleAsync());
        token.Day.Should().Be(new DateOnly(2027, 1, 16));
        token.ExpiresUtc.Should().Be(Utc(1, 17, 4, 30));
    }

    [Theory]
    [InlineData(17, 59, 59, 23, 29, true)]
    [InlineData(18, 0, 0, 23, 30, false)]
    public async Task SecondReminder_IsScheduledOnlyWhenItStillFallsOnTheSameLocalDay_InAHalfHourZone(
        int hour, int minute, int second, int reminderHour, int reminderMinute, bool scheduled)
    {
        var now = new DateTimeOffset(2027, 1, 15, hour, minute, second, TimeSpan.Zero);
        var due = Utc(1, 15, 18, 0).AddMinutes(reminderMinute - 30);
        await using var host = await CreateHostAsync(new FakeTimeProvider(now), Kolkata, 30, DueHabit(1, due, new TimeOnly(reminderHour, reminderMinute)));

        await RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle();
        var (next, secondAt) = await RemindersAsync(host, 1);
        next.Should().Be(due.AddDays(1));
        secondAt.Should().Be(scheduled ? now.UtcDateTime.AddMinutes(30) : null);
    }

    [Fact]
    public async Task WeeklyNudge_AppearsWithTwoDaysLeftAtTheExactCount_AndNotWhenTheTargetIsMet_OrThePauseCoversYesterday()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 17, 10, 0, 0, TimeSpan.Zero));
        await using var host = await IntegrationTestHost.CreateAsync(time: time);
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            User Weekly(long id, int[] days, DateOnly? pausedUntil = null)
            {
                var user = new User { Id = TestUsers.Id(id), Logins = TestUsers.Discord(id), Timezone = "UTC" };
                var habit = new Habit
                {
                    Name = "Swim", FrequencyType = FrequencyType.Weekly, WeeklyTarget = 3, PausedUntil = pausedUntil,
                    CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                };
                foreach (var day in days)
                    habit.Logs.Add(new HabitLog { Date = new DateOnly(2026, 1, day), Completed = true });
                user.Habits.Add(habit);
                return user;
            }
            db.Users.AddRange(
                Weekly(2001, [11, 12, 14, 17]),
                Weekly(2002, [12, 14, 15]),
                Weekly(2003, [12, 14], new DateOnly(2026, 1, 16)),
                Weekly(2004, [12, 14], new DateOnly(2026, 1, 15)));
            await db.SaveChangesAsync();
        });

        await host.RunJobAsync(MissedDayReflectionJob.Key);

        host.Discord.Messages.Select(m => m.UserId).Should().BeEquivalentTo([2001UL, 2004UL]);
        host.Discord.Messages.Single(m => m.UserId == 2001).Embed!.Description.Should().StartWith("You're at 2/3 for **Swim** this week.");
    }

    [Fact]
    public async Task WeeklyNudge_IsNotSentWithThreeDaysLeftInTheWeek()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 16, 10, 0, 0, TimeSpan.Zero));
        await using var host = await IntegrationTestHost.CreateAsync(time: time);
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User { Id = TestUsers.Id(2101), Logins = TestUsers.Discord(2101), Timezone = "UTC" };
            user.Habits.Add(new Habit
            {
                Name = "Swim", FrequencyType = FrequencyType.Weekly, WeeklyTarget = 3,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });

        await host.RunJobAsync(MissedDayReflectionJob.Key);

        host.Discord.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task DailyHabitPausedUntilTheDayBeforeYesterday_IsListedAsOpen()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 17, 10, 0, 0, TimeSpan.Zero));
        await using var host = await IntegrationTestHost.CreateAsync(time: time);
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User { Id = TestUsers.Id(2201), Logins = TestUsers.Discord(2201), Timezone = "UTC" };
            user.Habits.Add(new Habit { Name = "Walk", PausedUntil = new DateOnly(2026, 1, 15), CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });

        await host.RunJobAsync(MissedDayReflectionJob.Key);

        host.Discord.Messages.Should().ContainSingle().Which.Embed!.Description.Should().Contain("Walk");
    }
}
