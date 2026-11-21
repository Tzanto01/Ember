using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ember.Core.Localization;
using Ember.Core.Services;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebPushSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace Ember.Bot.Services;

/// <summary>
/// Sends Web Push notifications (VAPID, aes128gcm) to a user's push subscriptions: reminders (through
/// <see cref="WebPushReminderChannel"/>), the clear push (<see cref="SendClearAsync"/>) and plain notices (<see cref="SendNoticeAsync"/>).
/// A subscription the push service reports as gone (404/410) is deleted. Registered only when web push is configured
/// (<see cref="IsConfigured"/>).
/// </summary>
public sealed class WebPushSender
{
    /// <summary>Name of the <see cref="IHttpClientFactory"/> client used to reach push services.</summary>
    public const string HttpClientName = "web-push";

    /// <summary>How long (seconds) a push service keeps an undelivered reminder; a later one is pointless.</summary>
    public const int TimeToLiveSeconds = 12 * 60 * 60;

    private static readonly JsonSerializerOptions PayloadJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The notification tag of a habit's reminders.</summary>
    public static string HabitTag(int habitId) => $"habit-{habitId}";

    /// <summary>The notification tag of a routine's reminders.</summary>
    public static string RoutineTag(int routineId) => $"routine-{routineId}";

    /// <summary>
    /// The clear payload for <paramref name="tag"/>: a v1.14 service worker closes the notifications with that tag; an older one
    /// shows the "Logged" notification in <paramref name="t"/> (English when null).
    /// </summary>
    public static string ClearPayload(string tag, Texts? t = null) => JsonSerializer.Serialize(new
    {
        clear = new { tag },
        notification = new
        {
            title = "Ember",
            body = (t ?? BotTexts.English).Get("push.logged"),
            data = new { onActionClick = new { @default = new { operation = "navigateLastFocusedOrOpen", url = "/today" } } },
        },
    }, PayloadJson);

    /// <summary>
    /// The notification payload with <paramref name="body"/>, plus the "Done" action (labelled in <paramref name="t"/>, English when null)
    /// when <paramref name="doneToken"/> is set and the <paramref name="tag"/> when set; a click opens <paramref name="url"/>. The title
    /// is <paramref name="title"/>, "Ember" when null.
    /// </summary>
    internal static string BuildPayload(string body, string? doneToken = null, string? tag = null, Texts? t = null, string url = "/today",
        string? title = null)
        => JsonSerializer.Serialize(new
        {
            notification = new
            {
                title = title ?? "Ember",
                body,
                tag,
                actions = doneToken is null ? null : new[] { new { action = "done", title = (t ?? BotTexts.English).Get("reminder.done") } },
                data = new
                {
                    onActionClick = new { @default = new { operation = "navigateLastFocusedOrOpen", url } },
                    doneToken,
                },
            },
        }, PayloadJson);

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _httpClients;
    private readonly VapidAuthentication _vapid;
    private readonly ILogger<WebPushSender> _logger;

    public WebPushSender(IServiceScopeFactory scopes, IHttpClientFactory httpClients, VapidAuthentication vapid, ILogger<WebPushSender> logger)
    {
        _scopes = scopes;
        _httpClients = httpClients;
        _vapid = vapid;
        _logger = logger;
    }

