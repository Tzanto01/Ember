using System.Net;
using System.Security.Cryptography;
using Discord;
using Ember.Bot.Jobs;
using Ember.Bot.Services;
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
public class FiresideNotifyTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static async Task<(IntegrationTestHost Host, FakePushService Push)> CreateAsync(bool withVapid = true)
    {
        var push = new FakePushService();
        var (publicKey, privateKey) = NewKeyPair();
        var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now),
            args: withVapid ? [$"--WebPush:PublicKey={publicKey}", $"--WebPush:PrivateKey={privateKey}", "--WebPush:Subject=mailto:test@example.test"] : null,
            configureServices: services => services.AddHttpClient(WebPushSender.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => push));
        return (host, push);
    }

    private static string Endpoint(long id) => $"https://fcm.googleapis.com/send/u{id}";

    private static async Task SeedAsync(IntegrationTestHost host, long id, string name, bool notify = false, bool push = false)
    {
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.Users.Add(new User
            {
                Id = TestUsers.Id(id),
                Timezone = "UTC",
                DiscordReminders = true,
                Logins = TestUsers.Discord(id),
                FiresideEnabled = true,
                FiresideName = name,
                FiresideNotify = notify,
            });
            if (push)
            {
                var (clientKey, _) = NewKeyPair();
                db.PushSubscriptions.Add(new Ember.Core.Models.PushSubscription
                {
                    UserId = TestUsers.Id(id),
                    Endpoint = Endpoint(id),
                    P256dh = clientKey,
                    Auth = Base64Url(RandomNumberGenerator.GetBytes(16)),
                });
            }
            await db.SaveChangesAsync();
        });
    }

    private static Task LinkAsync(IntegrationTestHost host, long a, string aName, long b, string bName, bool aMutesB = false)
        => host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.FiresideLinks.Add(new FiresideLink { OwnerId = TestUsers.Id(a), FriendId = TestUsers.Id(b), KnownName = bName, Muted = aMutesB, CreatedAt = Now.UtcDateTime });
            db.FiresideLinks.Add(new FiresideLink { OwnerId = TestUsers.Id(b), FriendId = TestUsers.Id(a), KnownName = aName, CreatedAt = Now.UtcDateTime });
            await db.SaveChangesAsync();
        });

    private static Task<FiresideSendResult> SendAsync(IntegrationTestHost host, long from, long to)
        => host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var link = await db.FiresideLinks.Where(l => l.OwnerId == TestUsers.Id(from) && l.FriendId == TestUsers.Id(to)).Select(l => l.Id).SingleAsync();
            return await sp.GetRequiredService<FiresideService>().SendWarmthAsync(TestUsers.Id(from), link, "thinking");
        });

    private static Task RunJobAsync(IntegrationTestHost host)
        => ActivatorUtilities.CreateInstance<FiresideNotifyJob>(host.Host.Services).RunAsync();

    private static IEnumerable<string?> ButtonIds(MessageComponent? components)
        => components!.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().Select(b => b.CustomId);

    private static Task<int> WarmthIdAsync(IntegrationTestHost host, long receiver)
        => host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Warmth.Where(w => w.ToUserId == TestUsers.Id(receiver)).Select(w => w.Id).SingleAsync());

    private static Task<int> LinkIdAsync(IntegrationTestHost host, long owner, long friend)
        => host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().FiresideLinks
            .Where(l => l.OwnerId == TestUsers.Id(owner) && l.FriendId == TestUsers.Id(friend)).Select(l => l.Id).SingleAsync());

    [Fact]
    public async Task New_warmth_reaches_a_receiver_with_notifications_on_by_discord_dm_and_web_push_exactly_once()
    {
        var (host, push) = await CreateAsync();
        await using var hostScope = host;
        await SeedAsync(host, 10, "Rae", notify: true, push: true);
        await SeedAsync(host, 20, "Sam");
        await LinkAsync(host, 10, "Rae", 20, "Sam");
        await SendAsync(host, 20, 10);

        await RunJobAsync(host);
        await RunJobAsync(host);

        var dm = host.Discord.Messages.Should().ContainSingle().Subject;
        dm.UserId.Should().Be(10UL);
        dm.Embed!.Title.Should().Be("Thinking of you");
        dm.Embed.Description.Should().Contain("Sam");
        var receiverLink = await LinkIdAsync(host, 10, 20);
        ButtonIds(dm.Components).Should().Equal($"fireside:back:{receiverLink}", $"fireside:ok:{receiverLink}:{await WarmthIdAsync(host, 10)}");
        push.Endpoints.Should().Equal(Endpoint(10));
    }

    [Fact]
    public async Task Without_web_push_keys_new_warmth_still_reaches_the_receiver_by_discord_dm_and_pushes_nothing()
    {
        var (host, push) = await CreateAsync(withVapid: false);
        await using var hostScope = host;
        await SeedAsync(host, 10, "Rae", notify: true, push: true);
        await SeedAsync(host, 20, "Sam");
        await LinkAsync(host, 10, "Rae", 20, "Sam");
        await SendAsync(host, 20, 10);

        await RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.UserId.Should().Be(10UL);
        push.Endpoints.Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_is_sent_when_notifications_are_off_or_the_warmth_is_muted_dismissed_or_its_link_is_gone()
    {
        var (host, push) = await CreateAsync();
        await using var hostScope = host;
        await SeedAsync(host, 10, "Rae", notify: true, push: true);
        await SeedAsync(host, 11, "Off", notify: false, push: true);
        foreach (var (id, name) in new[] { (20L, "Visible"), (21L, "Muted"), (22L, "Dismissed"), (23L, "Removed"), (24L, "ToOff") })
            await SeedAsync(host, id, name);
        await LinkAsync(host, 10, "Rae", 20, "Visible");
        await LinkAsync(host, 10, "Rae", 21, "Muted", aMutesB: true);
        await LinkAsync(host, 10, "Rae", 22, "Dismissed");
        await LinkAsync(host, 10, "Rae", 23, "Removed");
        await LinkAsync(host, 11, "Off", 24, "ToOff");
        foreach (var sender in new[] { 20L, 21, 22, 23 }) await SendAsync(host, sender, 10);
        await SendAsync(host, 24, 11);
        await host.WithScopeAsync(async sp =>
        {
            var fireside = sp.GetRequiredService<FiresideService>();
            var warmth = await sp.GetRequiredService<EmberDbContext>().Warmth.Where(w => w.FromUserId == TestUsers.Id(22)).Select(w => w.Id).SingleAsync();
            await fireside.DismissWarmthAsync(TestUsers.Id(10), warmth);
            await fireside.RemoveFriendAsync(TestUsers.Id(10), await LinkIdAsync(host, 10, 23), false);
        });

        await RunJobAsync(host);

        var dm = host.Discord.Messages.Should().ContainSingle().Subject;
        dm.Embed!.Description.Should().Contain("Visible");
        push.Endpoints.Should().Equal(Endpoint(10));
    }

    [Fact]
    public async Task The_senders_name_is_escaped_so_markdown_in_it_is_not_rendered_as_a_link()
    {
        var (host, _) = await CreateAsync();
        await using var hostScope = host;
        const string markdown = "[x](https://example.test)";
        await SeedAsync(host, 10, "Rae", notify: true);
        await SeedAsync(host, 20, markdown);
        await LinkAsync(host, 10, "Rae", 20, markdown);
        await SendAsync(host, 20, 10);

        await RunJobAsync(host);

        host.Discord.Messages.Should().ContainSingle().Which.Embed!.Description.Should()
            .NotContain(markdown).And.NotContain("https://");
    }

    [Fact]
    public async Task A_joined_notice_goes_out_once_to_an_owner_with_notifications_on_with_only_the_okay_button()
    {
        var (host, _) = await CreateAsync();
        await using var hostScope = host;
        await SeedAsync(host, 10, "Rae", notify: true);
        await SeedAsync(host, 11, "Quiet", notify: false);
        await SeedAsync(host, 20, "Joiner");
        foreach (var owner in new[] { 10L, 11 })
        {
            var code = await host.WithScopeAsync(sp => sp.GetRequiredService<FiresideService>().CreateCodeAsync(TestUsers.Id(owner)));
            await host.WithScopeAsync(sp => sp.GetRequiredService<FiresideService>().JoinAsync(TestUsers.Id(20), code!.Code));
        }

        await RunJobAsync(host);
        await RunJobAsync(host);

        var dm = host.Discord.Messages.Should().ContainSingle().Subject;
        dm.UserId.Should().Be(10UL);
        dm.Text.Should().Contain("Joiner");
        ButtonIds(dm.Components).Should().Equal($"fireside:ok:{await LinkIdAsync(host, 10, 20)}");
    }
}
