using System.Net;
using System.Net.Http.Json;

namespace Ember.Api.Tests.Contract;

/// <summary>The 429 answer of each limiter: per-IP policies (auth, auth-other, widget), the mail limits and the feedback limits.</summary>
[Collection("Postgres")]
public class RateLimitContractTests(PostgresFixture postgres) : ContractTestBase(postgres, "rate-limits")
{
    protected override Dictionary<string, string?>? Settings => new()
    {
        ["Ember:RateLimits:AuthPerIpPerMinute"] = "3",
        ["Ember:RateLimits:AuthOtherPerIpPerMinute"] = "1",
        ["Ember:RateLimits:WidgetPerIpPerMinute"] = "1",
        ["Ember:RateLimits:MailPerEmailPerHour"] = "1",
        ["Ember:RateLimits:FeedbackPerHour"] = "1",
    };

    private Task<HttpResponseMessage> Widget()
        => Host.NewClient().Post("/api/widget/check-in", new { habitId = 1, date = Host.Today.Iso() });

    [DockerFact]
    public async Task Rate_limit_exchanges_match_the_golden()
    {
        var anonymous = Host.NewClient();
        var email = ApiHost.NewEmail();
        await Rec("mail.first.ok", "/api/auth/register", anonymous.Post("/api/auth/register", new Credentials(email, ApiClient.Passphrase)));
        await Rec("mail.limited", "/api/auth/register", anonymous.Post("/api/auth/register", new Credentials(email, ApiClient.Passphrase)));
        await Rec("auth.first", "/api/auth/login", anonymous.Post("/api/auth/login", new Credentials(email, ApiClient.Passphrase)));
        await Rec("auth.limited", "/api/auth/login", anonymous.Post("/api/auth/login", new Credentials(email, ApiClient.Passphrase)));

        await Rec("auth-other.first", "/api/auth/antiforgery", anonymous.Get("/api/auth/antiforgery"));
        await Rec("auth-other.limited", "/api/auth/antiforgery", anonymous.Get("/api/auth/antiforgery"));

        await Rec("widget.first", "/api/widget/check-in", Widget());
        await Rec("widget.limited", "/api/widget/check-in", Widget());

        var feedback = new { title = "Crash", text = "It closes right away", platform = "web" };
        await Rec("feedback.first", "/api/feedback", anonymous.Post("/api/feedback", feedback));
        await Rec("feedback.limited", "/api/feedback", anonymous.Post("/api/feedback", feedback));
        Verify();
    }
}
