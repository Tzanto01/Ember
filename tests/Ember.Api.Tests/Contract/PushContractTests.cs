using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ember.Core.Services;

namespace Ember.Api.Tests.Contract;

/// <summary>Web push (with a VAPID key configured), the reminder "Done" action and the home screen widget check-in.</summary>
[Collection("Postgres")]
public class PushContractTests(PostgresFixture postgres) : ContractTestBase(postgres, "push")
{
    private const string Subscriptions = "/api/me/push-subscriptions";
    private const string Done = "/api/reminders/done";
    private const string CheckIn = "/api/widget/check-in";

    private sealed record Keys(string P256dh, string Auth);

    private sealed record Subscription(string? Endpoint, Keys? Keys, bool? ShowNames = null);

    protected override Dictionary<string, string?>? Settings => new() { ["WebPush:PublicKey"] = "contract-vapid-public-key" };

    private static Subscription Valid(string host = "fcm.googleapis.com", bool? showNames = null)
        => new($"https://{host}/send/contract", new Keys("BPcontractp256dhkey", "contractauth"), showNames);

    private async Task<ApiClient> BearerAsync()
    {
        var user = await Host.ConfirmedUserAsync();
        var tokens = await (await user.Post("/api/auth/login", new Credentials(user.Email, ApiClient.Passphrase))).ExpectJson();
        var bearer = Host.NewClient();
        bearer.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        await bearer.GiveConsentAsync();
        return bearer;
    }

