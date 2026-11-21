using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ember.Bot.Notifications;

/// <summary>
/// Sends reminders as Web Push notifications through <see cref="WebPushSender"/> to the user's push subscriptions in
/// <see cref="Reminder.PushSubscriptionIds"/>. The notification names no habit, since it can show on a lock screen, unless the
/// subscription opted in (<see cref="Core.Models.PushSubscription.ShowNames"/>); then it names the habit or routine. Its tag
/// (<see cref="WebPushSender.HabitTag"/>, <see cref="WebPushSender.RoutineTag"/>) lets a later reminder or the clear push
/// (<see cref="WebPushSender.SendClearAsync"/>) replace it. Delivered when at least one subscription accepted the
/// message; a delivered reminder is remembered (<see cref="ReminderService.AddPushReminderAsync"/>) until its item is done or it expires.
/// A single-habit reminder also carries a "Done ✅" action with a one-time token (<see cref="ReminderService.CreateReminderActionTokenAsync"/>)
/// that expires with the reminder (<see cref="ReminderExpiry.IsExpired"/>), sent only to subscriptions registered by a
/// sign-in session that is still live; other subscriptions and routine reminders get the plain payload.
/// A let-go habit's reminder (<see cref="Reminder.LetGo"/>) is the quiet check-in text (nameless unless the subscription opted in) with
/// no action and no token, opens <c>/history/{habitId}</c> and is not remembered.
/// </summary>
public sealed class WebPushReminderChannel : IReminderChannel
{
    /// <summary>The Angular service worker notification payload, in English.</summary>
    public static readonly string Payload = WebPushSender.BuildPayload(BotTexts.English.Get("push.reminder.first"));

    /// <summary>The payload of a second reminder, in English: the same notification with its own body.</summary>
    public static readonly string SecondPayload = WebPushSender.BuildPayload(BotTexts.English.Get("push.reminder.second"));

    private readonly IServiceScopeFactory _scopes;
    private readonly WebPushSender _sender;
    private readonly ILogger<WebPushReminderChannel> _logger;

    public WebPushReminderChannel(IServiceScopeFactory scopes, WebPushSender sender, ILogger<WebPushReminderChannel> logger)
    {
        _scopes = scopes;
        _sender = sender;
        _logger = logger;
    }

    public ReminderChannels Channel => ReminderChannels.WebPush;

    public async Task<DeliveryOutcome> SendAsync(Reminder reminder, CancellationToken cancellationToken = default)
    {
        List<Core.Models.PushSubscription> subscriptions;
        HashSet<Guid> liveSessions;
        var t = BotTexts.For(reminder.Language);
        var body = t.Get(reminder.LetGo ? "letgo.reminder.nameless" : reminder.Second ? "push.reminder.second" : "push.reminder.first");
        var namedBody = reminder.LetGo
            ? t.Format("letgo.reminder", reminder.ItemName)
            : t.Format((reminder.IsRoutine ? "push.reminder.routine" : "push.reminder.habit")
                + (reminder.Second ? ".second" : ".first"), reminder.ItemName);
        var url = reminder.LetGo ? $"/history/{reminder.ItemId}" : "/today";
        string? doneToken = null;
        var tag = reminder.IsRoutine ? WebPushSender.RoutineTag(reminder.ItemId) : WebPushSender.HabitTag(reminder.ItemId);
        var allowed = reminder.PushSubscriptionIds ?? [];
        using (var scope = _scopes.CreateScope())
        {
            var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();
            subscriptions = (await reminders.GetPushSubscriptionsAsync(reminder.UserId, cancellationToken))
                .Where(s => allowed.Contains(s.Id)).ToList();
            if (subscriptions.Count == 0) return DeliveryOutcome.UserNotFound;

            liveSessions = await reminders.GetLiveSessionIdsAsync(reminder.UserId, cancellationToken);
            if (!reminder.IsRoutine && !reminder.LetGo && subscriptions.Any(s => s.SessionId is { } id && liveSessions.Contains(id)))
                doneToken = await reminders.CreateReminderActionTokenAsync(reminder.UserId, reminder.ItemId, reminder.Day,
                    cancellationToken);
        }

        var delivered = await _sender.SendAsync(reminder.UserId, subscriptions,
            s => WebPushSender.BuildPayload(s.ShowNames ? namedBody : body,
                s.SessionId is { } sessionId && liveSessions.Contains(sessionId) ? doneToken : null, tag, t, url),
            cancellationToken);
        if (!delivered) return DeliveryOutcome.Undeliverable;
        if (reminder.LetGo) return DeliveryOutcome.Delivered;

        try
        {
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ReminderService>().AddPushReminderAsync(reminder.UserId,
                reminder.IsRoutine ? null : reminder.ItemId, reminder.IsRoutine ? reminder.ItemId : null,
                reminder.Day, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not remember the web push reminder for {Tag} of user {UserId}.", tag, reminder.UserId);
        }
        return DeliveryOutcome.Delivered;
    }
}
