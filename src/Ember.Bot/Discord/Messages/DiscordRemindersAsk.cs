using Discord;
using Ember.Bot.Discord.CustomIds;
using Ember.Core.Localization;

namespace Ember.Bot.Discord.Messages;

/// <summary>The "may your reminders come here by Discord DM?" ask, its "Turn them on" and "Stop Discord reminders" buttons and the note that they are off (<see cref="DiscordReminderIds"/>).</summary>
internal static class DiscordRemindersAsk
{
    internal static string AskText(Texts t) => t.Get("discordreminders.ask");

    /// <summary>The note, on its own line, that a reminder set from Discord won't come by DM because the user said no.</summary>
    internal static string OffText(Texts t) => "\n" + t.Get("discordreminders.off");

    internal static MessageComponent AskButtons(Texts t, ulong userId) => new ComponentBuilder()
        .WithButton(t.Get("discordreminders.ask.yes"), DiscordReminderIds.Yes(userId), ButtonStyle.Primary)
        .WithButton(t.Get("discordreminders.ask.no"), DiscordReminderIds.No(userId), ButtonStyle.Secondary)
        .Build();

    internal static MessageComponent TurnOnButton(Texts t, ulong userId) => new ComponentBuilder()
        .WithButton(TurnOn(t, userId))
        .Build();

    private static ButtonBuilder TurnOn(Texts t, ulong userId) => new ButtonBuilder()
        .WithLabel(t.Get("discordreminders.turn-on"))
        .WithCustomId(DiscordReminderIds.Yes(userId))
        .WithStyle(ButtonStyle.Primary);

    /// <summary>The "Stop Discord reminders" button under every reminder DM.</summary>
    internal static ButtonBuilder StopButton(Texts t, ulong userId) => new ButtonBuilder()
        .WithLabel(t.Get("discordreminders.stop"))
        .WithCustomId(DiscordReminderIds.Stop(userId))
        .WithStyle(ButtonStyle.Secondary);

    /// <summary>A reminder's buttons after its stop button was used: every other button stays in its row, the stop button becomes "Turn them on".</summary>
    internal static MessageComponent AfterStop(IEnumerable<IMessageComponent> components, Texts t, ulong userId)
    {
        var builder = new ComponentBuilder();
        foreach (var (row, rowIndex) in components.OfType<ActionRowComponent>().Select((r, i) => (r, i)))
            foreach (var button in row.Components.OfType<ButtonComponent>())
                builder.WithButton(button.CustomId?.StartsWith(DiscordReminderIds.StopPrefix, StringComparison.Ordinal) == true
                    ? TurnOn(t, userId)
                    : button.ToBuilder(), rowIndex);
        return builder.Build();
    }
}
