using Discord;
using Ember.Bot.Discord.CustomIds;
using Ember.Core.Localization;

namespace Ember.Bot.Discord.Messages;

/// <summary>The notice that DM reminders were paused, with its "Try again" button (<see cref="DmRetryIds"/>).</summary>
internal static class DmRetryNotice
{
    internal static string NoticeText(Texts t) => t.Get("dmretry.notice");

    internal static MessageComponent RetryButton(Texts t, ulong userId) => new ComponentBuilder()
        .WithButton(t.Get("dmretry.try-again"), DmRetryIds.Retry(userId), ButtonStyle.Primary)
        .Build();
}
