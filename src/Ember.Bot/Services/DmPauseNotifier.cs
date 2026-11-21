using Discord;
using Discord.Interactions;
using Ember.Bot.Discord.Messages;
using Ember.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ember.Bot.Services;

/// <summary>
/// After any successful interaction, tells a user whose DM reminders are paused why, once,
/// with an ephemeral follow-up and a button to try again.
/// </summary>
public class DmPauseNotifier
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DmPauseNotifier> _logger;

    public DmPauseNotifier(IServiceProvider services, ILogger<DmPauseNotifier> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task OnInteractionExecutedAsync(ICommandInfo command, IInteractionContext context, IResult result)
    {
        if (!result.IsSuccess || context.Interaction is IAutocompleteInteraction || !context.Interaction.HasResponded)
            return;

        using var scope = _services.CreateScope();
        var habits = scope.ServiceProvider.GetRequiredService<HabitService>();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        Guid userId;
        try
        {
            userId = await habits.FindDiscordUserAsync(context.User.Id);
            if (!await reminders.ClaimDmPauseNoticeAsync(userId))
                return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check paused reminders for user {UserId}.", context.User.Id);
            return;
        }

        try
        {
            var t = await BotTexts.ForAsync(habits, context.Interaction);
            await context.Interaction.FollowupAsync(
                DmRetryNotice.NoticeText(t),
                components: DmRetryNotice.RetryButton(t, context.User.Id),
                ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not show the paused-reminders notice to user {UserId}.", context.User.Id);
            try { await reminders.ReleaseDmPauseNoticeAsync(userId); }
            catch (Exception releaseEx) { _logger.LogWarning(releaseEx, "Could not re-arm the paused-reminders notice for user {UserId}.", context.User.Id); }
        }
    }
}
