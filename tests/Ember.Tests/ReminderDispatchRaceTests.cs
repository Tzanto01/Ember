using Ember.Bot.Jobs;
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

/// <summary>
/// A reminder edit (from the API) or a snooze (from the reminder's Discord button) that lands while the dispatch job is still sending
/// survives the job's save, and the reminder is still sent exactly once.
/// </summary>
[Collection("Integration")]
public class ReminderDispatchRaceTests
{
    [Theory]
    [InlineData(HabitEdit.Snooze)]
    [InlineData(HabitEdit.NewReminderTime)]
    public Task HabitEditedWhileItsReminderIsSent_KeepsTheEditedNextReminder(HabitEdit edit)
        => Race.HabitEditedDuringSendAsync(edit, postgres: null);

    [Fact]
    public Task RoutineEditedWhileItsReminderIsSent_KeepsTheEditedNextReminder()
        => Race.RoutineEditedDuringSendAsync(postgres: null);

    [Fact]
    public Task HabitSnoozedWhileItsReminderIsSent_SendsNoSecondReminder()
        => Race.HabitSnoozedDuringSendWithSecondReminderAsync(postgres: null);
}

/// <summary>The same races against Postgres, the production database, where the compare-and-set runs as real SQL.</summary>
[Collection("Postgres")]
public class ReminderDispatchRacePostgresTests(PostgresFixture postgres)
{
    [DockerFact]
    public async Task HabitSnoozedWhileItsReminderIsSent_KeepsTheSnooze()
        => await Race.HabitEditedDuringSendAsync(HabitEdit.Snooze, await postgres.CreateDatabaseAsync());

    [DockerFact]
    public async Task RoutineEditedWhileItsReminderIsSent_KeepsTheEditedNextReminder()
        => await Race.RoutineEditedDuringSendAsync(await postgres.CreateDatabaseAsync());

    [DockerFact]
    public async Task HabitSnoozedWhileItsReminderIsSent_SendsNoSecondReminder()
        => await Race.HabitSnoozedDuringSendWithSecondReminderAsync(await postgres.CreateDatabaseAsync());
}

public enum HabitEdit { Snooze, NewReminderTime }

internal static class Race
{
    private const long DiscordUser = 10;
    private static readonly Guid UserId = TestUsers.Id(DiscordUser);

    // 15:00 in Berlin (UTC+1) on the same Friday.
    private static readonly TimeOnly Three = new(15, 0);
    private static readonly DateTime TodayThreeBerlin = new(2027, 1, 15, 14, 0, 0, DateTimeKind.Utc);

    public static async Task HabitEditedDuringSendAsync(HabitEdit edit, string? postgres)
    {
        var time = new FakeTimeProvider(Now);
        var channel = new EditDuringSendChannel();
        await using var host = await HostAsync(time, channel, postgres, db => db.Habits.Add(DueHabit(1, DiscordUser)));
        channel.OnSend = () => host.WithScopeAsync(sp => edit == HabitEdit.Snooze
            ? sp.GetRequiredService<ReminderService>().SnoozeReminderAsync(UserId, 1, Now)
            : sp.GetRequiredService<HabitService>().EditHabitAsync(UserId, 1, newName: null, newReminderTime: null,
                clearReminder: false, newReminderTimes: [Three]));
        var edited = edit == HabitEdit.Snooze ? Now.UtcDateTime + ReminderService.SnoozeDelay : TodayThreeBerlin;

        await RunAsync(host);
        time.Advance(TimeSpan.FromMinutes(1));
        await RunAsync(host);

        channel.Sent.Should().ContainSingle().Which.ItemId.Should().Be(1);
        (await NextReminderAsync(host, 1)).Should().Be(edited);
    }

