using Discord;
using Ember.Bot.Discord.CustomIds;
using Ember.Core.Localization;
using Ember.Core.Services;

namespace Ember.Bot.Discord.Messages;

/// <summary>The one-time question whether to show streaks (<see cref="TipIds"/>), asked after a new habit.</summary>
internal static class StreaksQuestion
{
    /// <summary>
    /// Asks the one-time streaks question as an ephemeral follow-up when <see cref="UserSettingsService.NeedsStreaksQuestionAsync"/>
    /// says the account needs it; the answer comes back as <c>tip:streaks:{on|off}</c>.
    /// </summary>
    internal static async Task AskOnceAsync(IDiscordInteraction interaction, UserSettingsService settings, Guid userId, Texts t)
    {
        if (!await settings.NeedsStreaksQuestionAsync(userId)) return;

        var buttons = new ComponentBuilder()
            .WithButton(t.Get("habit.streaks-tip.show"), TipIds.Streaks(show: true), ButtonStyle.Primary)
            .WithButton(t.Get("habit.streaks-tip.not-for-me"), TipIds.Streaks(show: false), ButtonStyle.Secondary)
            .Build();
        await interaction.FollowupAsync(t.Get("habit.streaks-tip.question"), components: buttons, ephemeral: true);
    }
}
