using Ember.Api.Email;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Api.Tests;

public sealed class RecordingEmailTransport : IEmailTransport
{
    public List<SentMail> Sent { get; } = [];

    /// <summary>Fails every send except the digest-failed alert.</summary>
    public bool Fail { get; set; }

    public Task SendAsync(string to, string subject, string textBody, CancellationToken cancellationToken)
    {
        if (Fail && !subject.StartsWith("Ember feedback digest failed", StringComparison.Ordinal)) throw new InvalidOperationException("SMTP down");
        Sent.Add(new SentMail(to, subject, textBody));
        return Task.CompletedTask;
    }
}

public abstract class FeedbackDigestTestBase(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected const string Recipient = "ops@example.test";

    protected RecordingEmailTransport Transport { get; } = new();

    protected async Task RunDigest()
    {
        using var scope = Host.Services.CreateScope();
        await ActivatorUtilities.CreateInstance<FeedbackDigest>(scope.ServiceProvider, (IEmailTransport)Transport).RunAsync(default);
    }

    protected Task Submit(string text, Guid? userId = null, string platform = "web", string? version = "1.0", string? title = null,
        IReadOnlyList<Ember.Core.Models.FeedbackImage>? images = null)
        => Host.Run(s => s.SaveFeedbackAsync(userId, platform, version, text, title: title, images: images));

    protected Task<List<Ember.Core.Models.Feedback>> Stored()
        => Host.Db(db => db.Feedback.AsNoTracking().OrderBy(f => f.Id).ToListAsync());

    protected string BerlinNow()
        => TimeZoneInfo.ConvertTimeFromUtc(Host.Time.GetUtcNow().UtcDateTime, FeedbackDigest.Zone)
            .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

[Collection("Postgres")]
public class FeedbackDigestTests(PostgresFixture postgres) : FeedbackDigestTestBase(postgres)
{
    protected override Dictionary<string, string?>? Settings => new() { ["Feedback:DigestTo"] = Recipient };

    [DockerFact]
    public async Task One_email_counts_all_pending_feedback_with_the_oldest_and_newest_time_and_the_next_run_sends_nothing()
    {
        var oldest = BerlinNow();
        await Submit("first thing");
        Host.Time.Advance(TimeSpan.FromMinutes(5));
        var newest = BerlinNow();
        await Submit("second thing", platform: "android", version: null);

        await RunDigest();
        await RunDigest();

        var mail = Transport.Sent.Should().ContainSingle().Subject;
        mail.To.Should().Be(Recipient);
        mail.Subject.Should().Be("Ember feedback: 2 new");
        mail.Body.Should().Contain("New feedback: 2").And.Contain($"{oldest} to {newest}").And.Contain("Read them on the Lookout board.");
        (await Stored()).Should().OnlyContain(f => f.DigestedAt != null);
    }

    [DockerFact]
    public async Task The_email_holds_no_title_text_picture_id_platform_or_version()
    {
        await Submit("my meds make me dizzy, see https://evil.example.test/x", platform: "android", version: "1.4.2", title: "Dizzy after meds",
            images: [new Ember.Core.Models.FeedbackImage { Id = Guid.NewGuid(), ContentType = "image/png", Size = 3, Data = [1, 2, 3] }]);
        var pictureId = (await Host.Db(db => db.FeedbackImages.AsNoTracking().Select(i => i.Id).ToListAsync())).Single();

        await RunDigest();

        var body = Transport.Sent.Should().ContainSingle().Subject.Body;
        body.Should().Contain("New feedback: 1");
        body.Should().NotContain("Dizzy").And.NotContain("meds").And.NotContain("evil").And.NotContain(pictureId.ToString())
            .And.NotContain("android").And.NotContain("1.4.2").And.NotContain("#");
    }

    [DockerFact]
    public async Task Feedback_that_arrives_after_a_digest_goes_out_in_the_next_one_alone()
    {
        await Submit("old");
        await RunDigest();
        await Submit("new");

        await RunDigest();

        Transport.Sent.Should().HaveCount(2);
        Transport.Sent[1].Body.Should().Contain("New feedback: 1");
    }

    [DockerFact]
    public async Task Nothing_is_sent_when_no_feedback_is_pending()
    {
        await RunDigest();

        Transport.Sent.Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_failed_send_marks_nothing_and_the_next_run_sends_the_same_feedback()
    {
        await Submit("keep me");
        Transport.Fail = true;

        await RunDigest();

        (await Stored()).Should().ContainSingle().Which.DigestedAt.Should().BeNull();
        Transport.Fail = false;
        Transport.Sent.Clear();
        await RunDigest();
        Transport.Sent.Should().ContainSingle().Which.Body.Should().Contain("New feedback: 1");
        (await Stored()).Should().OnlyContain(f => f.DigestedAt != null);
    }

    [DockerFact]
    public async Task A_failed_send_mails_one_alert_to_the_recipient_without_any_feedback_content()
    {
        await Submit("secret words");
        Transport.Fail = true;

        await RunDigest();

        var alert = Transport.Sent.Should().ContainSingle().Subject;
        alert.To.Should().Be(Recipient);
        alert.Subject.Should().Be("Ember feedback digest failed");
        alert.Body.Should().NotContain("secret words");
    }

    [DockerFact]
    public async Task Feedback_older_than_12_months_is_deleted_and_not_mailed_while_newer_is_kept()
    {
        await Submit("ancient");
        Host.Time.Advance(TimeSpan.FromDays(366));
        await Submit("recent");

        await RunDigest();

        (await Stored()).Should().ContainSingle().Which.Text.Should().Be("recent");
        Transport.Sent.Should().ContainSingle().Which.Body.Should().Contain("New feedback: 1");
    }

    [DockerFact]
    public async Task Feedback_just_under_12_months_old_is_kept()
    {
        await Submit("almost a year");
        Host.Time.Advance(TimeSpan.FromDays(360));

        await RunDigest();

        (await Stored()).Should().ContainSingle().Which.Text.Should().Be("almost a year");
    }

    [DockerFact]
    public async Task Digested_feedback_is_deleted_after_12_months_too()
    {
        await Submit("sent long ago");
        await RunDigest();
        Host.Time.Advance(TimeSpan.FromDays(366));

        await RunDigest();

        (await Stored()).Should().BeEmpty();
        Transport.Sent.Should().ContainSingle();
    }

    [DockerFact]
    public async Task Over_300_pending_the_first_email_has_300_and_says_how_many_remain_and_the_next_run_sends_the_rest()
    {
        await Host.Db(async db =>
        {
            var now = Host.Time.GetUtcNow().UtcDateTime;
            db.Feedback.AddRange(Enumerable.Range(0, 305).Select(i => new Ember.Core.Models.Feedback
            {
                Platform = "web", Text = $"item-{i:000}", CreatedAt = now.AddSeconds(i),
            }));
            await db.SaveChangesAsync();
            return 0;
        });

        await RunDigest();
        await RunDigest();
        await RunDigest();

        Transport.Sent.Should().HaveCount(2);
        var (first, second) = (Transport.Sent[0], Transport.Sent[1]);
        first.Subject.Should().Be("Ember feedback: 300 new");
        first.Body.Should().Contain("New feedback: 300").And.Contain("5 more pending");
        second.Body.Should().Contain("New feedback: 5").And.NotContain("more pending");
        (await Stored()).Should().OnlyContain(f => f.DigestedAt != null);
    }

    [DockerFact]
    public async Task The_email_holds_no_account_id_and_no_email_address()
    {
        var user = await Host.SignedInAsync(consent: false);
        var userId = await user.IdAsync();
        await Submit("from an account", userId);

        await RunDigest();

        var body = Transport.Sent.Should().ContainSingle().Subject.Body;
        body.Should().NotContain(userId.ToString()).And.NotContain(userId.ToString("N"));
        body.Should().NotContain(user.Email).And.NotContain("@");
    }
}

[Collection("Postgres")]
public class FeedbackDigestWithoutRecipientTests(PostgresFixture postgres) : FeedbackDigestTestBase(postgres)
{
    [DockerFact]
    public async Task Without_a_recipient_nothing_is_sent_and_the_feedback_stays_pending()
    {
        await Submit("waiting");

        await RunDigest();
        await RunDigest();

        Transport.Sent.Should().BeEmpty();
        (await Stored()).Should().ContainSingle().Which.DigestedAt.Should().BeNull();
    }

    [DockerFact]
    public async Task Without_a_recipient_old_feedback_is_still_deleted()
    {
        await Submit("ancient");
        Host.Time.Advance(TimeSpan.FromDays(366));

        await RunDigest();

        (await Stored()).Should().BeEmpty();
    }
}