    /// <summary>
    /// The user wants a second reminder 30 minutes after a delivered one, but snoozes this one while it is sent: the snooze drops the
    /// second reminder, so nothing is sent at 08:30 and the snoozed reminder comes at 09:00.
    /// </summary>
    public static async Task HabitSnoozedDuringSendWithSecondReminderAsync(string? postgres)
    {
        var time = new FakeTimeProvider(Now);
        var channel = new EditDuringSendChannel();
        await using var host = await HostAsync(time, channel, postgres, db => db.Habits.Add(DueHabit(1, DiscordUser)),
            secondReminderMinutes: 30);
        channel.OnSend = () => host.WithScopeAsync(sp => sp.GetRequiredService<ReminderService>().SnoozeReminderAsync(UserId, 1, Now));

        await RunAsync(host);
        time.Advance(TimeSpan.FromMinutes(31));
        await RunAsync(host);

        channel.Sent.Should().ContainSingle();
        (await NextReminderAsync(host, 1)).Should().Be(Now.UtcDateTime + ReminderService.SnoozeDelay);
    }

    public static async Task RoutineEditedDuringSendAsync(string? postgres)
    {
        var time = new FakeTimeProvider(Now);
        var channel = new EditDuringSendChannel();
        await using var host = await HostAsync(time, channel, postgres, db =>
        {
            db.Routines.Add(new Routine { Id = 1, UserId = UserId, Name = "Morning", ReminderTime = Nine, NextReminderUtc = Now.UtcDateTime });
            db.Habits.Add(new Habit { Id = 1, UserId = UserId, Name = "Habit 1", RoutineId = 1, CreatedAt = Now.UtcDateTime.AddDays(-30) });
        });
        channel.OnSend = () => host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>()
            .EditRoutineAsync(UserId, 1, name: null, reminderTime: Three, clearReminder: false, reminderExclusions: null, habitIds: null));

        await RunAsync(host);
        time.Advance(TimeSpan.FromMinutes(1));
        await RunAsync(host);

        channel.Sent.Should().ContainSingle().Which.ItemId.Should().Be(1);
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Routines.Select(r => r.NextReminderUtc).SingleAsync()))
            .Should().Be(TodayThreeBerlin);
    }

    /// <summary>A user reached only by web push, through <paramref name="channel"/>, with the items <paramref name="seed"/> adds.</summary>
    private static async Task<IntegrationTestHost> HostAsync(FakeTimeProvider time, EditDuringSendChannel channel, string? postgres,
        Action<EmberDbContext> seed, int secondReminderMinutes = 0)
    {
        var host = await IntegrationTestHost.CreateAsync(start: false, time: time, postgresConnectionString: postgres,
            configureServices: services => services.AddSingleton<IReminderChannel>(channel));
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.Users.Add(new User
            {
                Id = UserId, Logins = TestUsers.Discord(DiscordUser), Timezone = "Europe/Berlin", SecondReminderMinutes = secondReminderMinutes,
                PushSubscriptions = [new PushSubscription { Endpoint = "https://push.example/u10", P256dh = "k", Auth = "a" }],
            });
            seed(db);
            await db.SaveChangesAsync();
        });
        return host;
    }

    private static Task RunAsync(IntegrationTestHost host)
        => ActivatorUtilities.CreateInstance<ReminderDispatchJob>(host.Host.Services).RunAsync();

    /// <summary>A web push channel that runs <see cref="OnSend"/> on the first send, before it reports the reminder delivered.</summary>
    private sealed class EditDuringSendChannel : IReminderChannel
    {
        private readonly Lock _gate = new();
        private readonly List<Reminder> _sent = [];

        public Func<Task>? OnSend { get; set; }

        public IReadOnlyList<Reminder> Sent
        {
            get
            {
                lock (_gate) return _sent.ToList();
            }
        }

        public ReminderChannels Channel => ReminderChannels.WebPush;

        public async Task<DeliveryOutcome> SendAsync(Reminder reminder, CancellationToken cancellationToken = default)
        {
            bool first;
            lock (_gate)
            {
                _sent.Add(reminder);
                first = _sent.Count == 1;
            }

            if (first && OnSend is { } edit) await edit();
            return DeliveryOutcome.Delivered;
        }
    }
}
