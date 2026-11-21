using Discord;
using Ember.Bot.Discord.Messages;
using Ember.Bot.Notifications;
using Ember.Bot.Services;
using Ember.Core.Models;
using Ember.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;

namespace Ember.Bot.Jobs;

/// <summary>
/// Runs every minute: deletes warmth older than <see cref="FiresideService.WarmthLifetime"/> and expired codes, then delivers pending
/// Fireside notifications (new warmth, "joined your fireside"), up to <see cref="MaxPerRun"/> of each. Each is claimed once before
/// sending (<see cref="FiresideService.ClaimWarmthNoticeAsync"/>, <see cref="FiresideService.ClaimJoinNoticeAsync"/>), so nothing is
/// sent twice and a failed send is not retried; one no longer visible is dropped silently. Delivery goes to the receiver's reminder
/// destinations (<see cref="ReminderDestinations.Resolve"/>): a Discord DM with <see cref="FiresideDm.NoticeButtons"/>, and web push
/// tagged <see cref="PushTag"/> that names the sender only on subscriptions that show names. While the Discord client isn't logged in
/// (<see cref="IDiscordDmSender.IsLoggedIn"/>), a notification for a receiver with a Discord destination is given back unsent
/// (<see cref="FiresideService.ReleaseWarmthNoticeAsync"/>, <see cref="FiresideService.ReleaseJoinNoticeAsync"/>) for a later run.
/// </summary>
[DisallowConcurrentExecution]
public class FiresideNotifyJob : IJob
{
    public static readonly JobKey Key = new("fireside-notify", "fireside");

    /// <summary>Most notifications of each kind per run.</summary>
    public const int MaxPerRun = 50;

    /// <summary>The web push tag of Fireside notifications.</summary>
    public const string PushTag = "fireside";

    private readonly IServiceProvider _services;
    private readonly IDiscordDmSender _discord;
    private readonly WebPushSender? _push;
    private readonly ILogger<FiresideNotifyJob> _logger;

    public FiresideNotifyJob(IServiceProvider services, IDiscordDmSender discord, ILogger<FiresideNotifyJob> logger, WebPushSender? push = null)
    {
        _services = services;
        _discord = discord;
        _push = push;
        _logger = logger;
    }

    public Task Execute(IJobExecutionContext context) => RunAsync(context.CancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _services.CreateScope();
        var fireside = scope.ServiceProvider.GetRequiredService<FiresideService>();
        var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();

        await fireside.DeleteExpiredAsync(cancellationToken);
        var (warmthIds, linkIds) = await fireside.GetPendingNoticesAsync(MaxPerRun, cancellationToken);
        foreach (var id in warmthIds)
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (await fireside.ClaimWarmthNoticeAsync(id, cancellationToken) is { } notice) await SendAsync(fireside, reminders, notice, id, cancellationToken);
        }
        foreach (var id in linkIds)
        {
            if (cancellationToken.IsCancellationRequested) return;
            if (await fireside.ClaimJoinNoticeAsync(id, cancellationToken) is { } notice) await SendAsync(fireside, reminders, notice, null, cancellationToken);
        }
    }

    private async Task SendAsync(FiresideService fireside, ReminderService reminders, FiresideNotice notice, int? warmthId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await reminders.GetUserWithDestinationsAsync(notice.ReceiverId, cancellationToken) is not { } user) return;
            var targets = ReminderDestinations.Resolve(user, []);
            if (targets.Discord && user.DiscordId() is not null && !_discord.IsLoggedIn)
            {
                if (warmthId is { } id) await fireside.ReleaseWarmthNoticeAsync(id, cancellationToken);
                else await fireside.ReleaseJoinNoticeAsync(notice.LinkId, cancellationToken);
                return;
            }
            var t = BotTexts.For(user.Language);
            var warmth = notice.Message is not null;
            var kind = warmth ? "fireside.notify.warmth" : "fireside.notify.joined";

            if (targets.Discord && user.DiscordId() is { } discordId)
            {
                var line = t.Format(kind, Format.Sanitize(notice.Name));
                var buttons = FiresideDm.NoticeButtons(t, notice.LinkId, warmthId);
                var outcome = warmth
                    ? await _discord.SendMessageAsync(discordId, embed: new EmbedBuilder()
                        .WithColor(0xE8873A)
                        .WithTitle(t.Get("fireside.message." + notice.Message))
                        .WithDescription(line)
                        .Build(), components: buttons, cancellationToken: cancellationToken)
                    : await _discord.SendMessageAsync(discordId, line, components: buttons, cancellationToken: cancellationToken);
                if (outcome != DeliveryOutcome.Delivered)
                    _logger.LogInformation("Could not send a Fireside DM to user {UserId}: {Outcome}.", user.Id, outcome);
            }

            if (_push is not null && targets.PushSubscriptionIds.Count > 0)
                await _push.SendNoticeAsync(user.Id, targets.PushSubscriptionIds, t.Get(kind + ".unnamed"), t.Format(kind, notice.Name),
                    PushTag, warmth ? "/today" : "/fireside", cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not deliver a Fireside notification to user {UserId}.", notice.ReceiverId);
        }
    }
}
