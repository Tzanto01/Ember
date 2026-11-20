using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot.Discord.CustomIds;
using Ember.Core.Localization;
using Ember.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Ember.Bot.Modules;

[Group("privacy", "Control your privacy settings")]
public class PrivacyModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly HabitService _habits;
    private readonly ConsentService _consent;
    private readonly UserSettingsService _settings;
    private readonly AccountService _accounts;
    private readonly IConfiguration _config;

    public PrivacyModule(HabitService habits, ConsentService consent, UserSettingsService settings, AccountService accounts, IConfiguration config)
    {
        _habits = habits;
        _consent = consent;
        _settings = settings;
        _accounts = accounts;
        _config = config;
    }

    /// <summary>What <see cref="TryDeleteAsync"/> did.</summary>
    internal enum DeleteOutcome
    {
        /// <summary>Someone other than the button's owner clicked; nothing was deleted.</summary>
        NotYours,

        /// <summary>The account has a password or two-step sign-in, so it is deleted only in the web app; nothing was deleted.</summary>
        NeedsWebApp,

        /// <summary>The owner's data is deleted (or there was none).</summary>
        Deleted,
    }

    [SlashCommand("delete", "Permanently delete all your Ember data")]
    public Task DeleteAsync() => RespondWithDeleteConfirmationAsync();

    [ComponentInteraction(PrivacyIds.DeleteStartPattern, ignoreGroupNames: true)]
    public async Task OnStartDeleteAsync(string ownerIdStr)
    {
        if (!IsOwner(ownerIdStr, Context.User.Id))
        {
            await RespondAsync((await BotTexts.ForAsync(_habits, Context.Interaction)).Get("common.not-yours"), ephemeral: true);
            return;
        }

        await RespondWithDeleteConfirmationAsync();
    }

    private async Task RespondWithDeleteConfirmationAsync()
    {
        var userId = Context.User.Id;
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (await _accounts.IsProtectedAccountAsync(await _habits.FindDiscordUserAsync(userId)))
        {
            await RespondAsync(WebAppDeleteText(t, _config), ephemeral: true);
            return;
        }

        var buttons = new ComponentBuilder()
            .WithButton(t.Get("privacy.delete.button"), PrivacyIds.DeleteConfirm(userId), ButtonStyle.Danger)
            .WithButton(t.Get("common.cancel"), PrivacyIds.DeleteCancel(userId), ButtonStyle.Secondary)
            .Build();

        await RespondAsync(
            t.Get("privacy.delete.confirm"),
            components: buttons,
            ephemeral: true);
    }

    [ComponentInteraction(PrivacyIds.DeleteConfirmPattern, ignoreGroupNames: true)]
    public async Task OnConfirmDeleteAsync(string ownerIdStr)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        var outcome = await TryDeleteAsync(_habits, _accounts, ownerIdStr, Context.User.Id);
        if (outcome == DeleteOutcome.NotYours)
        {
            await RespondAsync(t.Get("common.not-yours"), ephemeral: true);
            return;
        }

        await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
        {
            m.Content = outcome == DeleteOutcome.Deleted ? t.Get("privacy.delete.done") : WebAppDeleteText(t, _config);
            m.Components = new ComponentBuilder().Build();
        });
    }

    [ComponentInteraction(PrivacyIds.DeleteCancelPattern, ignoreGroupNames: true)]
    public async Task OnCancelDeleteAsync(string ownerIdStr)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!IsOwner(ownerIdStr, Context.User.Id))
        {
            await RespondAsync(t.Get("common.not-yours"), ephemeral: true);
            return;
        }

        await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
        {
            m.Content = t.Get("privacy.delete.cancelled");
            m.Components = new ComponentBuilder().Build();
        });
    }

    [SlashCommand("withdraw", "Withdraw your health-data consent: nothing new is saved, reminders stop")]
    public async Task WithdrawAsync()
    {
        var userId = Context.User.Id;
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        var buttons = new ComponentBuilder()
            .WithButton(t.Get("privacy.withdraw.button"), PrivacyIds.WithdrawConfirm(userId), ButtonStyle.Danger)
            .WithButton(t.Get("common.cancel"), PrivacyIds.WithdrawCancel(userId), ButtonStyle.Secondary)
            .Build();

        await RespondAsync(t.Get("privacy.withdraw.confirm"), components: buttons, ephemeral: true);
    }

    [ComponentInteraction(PrivacyIds.WithdrawConfirmPattern, ignoreGroupNames: true)]
    public async Task OnConfirmWithdrawAsync(string ownerIdStr)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!IsOwner(ownerIdStr, Context.User.Id))
        {
            await RespondAsync(t.Get("common.not-yours"), ephemeral: true);
            return;
        }

        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        if (!await _consent.WithdrawHealthDataConsentAsync(userId))
        {
            await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
            {
                m.Content = t.Get("privacy.withdraw.none");
                m.Components = new ComponentBuilder().Build();
            });
            return;
        }

        var deletesOn = (await _settings.GetLocalTodayAsync(userId)).AddDays(ConsentService.WithdrawnDataLifetime.Days);
        await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
        {
            m.Content = t.Format("privacy.withdraw.done", deletesOn.ToString(t.Get("date.full"), t.Culture));
            m.Components = new ComponentBuilder()
                .WithButton(t.Get("consent.delete-button"), PrivacyIds.DeleteStart(Context.User.Id), ButtonStyle.Secondary)
                .Build();
        });
    }

    [ComponentInteraction(PrivacyIds.WithdrawCancelPattern, ignoreGroupNames: true)]
    public async Task OnCancelWithdrawAsync(string ownerIdStr)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!IsOwner(ownerIdStr, Context.User.Id))
        {
            await RespondAsync(t.Get("common.not-yours"), ephemeral: true);
            return;
        }

        await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
        {
            m.Content = t.Get("privacy.withdraw.cancelled");
            m.Components = new ComponentBuilder().Build();
        });
    }

    /// <summary>
    /// Deletes the owner's data only when the person clicking is that owner and the account has no password and no two-step sign-in
    /// (checked again here, so an older confirmation message can't skip it).
    /// </summary>
    internal static async Task<DeleteOutcome> TryDeleteAsync(HabitService habits, AccountService accounts, string ownerIdStr, ulong clickerId)
    {
        if (!IsOwner(ownerIdStr, clickerId)) return DeleteOutcome.NotYours;
        return await accounts.DeleteUnprotectedUserDataAsync(await habits.FindDiscordUserAsync(clickerId))
            ? DeleteOutcome.Deleted
            : DeleteOutcome.NeedsWebApp;
    }

    /// <summary>Where a protected account is deleted: the web app's settings, linked when the web origin is configured.</summary>
    internal static string WebAppDeleteText(Texts t, IConfiguration config)
        => config[LinkModule.WebOriginKey] is { Length: > 0 } origin
            ? t.Format("privacy.delete.web.link", origin.TrimEnd('/'))
            : t.Get("privacy.delete.web.app");

    private static bool IsOwner(string ownerIdStr, ulong clickerId)
        => ulong.TryParse(ownerIdStr, out var ownerId) && ownerId == clickerId;
}