    /// <summary>Builds the VAPID authentication from <c>WebPush:PublicKey</c>, <c>WebPush:PrivateKey</c> and <c>WebPush:Subject</c>.</summary>
    /// <exception cref="InvalidOperationException">A key or the subject is missing or invalid.</exception>
    public static VapidAuthentication CreateVapidAuthentication(IConfiguration config)
    {
        try
        {
            return new VapidAuthentication(config["WebPush:PublicKey"], config["WebPush:PrivateKey"])
            {
                Subject = config["WebPush:Subject"],
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException(InvalidConfigMessage, ex);
        }
    }

    private static readonly string[] VapidSettings = ["WebPush:PublicKey", "WebPush:PrivateKey", "WebPush:Subject"];

    private const string InvalidConfigMessage =
        "Invalid web push configuration: set all or none of WebPush:PublicKey, WebPush:PrivateKey and WebPush:Subject " +
        "(base64url VAPID keys; subject a mailto: or https: URL).";

    /// <summary>True when the VAPID public key, private key and subject are all configured, false when none is.</summary>
    /// <exception cref="InvalidOperationException">Only some of them are configured.</exception>
    public static bool IsConfigured(IConfiguration config)
    {
        var set = VapidSettings.Count(key => !string.IsNullOrWhiteSpace(config[key]));
        return set switch
        {
            0 => false,
            3 => true,
            _ => throw new InvalidOperationException(InvalidConfigMessage),
        };
    }

    /// <summary>
    /// Sends the clear payload (<see cref="ClearPayload"/>) for <paramref name="tag"/> in <paramref name="language"/> to the user's push
    /// subscriptions in <paramref name="subscriptionIds"/>.
    /// </summary>
    public async Task SendClearAsync(Guid userId, IReadOnlyCollection<int> subscriptionIds, string tag, string language,
        CancellationToken cancellationToken = default)
    {
        List<Core.Models.PushSubscription> subscriptions;
        using (var scope = _scopes.CreateScope())
            subscriptions = (await scope.ServiceProvider.GetRequiredService<ReminderService>().GetPushSubscriptionsAsync(userId, cancellationToken))
                .Where(s => subscriptionIds.Contains(s.Id)).ToList();
        var payload = ClearPayload(tag, BotTexts.For(language));
        await SendAsync(userId, subscriptions, _ => payload, cancellationToken);
    }

    /// <summary>
    /// Sends a plain notification (<paramref name="namedBody"/> to subscriptions that show names, else <paramref name="body"/>) with
    /// <paramref name="tag"/>, opening <paramref name="url"/>, to the user's push subscriptions in <paramref name="subscriptionIds"/>.
    /// The title is <paramref name="title"/>, "Ember" when null. True when at least one accepted it.
    /// </summary>
    public async Task<bool> SendNoticeAsync(Guid userId, IReadOnlyCollection<int> subscriptionIds, string body, string namedBody,
        string tag, string url, CancellationToken cancellationToken = default, string? title = null)
    {
        List<Core.Models.PushSubscription> subscriptions;
        using (var scope = _scopes.CreateScope())
            subscriptions = (await scope.ServiceProvider.GetRequiredService<ReminderService>().GetPushSubscriptionsAsync(userId, cancellationToken))
                .Where(s => subscriptionIds.Contains(s.Id)).ToList();
        if (subscriptions.Count == 0) return false;
        return await SendAsync(userId, subscriptions, s => BuildPayload(s.ShowNames ? namedBody : body, tag: tag, url: url, title: title), cancellationToken);
    }

    /// <summary>Pushes each subscription its payload; deletes ones the push service reports gone. True when at least one accepted it.</summary>
    internal async Task<bool> SendAsync(Guid userId, List<Core.Models.PushSubscription> subscriptions,
        Func<Core.Models.PushSubscription, string> payloadFor, CancellationToken cancellationToken)
    {
        var client = new PushServiceClient(_httpClients.CreateClient(HttpClientName))
        {
            DefaultAuthentication = _vapid,
            AutoRetryAfter = false,
        };

        var delivered = false;
        foreach (var subscription in subscriptions)
        {
            var target = new WebPushSubscription { Endpoint = subscription.Endpoint, Keys = new Dictionary<string, string>() };
            target.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
            target.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);

            var payload = payloadFor(subscription);
            try
            {
                await client.RequestPushMessageDeliveryAsync(target, new PushMessage(payload) { TimeToLive = TimeToLiveSeconds },
                    cancellationToken);
                delivered = true;
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ReminderService>()
                        .DeletePushSubscriptionByEndpointAsync(subscription.Endpoint, cancellationToken);
                    _logger.LogInformation("Removed an expired push subscription {SubscriptionId} of user {UserId}.",
                        subscription.Id, userId);
                }
                catch (Exception deleteEx) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(deleteEx, "Could not remove the expired push subscription {SubscriptionId} of user {UserId}.",
                        subscription.Id, userId);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Web push to subscription {SubscriptionId} of user {UserId} failed.",
                    subscription.Id, userId);
            }
        }

        return delivered;
    }
}
