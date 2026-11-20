using System.Net;

namespace Ember.Api.Tests.Contract;

/// <summary>Feedback from signed-in and anonymous senders, and the push key of an app without Web Push configured.</summary>
[ContractArea("feedback")]
public class FeedbackContractTests(PostgresFixture postgres) : ContractTestBase(postgres)
{
    private const string Feedback = "/api/feedback";

    private sealed record Body(string? Title, string? Text, string? Platform, string? Version = null, string?[]? Images = null, bool? Anonymous = null);

    private static Body Valid(string? title = "Crash on start", string? platform = "web", string?[]? images = null, bool? anonymous = null)
        => new(title, "It closes right away", platform, "1.2.3", images, anonymous);

    private static string Picture() => Convert.ToBase64String(SyntheticImages.Jpeg());

    [DockerFact]
    public async Task Feedback_exchanges_match_the_golden()
    {
        var anonymous = Host.NewClient();
        var user = await Host.SignedInAsync();
        await Rec("vapid.unconfigured", "/api/push/vapid-public-key", anonymous.Get("/api/push/vapid-public-key"));

        await Rec("post.anonymous.ok", Feedback, anonymous.Post(Feedback, Valid()));
        await Rec("post.android.ok", Feedback, anonymous.Post(Feedback, Valid(platform: "android")));
        await Rec("post.signed-in.ok", Feedback, user.Post(Feedback, Valid()));
        await Rec("post.signed-in.anonymously", Feedback, user.Post(Feedback, Valid(anonymous: true)));
        await Rec("post.without-title", Feedback, anonymous.Post(Feedback, new Body(null, "First line\nsecond line", "web")));
        await Rec("post.minimal", Feedback, anonymous.Post(Feedback, new { text = "Just text", platform = "web" }));
        await Rec("post.with-pictures", Feedback, user.Post(Feedback, Valid(images: [Picture(), Picture()])));

        await Rec("post.invalid", Feedback, anonymous.Post(Feedback, new Body(new string('t', 200), "", "ios", "bad version!")));
        await Rec("post.title-two-lines", Feedback, anonymous.Post(Feedback, Valid(title: "one\ntwo")));
        await Rec("post.text-too-long", Feedback, anonymous.Post(Feedback, new Body("Title", new string('a', 5001), "web")));
        await Rec("post.anonymous.pictures", Feedback, anonymous.Post(Feedback, Valid(images: [Picture()])));
        await Rec("post.too-many-pictures", Feedback, user.Post(Feedback, Valid(images: Enumerable.Repeat(Picture(), 20).ToArray())));
        await Rec("post.empty-picture", Feedback, user.Post(Feedback, Valid(images: [" "])));
        await Rec("post.unreadable-picture", Feedback, user.Post(Feedback, Valid(images: ["bm90IGEgcGljdHVyZQ=="])));
        await Rec("post.wrong-type", Feedback, anonymous.Post(Feedback, new { text = 5, platform = "web" }));
        await Rec("post.not-json", Feedback, anonymous.Http.PostAsync(Feedback, new StringContent("text=hi", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")));
        await Rec("post.cookie-without-token", Feedback, CookieWithoutToken(user, Valid()));
        Verify();
    }

    private async Task<HttpResponseMessage> CookieWithoutToken(ApiClient user, Body body)
    {
        var client = Host.NewClient();
        await (await client.Post("/api/auth/login?useCookies=true", new Credentials(user.Email, ApiClient.Passphrase))).Expect(HttpStatusCode.OK);
        return await client.Post(Feedback, body);
    }
}
