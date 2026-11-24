using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests;

public class FeedbackImageApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string Url = "/api/feedback";

    private static object Body(string?[]? images = null, bool? anonymous = null, string? title = "Crash on start", string? text = "It closes right away")
        => new { title, text, platform = "web", version = "1.2.3", images, anonymous };

    private Task<List<Ember.Core.Models.Feedback>> Stored() => Host.Db(db => db.Feedback.AsNoTracking().Include(f => f.Images).OrderBy(f => f.Id).ToListAsync());

    private static string B64(byte[] data) => SyntheticImages.Base64(data);

    [DockerFact]
    public async Task A_signed_in_post_with_two_pictures_stores_them_stripped_with_their_sniffed_types_in_upload_order()
    {
        var user = await Host.SignedInAsync(consent: false);

        await (await user.Post(Url, Body([B64(SyntheticImages.Jpeg(metadata: true, trailing: true)), B64(SyntheticImages.Png(metadata: true))])))
            .Expect(HttpStatusCode.NoContent);

        var item = (await Stored()).Should().ContainSingle().Subject;
        item.UserId.Should().Be(await user.IdAsync());
        item.Title.Should().Be("Crash on start");
        var images = item.Images.OrderBy(i => i.Id).ToList();
        images.Select(i => i.ContentType).Should().Equal("image/jpeg", "image/png");
        images[0].Data.Should().Equal(SyntheticImages.Jpeg());
        images[1].Data.Should().Equal(SyntheticImages.Png());
        images.Select(i => i.Size).Should().Equal(images.Select(i => i.Data.Length));
    }

    [DockerFact]
    public async Task Anonymous_true_stores_no_user_and_still_keeps_the_pictures()
    {
        var user = await Host.SignedInAsync(consent: false);

        await (await user.Post(Url, Body([B64(SyntheticImages.WebP())], anonymous: true))).Expect(HttpStatusCode.NoContent);

        var item = (await Stored()).Should().ContainSingle().Subject;
        item.UserId.Should().BeNull();
        item.Images.Should().ContainSingle().Which.ContentType.Should().Be("image/webp");
    }

    [DockerFact]
    public async Task An_anonymous_caller_with_pictures_is_rejected_on_images_and_nothing_is_stored()
    {
        var response = await Host.NewClient().Post(Url, Body([B64(SyntheticImages.Jpeg())]));

        var problem = await response.ExpectProblem(HttpStatusCode.BadRequest, "validation");
        problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Should().Equal("images");
        (await Stored()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Six_pictures_or_a_picture_that_is_not_jpeg_png_or_webp_are_rejected_on_images_and_nothing_is_stored()
    {
        var user = await Host.SignedInAsync(consent: false);
        var one = B64(SyntheticImages.Jpeg());

        foreach (var images in new[] { Enumerable.Repeat(one, 6).ToArray(), [B64(SyntheticImages.Gif())], [B64(SyntheticImages.Polyglot())], ["not base64!"] })
        {
            var problem = await (await user.Post(Url, Body(images))).ExpectProblem(HttpStatusCode.BadRequest, "validation");
            problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Should().Equal("images");
        }

        (await Stored()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task An_empty_title_is_required_and_a_title_with_a_line_break_is_refused()
    {
        foreach (var title in new[] { "", "   ", "two\nlines" })
        {
            var problem = await (await Host.NewClient().Post(Url, Body(title: title))).ExpectProblem(HttpStatusCode.BadRequest, "validation");
            problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Should().Equal("title");
        }

        (await Stored()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_title_with_a_unicode_line_or_paragraph_separator_is_refused()
    {
        foreach (var title in new[] { "one\u2028two", "one\u2029two" })
        {
            var problem = await (await Host.NewClient().Post(Url, Body(title: title))).ExpectProblem(HttpStatusCode.BadRequest, "validation");
            problem.GetProperty("errors").GetProperty("title")[0].GetString().Should().Be("Keep the title on one line.");
        }

        (await Stored()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_missing_title_from_an_older_app_is_taken_from_the_first_line_of_the_text()
    {
        await (await Host.NewClient().Post(Url, new { text = "Dark mode please\nIt hurts at night", platform = "web" })).Expect(HttpStatusCode.NoContent);

        (await Stored()).Should().ContainSingle().Which.Title.Should().Be("Dark mode please");
    }

    [DockerFact]
    public async Task A_title_is_stored_trimmed_and_100_characters_are_accepted_but_101_are_not()
    {
        await (await Host.NewClient().Post(Url, Body(title: "  " + new string('t', 100) + " "))).Expect(HttpStatusCode.NoContent);
        var problem = await (await Host.NewClient().Post(Url, Body(title: new string('t', 101)))).ExpectProblem(HttpStatusCode.BadRequest, "validation");

        problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Should().Equal("title");
        (await Stored()).Should().ContainSingle().Which.Title.Should().Be(new string('t', 100));
    }

    [DockerFact]
    public async Task A_user_over_the_30_MiB_daily_picture_budget_gets_a_429_and_nothing_more_is_stored()
    {
        var user = await Host.SignedInAsync(consent: false);
        var big = B64(SyntheticImages.Jpeg(entropyBytes: 9 * 1024 * 1024));

        for (var i = 0; i < 3; i++) await (await user.Post(Url, Body([big]))).Expect(HttpStatusCode.NoContent);
        var limited = await user.Post(Url, Body([big]));

        await limited.ExpectProblem((HttpStatusCode)429, "rate_limited");
        limited.Headers.RetryAfter.Should().NotBeNull();
        (await Stored()).Should().HaveCount(3);
        var other = await Host.SignedInAsync(consent: false);
        await (await other.Post(Url, Body([big]))).Expect(HttpStatusCode.NoContent);
    }

    [DockerFact]
    public async Task Deleting_the_account_removes_the_images_of_its_signed_in_reports_and_keeps_those_of_its_anonymous_ones()
    {
        var user = await Host.SignedInAsync(consent: false);
        var picture = B64(SyntheticImages.Jpeg());
        await (await user.Post(Url, Body([picture], title: "signed"))).Expect(HttpStatusCode.NoContent);
        await (await user.Post(Url, Body([picture], anonymous: true, title: "anon"))).Expect(HttpStatusCode.NoContent);

        await (await user.Post("/api/me/delete", new DeleteRequest(ApiClient.Passphrase, "DELETE"))).Expect(HttpStatusCode.NoContent);

        var item = (await Stored()).Should().ContainSingle().Subject;
        item.Title.Should().Be("anon");
        item.Images.Should().ContainSingle();
        (await Host.Db(db => db.FeedbackImages.CountAsync())).Should().Be(1);
    }

    [DockerFact]
    public async Task On_a_real_server_an_anonymous_body_over_64_KiB_is_refused_while_a_signed_in_one_with_a_large_picture_is_accepted()
    {
        Host.UseKestrel(0);
        var user = await KestrelUserAsync();

        var anonymous = await Host.CreateClient().PostAsJsonAsync(Url, Body(text: new string('x', 100 * 1024)));
        var large = B64(SyntheticImages.Jpeg(entropyBytes: 750 * 1024));
        var signedIn = await user.PostAsJsonAsync(Url, Body([large]));

        anonymous.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        await signedIn.Expect(HttpStatusCode.NoContent);
        (await Stored()).Should().ContainSingle().Which.Images.Should().ContainSingle().Which.Size.Should().BeGreaterThan(700 * 1024);
    }

    [DockerFact]
    public async Task A_report_from_an_account_that_no_longer_exists_is_kept_anonymous_without_its_pictures()
    {
        var picture = new FeedbackImage { ContentType = "image/jpeg", Data = SyntheticImages.Jpeg() };

        (await Host.Service<FeedbackService, bool>(s => s.SaveFeedbackAsync(Guid.NewGuid(), "web", "1.0", "It closes", default, "Crash", [picture]))).Should().BeTrue();

        var item = (await Stored()).Should().ContainSingle().Subject;
        (item.UserId, item.Title, item.Text).Should().Be(((Guid?)null, "Crash", "It closes"));
        item.Images.Should().BeEmpty();
        (await Host.Db(db => db.FeedbackImages.CountAsync())).Should().Be(0);
    }

    private async Task<HttpClient> KestrelUserAsync()
    {
        var http = Host.CreateClient();
        var email = ApiHost.NewEmail();
        await (await http.PostAsJsonAsync("/api/auth/register", new Credentials(email, ApiClient.Passphrase))).Expect(HttpStatusCode.OK);
        var link = await Host.OldConfirmLinkAsync((await Host.Mail.WaitForAsync(email, 1)).Single());
        await (await http.PostAsJsonAsync("/api/auth/confirmEmail", new ConfirmRequest(link.UserId, link.Code, ApiClient.Passphrase))).Expect(HttpStatusCode.OK);
        var tokens = await (await http.PostAsJsonAsync("/api/auth/login", new Credentials(email, ApiClient.Passphrase))).ExpectJson();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return http;
    }
}

public class FeedbackBigBodyRateLimitTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override Dictionary<string, string?>? Settings => new() { ["Ember:RateLimits:FeedbackPerHour"] = "2" };

    [DockerFact]
    public async Task Large_invalid_bodies_use_up_the_users_big_body_permits_and_then_get_a_429_while_small_reports_still_pass()
    {
        var user = await Host.SignedInAsync(consent: false);
        var large = new { title = "", text = "x", platform = "web", padding = new string('p', 100 * 1024) };

        for (var i = 0; i < 2; i++) await (await user.Post("/api/feedback", large)).ExpectProblem(HttpStatusCode.BadRequest, "validation");
        var limited = await user.Post("/api/feedback", large);

        await limited.ExpectProblem((HttpStatusCode)429, "rate_limited");
        limited.Headers.RetryAfter.Should().NotBeNull();
        var small = new HttpRequestMessage(HttpMethod.Post, "/api/feedback")
        {
            Content = new StringContent("{\"title\":\"Small\",\"text\":\"Still works\",\"platform\":\"web\"}", System.Text.Encoding.UTF8, "application/json"),
        };
        small.Headers.Add("X-XSRF-TOKEN", user.Csrf);
        await (await user.Http.SendAsync(small)).Expect(HttpStatusCode.NoContent);
    }
}
