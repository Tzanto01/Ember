using Ember.Bot.Modules;
using Ember.Bot.Services;
using Ember.Core.Models;
using Ember.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Ember.Bot.Jobs;

/// <summary>
/// Runs daily and deletes the health data of users whose consent stayed withdrawn (<see cref="User.ConsentWithdrawnAt"/>) for
/// <see cref="HabitService.WithdrawnDataLifetime"/>. <see cref="HabitService.DeletionReminderLead"/> before that, each such user is
/// reminded once over their reminder destinations (<see cref="ReminderDestinations.Resolve"/>): a Discord DM with the consent buttons
/// (<see cref="ConsentModule.PromptButtons"/>) and web push tagged <see cref="PushTag"/>. The reminder is claimed before sending, so a
/// failed send is not retried; the deletion waits at least <see cref="HabitService.DeletionReminderLead"/> after the claim.
/// </summary>
[DisallowConcurrentExecution]
public class HealthDataDeletionJob : IJob
{
    public static readonly JobKey Key = new("health-data-deletion", "accounts");

    /// <summary>The web push tag of the deletion reminder.</summary>
    public const string PushTag = "consent";

    private readonly IServiceProvider _services;
    private readonly IDiscordDmSender _discord;
    private readonly WebPushReminderChannel? _push;
    private readonly ILogger<HealthDataDeletionJob> _logger;
    private readonly TimeProvider _time;

    public HealthDataDeletionJob(IServiceProvider services, IDiscordDmSender discord, IEnumerable<IReminderChannel> channels,
        ILogger<HealthDataDeletionJob> logger, TimeProvider time)
    {
        _services = services;
        _discord = discord;
        _push = channels.OfType<WebPushReminderChannel>().FirstOrDefault();
        _logger = logger;
        _time = time;
    }

    public Task Execute(IJobExecutionContext context) => RunAsync(context.CancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        var habits = scope.ServiceProvider.GetRequiredService<HabitService>();
        var now = _time.GetUtcNow().UtcDateTime;

        var reminded = 0;
        foreach (var id in await habits.GetDeletionReminderDueAsync(
                     now - HabitService.WithdrawnDataLifetime + HabitService.DeletionReminderLead, cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (await habits.ClaimDeletionReminderAsync(id, cancellationToken) is not { } withdrawnAt) continue;
            reminded++;
            await RemindAsync(habits, id, withdrawnAt + HabitService.WithdrawnDataLifetime, cancellationToken);
        }

        var cutoff = now - HabitService.WithdrawnDataLifetime;
        var deleted = 0;
        foreach (var id in await habits.GetHealthDataDeletionDueAsync(cutoff, now - HabitService.DeletionReminderLead, cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (await habits.DeleteHealthDataAsync(id, cutoff, cancellationToken)) deleted++;
        }

        if (reminded > 0 || deleted > 0)
            _logger.LogInformation("HealthDataDeletionJob: reminded {Reminded} users, deleted the health data of {Deleted} users.",
                reminded, deleted);
    }

    private async Task RemindAsync(HabitService habits, Guid userId, DateTime deletesAtUtc, CancellationToken cancellationToken)
    {
        try
        {
            if (await habits.GetUserWithDestinationsAsync(userId, cancellationToken) is not { } user) return;
            var targets = ReminderDestinations.Resolve(user, []);
            var t = BotTexts.For(user.Language);
            var tz = TimezoneHelper.Find(user.Timezone) ?? TimeZoneInfo.Utc;
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(deletesAtUtc, DateTimeKind.Utc), tz))
                .ToString(t.Get("date.full"), t.Culture);

            if (targets.Discord && user.DiscordId() is { } discordId)
            {
                var outcome = await _discord.SendMessageAsync(discordId, t.Format("consent.deletion-reminder", date),
                    components: ConsentModule.PromptButtons(t, discordId), cancellationToken: cancellationToken);
                if (outcome != DeliveryOutcome.Delivered)
                    _logger.LogInformation("Could not send the deletion reminder DM to user {UserId}: {Outcome}.", user.Id, outcome);
            }

            if (_push is not null && targets.PushSubscriptionIds.Count > 0)
            {
                var body = t.Format("consent.deletion-push.body", date);
                await _push.SendNoticeAsync(user.Id, targets.PushSubscriptionIds, body, body, PushTag, "/consent", cancellationToken,
                    t.Get("consent.deletion-push.title"));
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not deliver the deletion reminder to user {UserId}.", userId);
        }
    }
}
