using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Privacy;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

/// <summary>Pins which rows <see cref="ConsentService.DeleteHealthDataAsync"/> removes and which it keeps.</summary>
[Collection("Integration")]
public class HealthDataDeletionScopeTests
{
    internal static readonly DateTimeOffset Now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = TestUsers.Id(1);

    private sealed record Counts(int Habits, int Logs, int Slips, int HabitNotes, int Marks, int DayNotes, int Moods, int Helps,
        int ReminderMessages, int PushReminders, int ActionTokens, int Routines, int JournalKeys, int PushSubscriptions, int Users);

    internal static async Task SeedAsync(IntegrationTestHost host)
    {
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User
            {
                Id = UserId,
                Logins = TestUsers.Discord(1),
                Timezone = "Europe/Berlin",
                Language = "de",
                GraceDaysPerWeek = 3,
                Theme = "tidepool",
                FiresideEnabled = true,
                FiresideName = "Sam",
                HealthDataConsentAt = Now.UtcDateTime.AddDays(-60),
                ConsentPolicyVersion = ConsentService.CurrentConsentPolicyVersion,
            };
            var habit = new Habit { Name = "Take meds", CreatedAt = Now.UtcDateTime.AddDays(-60) };
            habit.Logs.Add(new HabitLog { Date = new DateOnly(2027, 1, 10), Completed = true, Count = 1 });
            habit.Slips.Add(new HabitSlip { Date = new DateOnly(2027, 1, 11), Count = 1, Reasons = ["stress"] });
            habit.Notes.Add(new HabitNote { Date = new DateOnly(2027, 1, 10), Text = "Went fine" });
            user.Habits.Add(habit);
            user.DayMarks.Add(new DayMark { Date = new DateOnly(2027, 1, 12) });
            user.DayNotes.Add(new DayNote { Date = new DateOnly(2027, 1, 12), Text = "Rough day" });
            user.Moods.Add(new Mood { Label = "Tired" });
            user.Helps = new UserHelps { Encrypted = "c3ludGhldGlj", Version = 1, UpdatedAt = Now.UtcDateTime };
            user.Routines.Add(new Routine { Name = "Morning" });
            user.JournalKey = new JournalKey { Version = 1, PassphraseSalt = "cHM=", PassphraseIterations = 1, PassphraseIv = "aXY=", PassphraseWrapped = "d3I=" };
            user.PushSubscriptions.Add(new Ember.Core.Models.PushSubscription { Endpoint = "https://push.example.test/u1", P256dh = "k", Auth = "a" });
            db.Users.Add(user);
            await db.SaveChangesAsync();

            db.ReminderMessages.Add(new ReminderMessage { UserId = UserId, HabitId = habit.Id, Date = new DateOnly(2027, 1, 14), ChannelId = 5, MessageId = 6 });
            db.PushReminders.Add(new PushReminder { UserId = UserId, HabitId = habit.Id, Date = new DateOnly(2027, 1, 14) });
            db.ReminderActionTokens.Add(new ReminderActionToken
            { TokenHash = [1, 2, 3], UserId = UserId, HabitId = habit.Id, Day = new DateOnly(2027, 1, 14), ExpiresUtc = Now.UtcDateTime.AddDays(1) });
            await db.SaveChangesAsync();
        });
        (await host.WithScopeAsync(sp => sp.GetRequiredService<ConsentService>().WithdrawHealthDataConsentAsync(UserId))).Should().BeTrue();
    }

    private static Task<Counts> CountsAsync(IntegrationTestHost host)
        => host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            return new Counts(await db.Habits.CountAsync(), await db.HabitLogs.CountAsync(), await db.HabitSlips.CountAsync(),
                await db.HabitNotes.CountAsync(), await db.DayMarks.CountAsync(), await db.DayNotes.CountAsync(), await db.Moods.CountAsync(),
                await db.UserHelps.CountAsync(), await db.ReminderMessages.CountAsync(), await db.PushReminders.CountAsync(),
                await db.ReminderActionTokens.CountAsync(), await db.Routines.CountAsync(), await db.JournalKeys.CountAsync(),
                await db.PushSubscriptions.CountAsync(), await db.Users.CountAsync());
        });

    [Fact]
    public async Task DeletingHealthData_RemovesHabitsAndEverythingHangingOffThem_AndKeepsAccountSettingsRoutinesKeyPushAndFireside()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await SeedAsync(host);
        (await CountsAsync(host)).Should().Be(new Counts(1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1));
        time.Advance(ConsentService.WithdrawnDataLifetime);

        var deleted = await host.WithScopeAsync(sp => sp.GetRequiredService<ConsentService>()
            .DeleteHealthDataAsync(UserId, time.GetUtcNow().UtcDateTime));

        deleted.Should().BeTrue();
        (await CountsAsync(host)).Should().Be(new Counts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1));
        var user = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.AsNoTracking().SingleAsync());
        (user.Timezone, user.Language, user.GraceDaysPerWeek, user.Theme).Should().Be(("Europe/Berlin", "de", 3, "tidepool"));
        (user.FiresideEnabled, user.FiresideName).Should().Be((true, "Sam"));
        user.ConsentWithdrawnAt.Should().NotBeNull("the withdrawal stays recorded after the deletion");
    }

    [Fact]
    public async Task DeletingHealthData_BeforeTheCutoffOfTheWithdrawal_DeletesNothing()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await SeedAsync(host);

        var deleted = await host.WithScopeAsync(sp => sp.GetRequiredService<ConsentService>()
            .DeleteHealthDataAsync(UserId, time.GetUtcNow().UtcDateTime.AddMinutes(-1)));

        deleted.Should().BeFalse();
        (await CountsAsync(host)).Habits.Should().Be(1);
    }

    /// <summary>Seeds one withdrawn user with every health table filled, deletes, and asserts every table classified as deleted is empty.</summary>
    internal static async Task AssertWithdrawalEmptiesEveryHealthTableAsync(IntegrationTestHost host, FakeTimeProvider time)
    {
        await SeedAsync(host);
        time.Advance(ConsentService.WithdrawnDataLifetime);
        var tables = await host.WithScopeAsync(sp => Task.FromResult(sp.GetRequiredService<EmberDbContext>().Model.GetEntityTypes()
            .Where(t => DataLifecycle.Entities.Single(e => e.Entity == t.ClrType).Withdrawal != WithdrawalRule.Kept)
            .Select(t => t.GetTableName()!).ToList()));

        async Task<Dictionary<string, long>> CountRowsAsync() => await host.WithScopeAsync(async sp =>
        {
            var connection = sp.GetRequiredService<EmberDbContext>().Database.GetDbConnection();
            await connection.OpenAsync();
            var counts = new Dictionary<string, long>();
            foreach (var table in tables)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
                counts[table] = Convert.ToInt64(await command.ExecuteScalarAsync());
            }
            return counts;
        });

        (await CountRowsAsync()).Should().OnlyContain(c => c.Value > 0, "the seed fills every health table, so the deletion has something to empty");
        (await host.WithScopeAsync(sp => sp.GetRequiredService<ConsentService>().DeleteHealthDataAsync(UserId, time.GetUtcNow().UtcDateTime))).Should().BeTrue();
        (await CountRowsAsync()).Should().OnlyContain(c => c.Value == 0);
    }

    [Fact]
    public async Task WithdrawalEmptiesEveryHealthTable()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);

        await AssertWithdrawalEmptiesEveryHealthTableAsync(host, time);
    }
}
