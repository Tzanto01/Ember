using System.Security.Cryptography;
using Ember.Bot.Jobs;
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

/// <summary>
/// While the Discord client isn't logged in, jobs leave Discord DMs, the one-time Discord ask and notices waiting instead of using them
/// up, and still deliver by web push where that is the only other destination.
/// </summary>
[Collection("Integration")]
public class DiscordLoggedOutTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static async Task<(IntegrationTestHost Host, FakePushService Push, FakeTimeProvider Time)> CreateAsync()
    {
        var push = new FakePushService();
        var time = new FakeTimeProvider(Now);
        var (publicKey, privateKey) = NewKeyPair();
        var host = await IntegrationTestHost.CreateAsync(start: false, time: time,
            args: [$"--WebPush:PublicKey={publicKey}", $"--WebPush:PrivateKey={privateKey}", "--WebPush:Subject=mailto:test@example.test"],
            configureServices: services => services.AddHttpClient(WebPushSender.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => push));
        host.Discord.IsLoggedIn = false;
        return (host, push, time);
    }

    private static Ember.Core.Models.PushSubscription Subscription(long id) => new()
    {
        Endpoint = $"https://fcm.googleapis.com/send/u{id}", P256dh = NewKeyPair().Public, Auth = Base64Url(RandomNumberGenerator.GetBytes(16)),
    };

    private static User DiscordUser(long id, bool push = false, bool? discordReminders = true)
    {
        var user = new User
        {
            Id = TestUsers.Id(id), Logins = TestUsers.Discord(id), DiscordReminders = discordReminders, Timezone = "UTC",
            HealthDataConsentAt = Now.UtcDateTime, ConsentPolicyVersion = ConsentService.CurrentConsentPolicyVersion,
        };
        if (push) user.PushSubscriptions.Add(Subscription(id));
        return user;
    }

    private static Habit DueHabit(int id, long user) => new()
    {
        Id = id, UserId = TestUsers.Id(user), Name = $"Habit {id}", ReminderTime = new TimeOnly(8, 0), NextReminderUtc = Now.UtcDateTime,
        CreatedAt = Now.UtcDateTime.AddDays(-30),
    };

    private static Task SeedAsync(IntegrationTestHost host, IEnumerable<User> users, params Habit[] habits)
        => host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.Users.AddRange(users);
            db.Habits.AddRange(habits);
            await db.SaveChangesAsync();
        });

    private static Task RunDispatchAsync(IntegrationTestHost host)
        => ActivatorUtilities.CreateInstance<ReminderDispatchJob>(host.Host.Services).RunAsync();

    private static Task<T> DbAsync<T>(IntegrationTestHost host, Func<EmberDbContext, Task<T>> query)
        => host.WithScopeAsync(sp => query(sp.GetRequiredService<EmberDbContext>()));

    [Fact]
    public async Task A_reminder_only_for_discord_waits_without_counting_a_failure_and_goes_out_once_the_login_succeeds()
    {
        var (host, push, time) = await CreateAsync();
        await using var hostScope = host;
        await SeedAsync(host, [DiscordUser(10)], DueHabit(1, 10));

        for (var run = 0; run < ReminderDispatchJob.MaxConsecutiveDmFailures + 1; run++)
        {
            await RunDispatchAsync(host);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        host.Discord.Messages.Should().BeEmpty();
        push.Requests.Should().BeEmpty();
        (await DbAsync(host, db => db.Habits.Where(h => h.Id == 1).Select(h => h.NextReminderUtc).SingleAsync())).Should().Be(Now.UtcDateTime);
        var user = await DbAsync(host, db => db.Users.AsNoTracking().SingleAsync());
        (user.DmFailureCount, user.DmRemindersPausedAt).Should().Be((0, (DateTime?)null));

        host.Discord.IsLoggedIn = true;
        await RunDispatchAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.Text.Should().Contain("Habit 1");
        (await DbAsync(host, db => db.Habits.Where(h => h.Id == 1).Select(h => h.NextReminderUtc).SingleAsync())).Should().BeAfter(time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task A_reminder_with_web_push_too_still_goes_out_by_web_push_without_counting_a_discord_failure()
    {
        var (host, push, _) = await CreateAsync();
        await using var hostScope = host;
        await SeedAsync(host, [DiscordUser(10, push: true)], DueHabit(1, 10));

        await RunDispatchAsync(host);

        host.Discord.Messages.Should().BeEmpty();
        push.Requests.Should().ContainSingle();
        (await DbAsync(host, db => db.Habits.Where(h => h.Id == 1).Select(h => h.NextReminderUtc).SingleAsync())).Should().BeAfter(Now.UtcDateTime);
        (await DbAsync(host, db => db.Users.Select(u => u.DmFailureCount).SingleAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Open_reminder_messages_wait_to_be_closed_until_the_login_succeeds()
    {
        var (host, _, time) = await CreateAsync();
        await using var hostScope = host;
        await SeedAsync(host, [DiscordUser(10)], DueHabit(1, 10));
        host.Discord.IsLoggedIn = true;
        await RunDispatchAsync(host);
        host.Discord.Messages.Should().ContainSingle();
        await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().CheckInAsync(TestUsers.Id(10), 1, DateOnly.FromDateTime(Now.UtcDateTime), true));

        host.Discord.IsLoggedIn = false;
        time.Advance(TimeSpan.FromMinutes(1));
        await RunDispatchAsync(host);
        host.Discord.Changes.Should().BeEmpty();
        (await DbAsync(host, db => db.ReminderMessages.CountAsync())).Should().Be(1);

        host.Discord.IsLoggedIn = true;
        await RunDispatchAsync(host);
        host.Discord.Changes.Should().ContainSingle();
        (await DbAsync(host, db => db.ReminderMessages.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task The_one_time_discord_ask_waits_until_the_login_succeeds()
    {
        var (host, _, _) = await CreateAsync();
        await using var hostScope = host;
        var user = DiscordUser(10, discordReminders: null);
        user.Habits.Add(new Habit { Name = "Stretch", ReminderTimes = [new TimeOnly(9, 0)] });
        await SeedAsync(host, [user]);
        var ask = ActivatorUtilities.CreateInstance<DiscordReminderAskJob>(host.Host.Services);

        await ask.RunAsync();
        host.Discord.Messages.Should().BeEmpty();
        (await DbAsync(host, db => db.Users.Select(u => u.DiscordRemindersAskedAt).SingleAsync())).Should().BeNull();

        host.Discord.IsLoggedIn = true;
        await ask.RunAsync();
        host.Discord.Messages.Should().ContainSingle().Which.UserId.Should().Be(10UL);
    }

    [Fact]
    public async Task A_fireside_notice_for_a_discord_receiver_is_given_back_and_delivered_once_the_login_succeeds()
    {
        var (host, push, _) = await CreateAsync();
        await using var hostScope = host;
        var receiver = DiscordUser(10, push: true);
        (receiver.FiresideEnabled, receiver.FiresideName, receiver.FiresideNotify) = (true, "Rae", true);
        var sender = DiscordUser(20);
        (sender.FiresideEnabled, sender.FiresideName) = (true, "Sam");
        await SeedAsync(host, [receiver, sender]);
        var link = await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.FiresideLinks.Add(new FiresideLink { OwnerId = TestUsers.Id(10), FriendId = TestUsers.Id(20), KnownName = "Sam", CreatedAt = Now.UtcDateTime });
            var back = new FiresideLink { OwnerId = TestUsers.Id(20), FriendId = TestUsers.Id(10), KnownName = "Rae", CreatedAt = Now.UtcDateTime };
            db.FiresideLinks.Add(back);
            await db.SaveChangesAsync();
            return back.Id;
        });
        await host.WithScopeAsync(sp => sp.GetRequiredService<FiresideService>().SendWarmthAsync(TestUsers.Id(20), link, "thinking"));
        var job = ActivatorUtilities.CreateInstance<FiresideNotifyJob>(host.Host.Services);

        await job.RunAsync();
        host.Discord.Messages.Should().BeEmpty();
        push.Requests.Should().BeEmpty();
        (await DbAsync(host, db => db.Warmth.Select(w => w.NotifyPending).SingleAsync())).Should().BeTrue();

        host.Discord.IsLoggedIn = true;
        await job.RunAsync();
        await job.RunAsync();
        host.Discord.Messages.Should().ContainSingle().Which.UserId.Should().Be(10UL);
        push.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task The_deletion_reminder_for_a_discord_user_is_given_back_and_sent_once_the_login_succeeds()
    {
        var (host, push, time) = await CreateAsync();
        await using var hostScope = host;
        var user = DiscordUser(10, push: true);
        user.Habits.Add(new Habit { Name = "Take meds", CreatedAt = Now.UtcDateTime.AddDays(-60) });
        await SeedAsync(host, [user]);
        (await host.WithScopeAsync(sp => sp.GetRequiredService<ConsentService>().WithdrawHealthDataConsentAsync(TestUsers.Id(10)))).Should().BeTrue();
        var job = ActivatorUtilities.CreateInstance<HealthDataDeletionJob>(host.Host.Services);
        time.Advance(TimeSpan.FromDays(27));

        await job.RunAsync();
        host.Discord.Messages.Should().BeEmpty();
        push.Requests.Should().BeEmpty();
        (await DbAsync(host, db => db.Users.Select(u => u.DeletionReminderSentAt).SingleAsync())).Should().BeNull();

        host.Discord.IsLoggedIn = true;
        time.Advance(TimeSpan.FromDays(1));
        await job.RunAsync();
        host.Discord.Messages.Should().ContainSingle().Which.UserId.Should().Be(10UL);
        push.Requests.Should().ContainSingle();
    }
}
