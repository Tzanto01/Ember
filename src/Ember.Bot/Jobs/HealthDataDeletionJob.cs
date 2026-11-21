using Ember.Bot.Discord.Messages;
using Ember.Bot.Services;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Ember.Bot.Jobs;

/// <summary>
/// Runs daily and deletes the health data of users whose consent stayed withdrawn (<see cref="User.ConsentWithdrawnAt"/>) for
/// <see cref="ConsentService.WithdrawnDataLifetime"/>. <see cref="ConsentService.DeletionReminderLead"/> before that, each such user gets one
/// notice, whatever their reminder settings: a DM to a linked Discord account with the consent prompt (<see cref="ConsentPrompt.PromptEmbed"/>,
/// <see cref="ConsentPrompt.PromptButtons"/>) and web push tagged <see cref="PushTag"/> to every enabled push subscription. It names the
/// deletion day (<see cref="ConsentService.DeletionDueUtc"/>, the user's local date). The notice is claimed before sending; when the user has
/// a target but no send succeeded, the claim is given back so the next run tries again; a user without any target keeps the claim. The
/// deletion waits at least <see cref="ConsentService.DeletionReminderLead"/> after the claim. One user's failure doesn't stop the run.
/// </summary>
[DisallowConcurrentExecution]
public class HealthDataDeletionJob : IJob
{
    public static readonly JobKey Key = new("health-data-deletion", "accounts");

    /// <summary>The web push tag of the deletion reminder.</summary>
    public const string PushTag = "consent";

    /// <summary>How much later than the run's start a claim may land and still count for the deletion three daily runs later.</summary>
    private static readonly TimeSpan ClaimSlack = TimeSpan.FromHours(1);

    /// <summary>How long past the deletion date a failed reminder is retried; after that the claim stays, so the deletion isn't held back for good.</summary>
    internal static readonly TimeSpan RetryFor = TimeSpan.FromDays(7);

    private readonly IServiceProvider _services;
    private readonly IDiscordDmSender _discord;
    private readonly WebPushReminderChannel? _push;
    private readonly ILogger<HealthDataDeletionJob> _logger;
    private readonly TimeProvider _time;
    private readonly IConfiguration _config;

    public HealthDataDeletionJob(IServiceProvider services, IDiscordDmSender discord, IEnumerable<IReminderChannel> channels,
        ILogger<HealthDataDeletionJob> logger, TimeProvider time, IConfiguration config)
    {
        _services = services;
        _discord = discord;
        _push = channels.OfType<WebPushReminderChannel>().FirstOrDefault();
        _logger = logger;
        _time = time;
        _config = config;
    }

    public Task Execute(IJobExecutionContext context) => RunAsync(context.CancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
        var consent = scope.ServiceProvider.GetRequiredService<ConsentService>();
        var now = _time.GetUtcNow().UtcDateTime;

        var reminded = 0;
        foreach (var id in await consent.GetDeletionReminderDueAsync(
                     now - ConsentService.WithdrawnDataLifetime + ConsentService.DeletionReminderLead, cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                if (await consent.ClaimDeletionReminderAsync(id, cancellationToken) is not { } withdrawnAt) continue;
                var delivered = false;
                try
                {
                    delivered = await RemindAsync(reminders, id, ConsentService.DeletionDueUtc(withdrawnAt, now), cancellationToken);
                }
                finally
                {
                    if (!delivered && now < withdrawnAt + ConsentService.WithdrawnDataLifetime + RetryFor)
                        await consent.ReleaseDeletionReminderAsync(id, CancellationToken.None);
                }
                if (delivered) reminded++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Could not send the deletion reminder to user {UserId}: {ExceptionType}.", id, ex.GetType().Name);
            }
        }

        var cutoff = now - ConsentService.WithdrawnDataLifetime;
        var deleted = 0;
        foreach (var id in await consent.GetHealthDataDeletionDueAsync(cutoff, now - ConsentService.DeletionReminderLead + ClaimSlack, cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                if (await consent.DeleteHealthDataAsync(id, cutoff, cancellationToken)) deleted++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Could not delete the health data of user {UserId}: {ExceptionType}.", id, ex.GetType().Name);
            }
        }

        if (reminded > 0 || deleted > 0)
            _logger.LogInformation("HealthDataDeletionJob: reminded {Reminded} users, deleted the health data of {Deleted} users.",
                reminded, deleted);
    }

    /// <summary>Sends the notice; false when the user has a target but nothing reached them (true when there is no target at all).</summary>
    private async Task<bool> RemindAsync(ReminderService reminders, Guid userId, DateTime deletesAtUtc, CancellationToken cancellationToken)
    {
        if (await reminders.GetUserWithDestinationsAsync(userId, cancellationToken) is not { } user) return true;
        var pushIds = _push is null ? [] : user.PushSubscriptions.Where(s => s.Enabled).Select(s => s.Id).ToList();
        var discordId = user.DiscordId();
        if (discordId is null && pushIds.Count == 0) return true;

        var t = BotTexts.For(user.Language);
        var tz = TimeZones.Find(user.Timezone) ?? TimeZoneInfo.Utc;
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(deletesAtUtc, DateTimeKind.Utc), tz))
            .ToString(t.Get("date.full"), t.Culture);

        var delivered = false;
        if (discordId is { } id)
        {
            try
            {
                var outcome = await _discord.SendMessageAsync(id, t.Format("consent.deletion-reminder", date),
                    embed: ConsentPrompt.PromptEmbed(t, _config), components: ConsentPrompt.PromptButtons(t, id), cancellationToken: cancellationToken);
                delivered = outcome == DeliveryOutcome.Delivered;
                if (!delivered) _logger.LogInformation("Could not send the deletion reminder DM to user {UserId}: {Outcome}.", user.Id, outcome);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Could not send the deletion reminder DM to user {UserId}: {ExceptionType}.", user.Id, ex.GetType().Name);
            }
        }

        if (pushIds.Count > 0)
        {
            try
            {
                var body = t.Format("consent.deletion-push.body", date);
                delivered |= await _push!.SendNoticeAsync(user.Id, pushIds, body, body, PushTag, "/consent", cancellationToken,
                    t.Get("consent.deletion-push.title"));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Could not push the deletion reminder to user {UserId}: {ExceptionType}.", user.Id, ex.GetType().Name);
            }
        }
        return delivered;
    }
}