    private Task<HttpResponseMessage> WidgetCheckIn(string? authorization, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, CheckIn) { Content = JsonContent.Create(body) };
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return Host.NewClient().Http.SendAsync(request);
    }

    [DockerFact]
    public async Task Push_reminder_and_widget_exchanges_match_the_golden()
    {
        await Push();
        await ReminderDone();
        await Widget();
        Verify();
    }

    private async Task Push()
    {
        var user = await Host.SignedInAsync();
        await Rec("vapid.ok", "/api/push/vapid-public-key", Host.NewClient().Get("/api/push/vapid-public-key"));
        await Rec("subscribe.ok", Subscriptions, user.Post(Subscriptions, Valid()));
        await Rec("subscribe.show-names", Subscriptions, user.Post(Subscriptions, Valid("updates.push.services.mozilla.com", true)));
        await Rec("subscribe.invalid-endpoint", Subscriptions, user.Post(Subscriptions, Valid("evil.example.test")));
        await Rec("subscribe.invalid-keys", Subscriptions, user.Post(Subscriptions, new Subscription(Valid().Endpoint, new Keys("not base64!", ""))));
        await Rec("subscribe.missing-body-fields", Subscriptions, user.Post(Subscriptions, new { }));
        await Rec("subscribe.unauthenticated", Subscriptions, Host.NewClient().Post(Subscriptions, Valid()));
        await Rec("destinations.with-browser", "/api/me/reminder-destinations", user.Get("/api/me/reminder-destinations"));
        await Rec("unsubscribe.ok", Subscriptions, user.Delete($"{Subscriptions}?endpoint={Uri.EscapeDataString(Valid().Endpoint!)}"));
        await Rec("unsubscribe.missing-endpoint", Subscriptions, user.Delete(Subscriptions));
        await Rec("unsubscribe.unauthenticated", Subscriptions, Host.NewClient().Delete($"{Subscriptions}?endpoint=x"));
    }

    private async Task ReminderDone()
    {
        var user = await Host.SignedInAsync();
        var userId = await user.IdAsync();
        var habit = (await user.CreateHabitAsync("Water")).GetProperty("id").GetInt32();
        var letGo = (await (await user.Post("/api/habits", new { name = "Coffee", kind = "letGo", frequency = "daily" })).ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();
        Task<string> Token(int id, TimeSpan lifetime)
            => Host.Service(h => h.CreateReminderActionTokenAsync(userId, id, Host.Today, Host.Time.GetUtcNow().UtcDateTime + lifetime));

        var anonymous = Host.NewClient();
        var token = await Token(habit, TimeSpan.FromHours(10));
        await Rec("done.ok", Done, anonymous.Post(Done, new { token }));
        await Rec("done.again", Done, anonymous.Post(Done, new { token }));
        await Rec("done.unknown-token", Done, anonymous.Post(Done, new { token = "unknown" }));
        await Rec("done.missing-token", Done, anonymous.Post(Done, new { }));
        await Rec("done.token-too-long", Done, anonymous.Post(Done, new { token = new string('a', 129) }));
        await Rec("done.let-go", Done, anonymous.Post(Done, new { token = await Token(letGo, TimeSpan.FromHours(10)) }));

        var expiring = await Token(habit, TimeSpan.FromMinutes(5));
        var withdrawn = await Token(habit, TimeSpan.FromHours(10));
        Host.Time.Advance(TimeSpan.FromMinutes(10));
        await Rec("done.expired", Done, anonymous.Post(Done, new { token = expiring }));
        await user.Post("/api/me/consent/withdraw");
        await Rec("done.consent-required", Done, anonymous.Post(Done, new { token = withdrawn }));
    }

    private async Task Widget()
    {
        var bearer = await BearerAsync();
        var habit = (await (await bearer.Post("/api/habits", new { name = "Pills", frequency = "daily", timesPerDay = 2 })).ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var letGo = (await (await bearer.Post("/api/habits", new { name = "Coffee", kind = "letGo", frequency = "daily" })).ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var key = (await (await bearer.Post("/api/me/widget-key")).ExpectJson()).GetProperty("key").GetString();
        var today = Host.Today.Iso();

        await Rec("check-in.partial", CheckIn, WidgetCheckIn($"Widget {key}", new { habitId = habit, date = today }));
        await Rec("check-in.done", CheckIn, WidgetCheckIn($"Widget {key}", new { habitId = habit, date = today }));
        await Rec("check-in.low-day", CheckIn, WidgetLowDay(bearer, key!));
        await Rec("check-in.no-header", CheckIn, WidgetCheckIn(null, new { habitId = habit, date = today }));
        await Rec("check-in.wrong-scheme", CheckIn, WidgetCheckIn($"Bearer {key}", new { habitId = habit, date = today }));
        await Rec("check-in.unknown-key", CheckIn, WidgetCheckIn("Widget unknown-key", new { habitId = habit, date = today }));
        await Rec("check-in.missing-habit", CheckIn, WidgetCheckIn($"Widget {key}", new { date = today }));
        await Rec("check-in.bad-date", CheckIn, WidgetCheckIn($"Widget {key}", new { habitId = habit, date = "today" }));
        await Rec("check-in.not-today", CheckIn, WidgetCheckIn($"Widget {key}", new { habitId = habit, date = Host.Today.AddDays(-1).Iso() }));
        await Rec("check-in.let-go", CheckIn, WidgetCheckIn($"Widget {key}", new { habitId = letGo, date = today }));
        await Rec("check-in.not-found", CheckIn, WidgetCheckIn($"Widget {key}", new { habitId = 9999, date = today }));
        await Rec("check-in.cookie-session-is-not-a-key", CheckIn, (await Host.SignedInAsync()).Post(CheckIn, new { habitId = habit, date = today }));

        await bearer.Post("/api/auth/logout");
        await Rec("check-in.ended-session", CheckIn, WidgetCheckIn($"Widget {key}", new { habitId = habit, date = today }));
    }

    private async Task<HttpResponseMessage> WidgetLowDay(ApiClient bearer, string key)
    {
        var rest = (await (await bearer.Post("/api/habits", new { name = "Stretch", frequency = "daily", timesPerDay = 2 })).ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();
        await bearer.Put($"/api/low-days/{Host.Today.Iso()}");
        return await WidgetCheckIn($"Widget {key}", new { habitId = rest, date = Host.Today.Iso() });
    }
}
