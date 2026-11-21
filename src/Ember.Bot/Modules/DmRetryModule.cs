using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot.Discord.CustomIds;
using Ember.Bot.Services;
using Ember.Core.Services;

namespace Ember.Bot.Modules;

/// <summary>
/// Lets a user whose DM reminders were paused (Discord refused the DMs) check that Ember can
/// reach them again. Custom id: <see cref="DmRetryIds"/>.
/// </summary>
public class DmRetryModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly HabitService _habits;
    private readonly ReminderService _reminders;
    private readonly IDiscordDmSender _discord;

    public DmRetryModule(HabitService habits, ReminderService reminders, IDiscordDmSender discord)
    {
        _habits = habits;
        _reminders = reminders;
        _discord = discord;
    }

    [ComponentInteraction(DmRetryIds.RetryPattern)]
    public async Task OnRetryAsync(string userIdStr)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!ulong.TryParse(userIdStr, out var userId) || userId != Context.User.Id)
        {
            await RespondAsync(t.Get("common.not-yours"), ephemeral: true);
            return;
        }

        var outcome = await _discord.SendMessageAsync(userId, t.Get("dmretry.check"));

        if (outcome != DeliveryOutcome.Delivered)
        {
            await RespondAsync(t.Get("dmretry.still-blocked"), ephemeral: true);
            return;
        }

        await _reminders.ResumeDmRemindersAsync(await _habits.FindDiscordUserAsync(userId));
        await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
        {
            m.Content = t.Get("dmretry.worked");
            m.Components = new ComponentBuilder().Build();
        });
    }
}
