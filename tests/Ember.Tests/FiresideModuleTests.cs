using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot;
using Ember.Bot.Modules;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Tests;

[Collection("Integration")]
public class FiresideModuleTests
{
    private sealed class RecordingModule(HabitService habits, FiresideService fireside) : FiresideModule(habits, fireside)
    {
        public List<string?> Replies { get; } = [];

        protected override Task<IUserMessage> FollowupAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
            AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
            PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
        {
            Replies.Add(text);
            return Task.FromResult<IUserMessage>(null!);
        }

        protected override Task DeferAsync(bool ephemeral = false, RequestOptions? options = null) => Task.CompletedTask;

        public List<Optional<MessageComponent>> Edits { get; } = [];

        protected override Task<IUserMessage> ModifyOriginalResponseAsync(Action<MessageProperties> func, RequestOptions? options = null)
        {
            var properties = new MessageProperties();
            func(properties);
            Edits.Add(properties.Components);
            return Task.FromResult<IUserMessage>(null!);
        }
    }

    private static RecordingModule ModuleFor(IServiceProvider sp, ulong discordId)
    {
        var module = new RecordingModule(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<FiresideService>());
        ((IInteractionModuleBase)module).SetContext(BotHandlers.ContextFor<SocketMessageComponent>(discordId));
        return module;
    }

    private static async Task SeedAsync(IntegrationTestHost host, long id, string name, bool fireside)
        => await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.Users.Add(new User { Id = TestUsers.Id(id), Timezone = "UTC", Logins = TestUsers.Discord(id), FiresideEnabled = fireside, FiresideName = name });
            await db.SaveChangesAsync();
        });

    [Fact]
    public async Task The_fireside_commands_answer_the_off_text_when_fireside_is_off_and_for_unknown_users()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);
        await SeedAsync(host, 10, "Rae", fireside: false);
        var off = BotTexts.English.Get("fireside.off");

        var replies = await host.WithScopeAsync(async sp =>
        {
            var seen = new List<string?>();
            foreach (var discordId in new ulong[] { 10, 999 })
            {
                var module = ModuleFor(sp, discordId);
                await module.CodeAsync();
                await module.JoinAsync("ABCD EFGH");
                await module.SendAsync();
                seen.AddRange(module.Replies);
            }
            return seen;
        });

        replies.Should().HaveCount(6).And.OnlyContain(r => r == off);
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().FiresideCodes.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task The_send_some_back_button_of_another_users_link_answers_the_generic_unavailable_text_and_do_nothing()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);
        await SeedAsync(host, 10, "Rae", fireside: true);
        await SeedAsync(host, 20, "Sam", fireside: true);
        await SeedAsync(host, 30, "Eve", fireside: true);
        var raesLink = await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.FiresideLinks.Add(new FiresideLink { OwnerId = TestUsers.Id(10), FriendId = TestUsers.Id(20), KnownName = "Sam", CreatedAt = DateTime.UtcNow });
            db.FiresideLinks.Add(new FiresideLink { OwnerId = TestUsers.Id(20), FriendId = TestUsers.Id(10), KnownName = "Rae", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return await db.FiresideLinks.Where(l => l.OwnerId == TestUsers.Id(10)).Select(l => l.Id).SingleAsync();
        });

        var replies = await host.WithScopeAsync(async sp =>
        {
            var module = ModuleFor(sp, 30);
            await module.SendBackAsync(raesLink.ToString());
            await module.SendBackAsync("not-a-number");
            return module.Replies;
        });

        replies.Should().HaveCount(2).And.OnlyContain(r => r == BotTexts.English.Get("fireside.unavailable"));
    }

    [Fact]
    public async Task The_owner_of_a_link_gets_the_message_picker_from_send_some_back()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);
        await SeedAsync(host, 10, "Rae", fireside: true);
        await SeedAsync(host, 20, "Sam", fireside: true);
        var link = await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.FiresideLinks.Add(new FiresideLink { OwnerId = TestUsers.Id(10), FriendId = TestUsers.Id(20), KnownName = "Sam", CreatedAt = DateTime.UtcNow });
            db.FiresideLinks.Add(new FiresideLink { OwnerId = TestUsers.Id(20), FriendId = TestUsers.Id(10), KnownName = "Rae", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return await db.FiresideLinks.Where(l => l.OwnerId == TestUsers.Id(10)).Select(l => l.Id).SingleAsync();
        });

        var replies = await host.WithScopeAsync(async sp =>
        {
            var module = ModuleFor(sp, 10);
            await module.SendBackAsync(link.ToString());
            return module.Replies;
        });

        replies.Should().ContainSingle().Which.Should().Be(BotTexts.English.Get("fireside.pick-message"));
    }

    [Fact]
    public async Task Okay_on_a_warmth_dm_dismisses_that_warmth_only_for_its_receiver_and_always_removes_the_buttons()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);
        await SeedAsync(host, 10, "Rae", fireside: true);
        await SeedAsync(host, 20, "Sam", fireside: true);
        await SeedAsync(host, 30, "Eve", fireside: true);
        var (raesLink, warmthId) = await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.FiresideLinks.Add(new FiresideLink { OwnerId = TestUsers.Id(10), FriendId = TestUsers.Id(20), KnownName = "Sam", CreatedAt = DateTime.UtcNow });
            db.FiresideLinks.Add(new FiresideLink { OwnerId = TestUsers.Id(20), FriendId = TestUsers.Id(10), KnownName = "Rae", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var samsLink = await db.FiresideLinks.Where(l => l.OwnerId == TestUsers.Id(20)).Select(l => l.Id).SingleAsync();
            await sp.GetRequiredService<FiresideService>().SendWarmthAsync(TestUsers.Id(20), samsLink, "thinking");
            return (await db.FiresideLinks.Where(l => l.OwnerId == TestUsers.Id(10)).Select(l => l.Id).SingleAsync(),
                await db.Warmth.Where(w => w.ToUserId == TestUsers.Id(10)).Select(w => w.Id).SingleAsync());
        });
        Task<bool> DismissedAsync() => host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Warmth
            .Where(w => w.Id == warmthId).Select(w => w.DismissedAtUtc != null).SingleAsync());

        var eve = await host.WithScopeAsync(async sp =>
        {
            var module = ModuleFor(sp, 30);
            await module.OkayAsync($"{raesLink}:{warmthId}");
            await module.OkayAsync($"{raesLink}:not-a-number");
            await module.OkayAsync(raesLink.ToString());
            return module;
        });
        (await DismissedAsync()).Should().BeFalse();

        var rae = await host.WithScopeAsync(async sp =>
        {
            var module = ModuleFor(sp, 10);
            await module.OkayAsync($"{raesLink}:{warmthId}");
            return module;
        });
        (await DismissedAsync()).Should().BeTrue();

        eve.Edits.Concat(rae.Edits).Should().HaveCount(4).And.OnlyContain(c => c.IsSpecified && c.Value.Components.Count == 0);
        eve.Replies.Concat(rae.Replies).Should().BeEmpty();
    }
}
