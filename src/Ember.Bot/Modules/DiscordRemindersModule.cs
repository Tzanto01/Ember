using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot.Discord.CustomIds;
using Ember.Bot.Discord.Messages;
using Ember.Core.Localization;
using Ember.Core.Services;

namespace Ember.Bot.Modules;

/// <summary>
/// The answer to "may your reminders come here by Discord DM?" (<see cref="Jobs.DiscordReminderAskJob"/>); the latest answer wins.
/// Custom ids: <see cref="DiscordReminderIds"/>.
/// under every reminder DM. <c>/reminders off|on</c> sets the same answer from a command.
/// </summary>
public class DiscordRemindersModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly HabitService _habits;
    private readonly ReminderService _reminders;

    public DiscordRemindersModule(HabitService habits, ReminderService reminders)
    {
        _habits = habits;
        _reminders = reminders;
    }

    /// <summary>Sets Discord reminders from a command, creating the account as any bot command does.</summary>
    internal static async Task SetFromCommandAsync(HabitService habits, ReminderService reminders, ulong discordUserId, bool on, string? language)
    {
        var userId = await habits.ResolveOrCreateDiscordUserAsync(discordUserId, language);
        await reminders.SetDiscordRemindersAsync(userId, on);
    }

    [SlashCommand("reminders", "Turn reminder DMs from Ember off or on")]
    public async Task RemindersAsync([Summary("state", "Off or on")] DiscordRemindersState state)
    {
        var on = state == DiscordRemindersState.On;
        await SetFromCommandAsync(_habits, _reminders, Context.User.Id, on, Languages.FromLocale(Context.Interaction.UserLocale));
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        await RespondAsync(on ? t.Get("discordreminders.yes") : t.Get("discordreminders.command.off"), ephemeral: true);
    }

    /// <summary>Records the owner's answer only when the person clicking is that owner. Returns whether the clicker is the owner.</summary>
    internal static async Task<bool> TryAnswerAsync(HabitService habits, ReminderService reminders, string ownerIdStr, ulong clickerId, bool yes)
    {
        if (!ulong.TryParse(ownerIdStr, out var ownerId) || ownerId != clickerId) return false;
        await reminders.SetDiscordRemindersAsync(await habits.FindDiscordUserAsync(clickerId), yes);
        return true;
    }

    [ComponentInteraction(DiscordReminderIds.YesPattern)]
    public Task OnYesAsync(string ownerIdStr) => AnswerAsync(ownerIdStr, yes: true);

    [ComponentInteraction(DiscordReminderIds.NoPattern)]
    public Task OnNoAsync(string ownerIdStr) => AnswerAsync(ownerIdStr, yes: false);

    [ComponentInteraction(DiscordReminderIds.StopPattern)]
    public async Task OnStopAsync(string ownerIdStr)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!await TryAnswerAsync(_habits, _reminders, ownerIdStr, Context.User.Id, yes: false))
        {
            await RespondAsync(t.Get("common.not-yours"), ephemeral: true);
            return;
        }

        var component = (SocketMessageComponent)Context.Interaction;
        await component.UpdateAsync(m =>
        {
            m.Content = $"{component.Message.Content}\n\n{t.Get("discordreminders.command.off")}";
            m.Components = DiscordRemindersAsk.AfterStop(component.Message.Components, t, Context.User.Id);
        });
    }

    private async Task AnswerAsync(string ownerIdStr, bool yes)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!await TryAnswerAsync(_habits, _reminders, ownerIdStr, Context.User.Id, yes))
        {
            await RespondAsync(t.Get("common.not-yours"), ephemeral: true);
            return;
        }

        await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
        {
            m.Content = t.Get(yes ? "discordreminders.yes" : "discordreminders.no");
            m.Components = yes ? new ComponentBuilder().Build() : DiscordRemindersAsk.TurnOnButton(t, Context.User.Id);
        });
    }
}

/// <summary>The <c>/reminders</c> choices: Discord reminder DMs off or on.</summary>
public enum DiscordRemindersState
{
    [ChoiceDisplay("off")] Off,
    [ChoiceDisplay("on")] On,
}
