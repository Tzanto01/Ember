using System.Collections.Concurrent;
using System.Reflection;
using Discord;
using Discord.Interactions;
using Ember.Bot;
using Ember.Bot.Modules;
using Ember.Core.Data;
using Ember.Core.Models;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ember.Tests;

/// <summary>
/// The production host (gateway included, initialized and started like <c>Program.cs</c>) shows the paused-reminders notice
/// and the consent prompt as ephemeral follow-ups after any successful interaction.
/// </summary>
[Collection("Integration")]
public class InteractionNoticeWiringTests
{
    private const ulong DiscordId = 930;

    [Fact]
    public async Task After_an_interaction_a_paused_user_without_consent_gets_the_dm_pause_notice_and_the_consent_prompt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var host = AppHost.CreateHostBuilder([], new EmberHostOptions
        {
            ConfigureDbContext = (options, _) => options.UseSqlite(connection),
        }).Build();

        await AppHost.InitializeAsync(host);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmberDbContext>();
            db.Users.Add(new User
            {
                Id = TestUsers.Id(DiscordId),
                Timezone = "UTC",
                Logins = TestUsers.Discord(DiscordId),
                DmRemindersPausedAt = DateTime.UtcNow,
                Habits = [new Habit { Name = "Stretch" }],
            });
            await db.SaveChangesAsync();
        }

        var bot = host.Services.GetServices<IHostedService>().OfType<BotService>().Single();
        await bot.Invoking(b => b.StartAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>("no BotToken is configured, so the bot stops before logging in to Discord");

        var interactions = host.Services.GetRequiredService<InteractionService>();
        await interactions.AddModuleAsync<ProbeModule>(host.Services);
        var followups = new ConcurrentQueue<FakeInteraction.Followup>();
        var context = new FakeContext(FakeInteraction.Create(DiscordId, ProbeModule.CustomId, followups));

        var result = await interactions.ExecuteCommandAsync(context, host.Services);
        result.IsSuccess.Should().BeTrue();

        var deadline = Environment.TickCount64 + 30_000;
        while (followups.Count < 2 && Environment.TickCount64 < deadline)
            await Task.Delay(20);

        var t = BotTexts.English;
        followups.Should().HaveCount(2);
        followups.Should().ContainSingle(f => f.Text == DmRetryModule.NoticeText(t)).Which.Ephemeral.Should().BeTrue();
        followups.Should().ContainSingle(f => f.Embed != null && f.Embed.Description == ConsentModule.PromptEmbed(t, host.Services.GetRequiredService<IConfiguration>()).Description)
            .Which.Ephemeral.Should().BeTrue();
    }

    /// <summary>A component handler that succeeds, standing in for any Ember button the user presses.</summary>
    public sealed class ProbeModule : InteractionModuleBase<IInteractionContext>
    {
        public const string CustomId = "test:notice-probe";

        [ComponentInteraction(CustomId)]
        public Task Probe() => RespondAsync();
    }

    private sealed class FakeContext(IDiscordInteraction interaction) : IInteractionContext
    {
        public IDiscordClient Client => null!;
        public IGuild Guild => null!;
        public IMessageChannel Channel => null!;
        public IUser User => interaction.User;
        public IDiscordInteraction Interaction => interaction;
    }

    /// <summary>An answered button press by <c>userId</c> that records its follow-ups; everything else returns defaults.</summary>
    public class FakeInteraction : DispatchProxy
    {
        public sealed record Followup(string? Text, Embed? Embed, MessageComponent? Components, bool Ephemeral);

        private Dictionary<string, object?> _values = [];
        private ConcurrentQueue<Followup> _followups = new();

        public static IComponentInteraction Create(ulong userId, string customId, ConcurrentQueue<Followup> followups)
        {
            var user = Create<IUser, FakeInteraction>();
            ((FakeInteraction)(object)user)._values = new() { ["get_Id"] = userId };
            var data = Create<IComponentInteractionData, FakeInteraction>();
            ((FakeInteraction)(object)data)._values = new() { ["get_CustomId"] = customId, ["get_Type"] = ComponentType.Button };
            var interaction = Create<IComponentInteraction, FakeInteraction>();
            var fake = (FakeInteraction)(object)interaction;
            fake._followups = followups;
            fake._values = new()
            {
                ["get_User"] = user,
                ["get_Data"] = data,
                ["get_Type"] = InteractionType.MessageComponent,
                ["get_UserLocale"] = "en-US",
                ["get_HasResponded"] = true,
            };
            return interaction;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            if (method.Name == nameof(IDiscordInteraction.FollowupAsync))
            {
                var parameters = method.GetParameters();
                object? Arg(string name) => args![Array.FindIndex(parameters, p => p.Name == name)];
                _followups.Enqueue(new Followup((string?)Arg("text"), (Embed?)Arg("embed"), (MessageComponent?)Arg("components"), (bool)Arg("ephemeral")!));
                return Task.FromResult<IUserMessage>(null!);
            }

            if (_values.TryGetValue(method.Name, out var value))
                return value;

            var type = method.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, [null]);
            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }
}
