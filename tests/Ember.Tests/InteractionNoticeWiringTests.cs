using System.Collections.Concurrent;
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
        var followups = new ConcurrentQueue<FakeFollowup>();
        var context = new FakeInteractionContext(FakeInteraction.Component(DiscordId, ProbeModule.CustomId, followups));

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
}
