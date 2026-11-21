using Discord;
using Ember.Bot.Discord.CustomIds;
using Ember.Core.Localization;
using Ember.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Ember.Bot.Discord.Messages;

/// <summary>The health-data consent prompt (<see cref="ConsentIds"/>) and the answer to declining it.</summary>
internal static class ConsentPrompt
{
    internal const string PolicyUrlKey = "Ember:PrivacyPolicyUrl";

    internal static Embed PromptEmbed(Texts t, IConfiguration config)
    {
        var description = t.Get("consent.body");

        if (config[PolicyUrlKey] is { Length: > 0 } url)
            description += "\n\n" + t.Format("consent.policy-link", url);

        return new EmbedBuilder()
            .WithColor(0x5865F2)
            .WithTitle(t.Get("consent.title"))
            .WithDescription(description)
            .Build();
    }

    internal static MessageComponent PromptButtons(Texts t, ulong userId) => new ComponentBuilder()
        .WithButton(t.Get("consent.agree"), ConsentIds.Agree(userId), ButtonStyle.Success)
        .WithButton(t.Get("consent.decline"), ConsentIds.Decline(userId), ButtonStyle.Secondary)
        .Build();

    /// <summary>
    /// The answer to "No thanks": <c>consent.declined</c>, or for a user whose consent was already withdrawn (<paramref name="withdrawn"/>),
    /// <c>privacy.withdraw.done</c> with the deletion day (<see cref="ConsentService.DeletionDueUtc"/>) in <paramref name="tz"/>.
    /// </summary>
    internal static string DeclinedText(Texts t, (DateTime WithdrawnAt, DateTime? ReminderSentAt)? withdrawn, TimeZoneInfo tz)
        => withdrawn is { } w
            ? t.Format("privacy.withdraw.done", DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                    DateTime.SpecifyKind(ConsentService.DeletionDueUtc(w.WithdrawnAt, w.ReminderSentAt), DateTimeKind.Utc), tz))
                .ToString(t.Get("date.full"), t.Culture))
            : t.Get("consent.declined");
}
