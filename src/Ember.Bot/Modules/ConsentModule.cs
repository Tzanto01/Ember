using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot.Discord.CustomIds;
using Ember.Core.Localization;
using Ember.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Ember.Bot.Modules;

/// <summary>
/// Asks for explicit consent to store habit names, check-ins, low days, day notes and moods (GDPR Art. 9(2)(a)).
/// Custom ids: <see cref="ConsentIds"/>.
/// </summary>
public class ConsentModule : InteractionModuleBase<SocketInteractionContext>
{
    internal const string PolicyUrlKey = "Ember:PrivacyPolicyUrl";

    private readonly HabitService _habits;
    private readonly ConsentService _consent;
    private readonly UserSettingsService _settings;

    public ConsentModule(HabitService habits, ConsentService consent, UserSettingsService settings)
    {
        _habits = habits;
        _consent = consent;
        _settings = settings;
    }

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

    /// <summary>Answers the interaction with the consent prompt and notes that it was shown today.</summary>
    internal static async Task RespondWithPromptAsync(IInteractionContext context, IConfiguration config, HabitService habits, ConsentService consent)
    {
        var t = await BotTexts.ForAsync(habits, context.Interaction);
        await context.Interaction.RespondAsync(embed: PromptEmbed(t, config), components: PromptButtons(t, context.User.Id), ephemeral: true);
        await consent.MarkConsentPromptedAsync(await habits.FindDiscordUserAsync(context.User.Id));
    }

    /// <summary>
    /// Applies the owner's answer only when the person clicking is that owner: agreeing records consent, declining holds back the next
    /// automatic prompt (<see cref="ConsentService.ConsentDeclineBackoffDays"/>) and withdraws, so the health data is deleted after
    /// <see cref="ConsentService.WithdrawnDataLifetime"/>. Returns whether the clicker is the owner.
    /// An account created by agreeing gets <paramref name="language"/>.
    /// </summary>
    internal static async Task<bool> TryAnswerAsync(HabitService habits, ConsentService consent, string ownerIdStr, ulong clickerId, bool agreed,
        string? language = null)
    {
        if (!ulong.TryParse(ownerIdStr, out var ownerId) || ownerId != clickerId) return false;
        if (agreed) await consent.GiveHealthDataConsentAsync(await habits.ResolveOrCreateDiscordUserAsync(clickerId, language));
        else await consent.DeclineConsentPromptAsync(await habits.FindDiscordUserAsync(clickerId));
        return true;
    }

    [ComponentInteraction(ConsentIds.AgreePattern)]
    public async Task OnAgreeAsync(string ownerIdStr)
    {
        if (!await TryAnswerAsync(_habits, _consent, ownerIdStr, Context.User.Id, agreed: true, Languages.FromLocale(Context.Interaction.UserLocale)))
        {
            await RespondAsync((await BotTexts.ForAsync(_habits, Context.Interaction)).Get("common.not-yours"), ephemeral: true);
            return;
        }

        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
        {
            m.Content = t.Get("consent.agreed");
            m.Embed = null;
            m.Components = new ComponentBuilder().Build();
        });
    }

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

    [ComponentInteraction(ConsentIds.DeclinePattern)]
    public async Task OnDeclineAsync(string ownerIdStr)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var withdrawn = await _consent.GetConsentWithdrawalAsync(userId);
        if (!await TryAnswerAsync(_habits, _consent, ownerIdStr, Context.User.Id, agreed: false))
        {
            await RespondAsync(t.Get("common.not-yours"), ephemeral: true);
            return;
        }

        var content = DeclinedText(t, withdrawn, await _settings.GetTimeZoneAsync(userId));
        await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
        {
            m.Content = content;
            m.Embed = null;
            m.Components = new ComponentBuilder()
                .WithButton(t.Get("consent.delete-button"), PrivacyIds.DeleteStart(Context.User.Id), ButtonStyle.Secondary)
                .Build();
        });
    }
}
