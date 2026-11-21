using Ember.Bot.Discord.Messages;
using Ember.Bot.Notifications;
using Ember.Bot.Services;
using Ember.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Ember.Bot.Jobs;

/// <summary>
/// Runs every minute and asks, once per account, up to <see cref="ReminderService.MaxDiscordAsksPerRun"/> Discord-linked users who never
/// answered whether their reminders may come by Discord DM and have a reminder time set (<see cref="ReminderService.GetDiscordAskUserIdsAsync"/>).
/// The ask is recorded before the DM is sent, so it is never repeated, also when the DM can't be delivered; only a transient send
/// failure (<see cref="ReminderRetries.IsTransient"/>) clears it so the next run asks again.
/// </summary>
[DisallowConcurrentExecution]
public class DiscordReminderAskJob : IJob
{
    public static readonly JobKey Key = new("discord-reminder-ask", "asks");

    private readonly IServiceProvider _services;
    private readonly IDiscordDmSender _discord;
    private readonly ILogger<DiscordReminderAskJob> _logger;

    public DiscordReminderAskJob(IServiceProvider services, IDiscordDmSender discord, ILogger<DiscordReminderAskJob> logger)
    {
        _services = services;
        _discord = discord;
        _logger = logger;
    }

    public Task Execute(IJobExecutionContext context) => RunAsync(context.CancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<UserSettingsService>();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        foreach (var (userId, discordUserId) in await reminders.GetDiscordAskUserIdsAsync(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                await reminders.MarkDiscordRemindersAskedAsync(userId, cancellationToken);
                var t = BotTexts.For(await settings.GetLanguageAsync(userId));
                var outcome = await _discord.SendMessageAsync(discordUserId, DiscordRemindersAsk.AskText(t),
                    components: DiscordRemindersAsk.AskButtons(t, discordUserId), cancellationToken: cancellationToken);
                if (outcome != DeliveryOutcome.Delivered)
                    _logger.LogInformation("Could not ask user {UserId} about Discord reminders: {Outcome}.", userId, outcome);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ReminderRetries.IsTransient(ex))
            {
                _logger.LogWarning(ex, "Could not ask user {UserId} about Discord reminders; asking again next run.", userId);
                await reminders.ClearDiscordRemindersAskedAsync(userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not ask user {UserId} about Discord reminders.", userId);
            }
        }
    }
}
