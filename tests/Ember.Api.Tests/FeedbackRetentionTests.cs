using Ember.Api.Feedback;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Api.Tests;

public abstract class FeedbackRetentionTestBase(PostgresFixture postgres) : ApiTestBase(postgres)
{
    internal CapturingLogger<FeedbackRetention> Logs { get; } = new();

    protected DateTime Now => Host.Time.GetUtcNow().UtcDateTime;

    protected async Task RunRetention()
    {
        using var scope = Host.Services.CreateScope();
        await ActivatorUtilities.CreateInstance<FeedbackRetention>(scope.ServiceProvider, Logs).RunAsync(default);
    }

    protected Task<Guid> NewUser() => Host.Service(s => s.ResolveOrCreateDiscordUserAsync(ApiHost.NewDiscordId()));

    protected async Task<int> Report(string title, Guid? userId = null, TimeSpan age = default, int pictures = 0)
    {
        await Host.Run<FeedbackService>(s => s.SaveFeedbackAsync(userId, "web", "1.0", "text of " + title, default, title,
            Enumerable.Range(0, pictures).Select(_ => new FeedbackImage { ContentType = "image/png", Data = SyntheticImages.Png() }).ToList()));
        await Host.Db(db => db.Feedback.Where(f => f.Title == title).ExecuteUpdateAsync(s => s.SetProperty(f => f.CreatedAt, f => Now - age)));
        return await Host.Db(db => db.Feedback.Where(f => f.Title == title).Select(f => f.Id).SingleAsync());
    }

    protected Task Sent(int id) => Host.Db(db => db.LookoutOutbox.Where(o => o.FeedbackId == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.SentAt, Now)));

    protected Task Parked(int id) => Host.Db(db => db.LookoutOutbox.Where(o => o.FeedbackId == id)
        .ExecuteUpdateAsync(s => s.SetProperty(o => o.ParkedAt, Now).SetProperty(o => o.ParkedStatus, 400)));

    protected Task<List<string>> Titles() => Host.Db(db => db.Feedback.OrderBy(f => f.Title).Select(f => f.Title).ToListAsync());

    protected Task<List<string>> TitlesWithPictures()
        => Host.Db(db => db.Feedback.Where(f => f.Images.Any()).OrderBy(f => f.Title).Select(f => f.Title).ToListAsync());
}

public class FeedbackRetentionTests(PostgresFixture postgres) : FeedbackRetentionTestBase(postgres)
{
    private static readonly TimeSpan OverAYear = TimeSpan.FromDays(366);
    private static readonly TimeSpan UnderAYear = TimeSpan.FromDays(360);

    [DockerFact]
    public async Task Every_report_goes_after_12_months_and_none_before_whatever_Lookout_answered()
    {
        var user = await NewUser();
        foreach (var (name, age) in new[] { ("old", OverAYear), ("young", UnderAYear) })
        {
            await Report($"{name} unsent", user, age);
            await Report($"{name} anonymous unsent", null, age);
            await Sent(await Report($"{name} sent", user, age));
            await Sent(await Report($"{name} anonymous sent", null, age));
            await Parked(await Report($"{name} parked", user, age));
        }

        await RunRetention();

        (await Titles()).Should().Equal("young anonymous sent", "young anonymous unsent", "young parked", "young sent", "young unsent");
        (await Host.Db(db => db.LookoutOutbox.CountAsync())).Should().Be(5, "each report's outbox row goes with it");
    }

    [DockerFact]
    public async Task Reports_dropped_before_Lookout_got_them_are_logged_as_a_count_only()
    {
        await Report("Secret-Old-One", await NewUser(), OverAYear);
        await Report("Secret-Old-Two", null, OverAYear);
        await Sent(await Report("Secret-Old-Sent", null, OverAYear));
        await Parked(await Report("Secret-Old-Parked", null, OverAYear));

        await RunRetention();

        Logs.All.Should().StartWith("Warning: Deleted 2 feedback reports").And.NotContain("Secret-Old");
    }

    [DockerFact]
    public async Task A_waiting_user_deletion_is_never_dropped()
    {
        var user = await NewUser();
        await Report("gone with the account", user);
        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(user));
        Host.Time.Advance(TimeSpan.FromDays(400));

        await RunRetention();

        (await Host.Db(db => db.LookoutOutbox.AsNoTracking().ToListAsync())).Should().ContainSingle().Which.Kind.Should().Be(LookoutOutbox.UserDelete);
    }

    [DockerFact]
    public async Task Pictures_go_within_the_last_day_so_they_never_outlive_the_12_months()
    {
        await Report("nearly a year", pictures: 1);
        await Host.Db(db => db.Feedback.ExecuteUpdateAsync(s => s.SetProperty(f => f.CreatedAt, Now.AddMonths(-12).AddHours(12))));
        await Report("half a year", age: TimeSpan.FromDays(180), pictures: 1);

        await RunRetention();

        (await TitlesWithPictures()).Should().Equal("half a year");
        (await Titles()).Should().HaveCount(2);
    }

    [DockerFact]
    public async Task Pictures_go_30_days_after_the_report_only_once_Lookout_confirmed_it_and_the_text_stays()
    {
        var user = await NewUser();
        foreach (var (name, age) in new[] { ("month", TimeSpan.FromDays(29.1)), ("fresh", TimeSpan.FromDays(28.9)) })
        {
            await Sent(await Report($"{name} sent", user, age, pictures: 2));
            await Report($"{name} unsent", user, age, pictures: 1);
            await Parked(await Report($"{name} parked", user, age, pictures: 1));
            var unqueued = await Report($"{name} never queued", user, age, pictures: 1);
            await Host.Db(db => db.LookoutOutbox.Where(o => o.FeedbackId == unqueued).ExecuteDeleteAsync());
        }

        await RunRetention();

        (await TitlesWithPictures()).Should().Equal("fresh never queued", "fresh parked", "fresh sent", "fresh unsent", "month never queued", "month parked", "month unsent");
        (await Titles()).Should().HaveCount(8);
    }

    [DockerFact]
    public async Task Confirmed_anonymous_reports_stay_while_the_setting_is_off()
    {
        await Sent(await Report("anonymous sent"));

        await RunRetention();

        (await Titles()).Should().Equal("anonymous sent");
    }

    [DockerFact]
    public async Task Only_intake_counts_outside_the_24_hour_window_are_deleted()
    {
        await Report("yesterday");
        Host.Time.Advance(TimeSpan.FromHours(23));
        await Report("today");
        Host.Time.Advance(TimeSpan.FromHours(2));

        await RunRetention();

        (await Host.Db(db => db.FeedbackIntake.CountAsync())).Should().Be(1);
    }
}

public class FeedbackEarlyDeletionTests(PostgresFixture postgres) : FeedbackRetentionTestBase(postgres)
{
    protected override Dictionary<string, string?>? Settings => new() { [FeedbackRetention.DeleteSentCopiesSetting] = "true" };

    private Task<SenderRig> Rig() => SenderRig.StartAsync(Host, extraSettings: new() { [FeedbackRetention.DeleteSentCopiesSetting] = "true" });

    [DockerFact]
    public async Task The_job_deletes_only_anonymous_reports_Lookout_confirmed()
    {
        var user = await NewUser();
        await Report("linked unsent", user);
        await Sent(await Report("linked sent", user));
        await Report("anonymous unsent");
        await Parked(await Report("anonymous parked"));
        await Sent(await Report("anonymous sent"));
        var unqueued = await Report("anonymous never queued");
        await Host.Db(db => db.LookoutOutbox.Where(o => o.FeedbackId == unqueued).ExecuteDeleteAsync());

        await RunRetention();

        (await Titles()).Should().Equal("anonymous never queued", "anonymous parked", "anonymous unsent", "linked sent", "linked unsent");
    }

    [DockerFact]
    public async Task The_sender_deletes_an_anonymous_report_with_its_outbox_row_once_Lookout_confirms_and_only_the_pictures_of_a_linked_one()
    {
        await using var rig = await Rig();
        await Report("anonymous", pictures: 1);
        await Report("linked", await NewUser(), pictures: 1);

        await rig.Step();
        await rig.Step();

        rig.Fake.Requests.Select(r => r.Json().GetProperty("title").GetString()).Should().Equal("anonymous", "linked");
        rig.Fake.Requests.Should().OnlyContain(r => r.Json().GetProperty("images").GetArrayLength() == 1);
        (await Titles()).Should().Equal("linked");
        (await Host.Db(db => db.FeedbackImages.CountAsync())).Should().Be(0);
        (await Host.Db(db => db.LookoutOutbox.AsNoTracking().ToListAsync())).Should().ContainSingle().Which.SentAt.Should().NotBeNull();
    }

    [DockerFact]
    public async Task The_job_deletes_every_confirmed_picture_at_once_and_no_other()
    {
        var user = await NewUser();
        await Sent(await Report("sent", user, pictures: 1));
        await Report("unsent", user, pictures: 1);
        await Parked(await Report("parked", user, pictures: 1));

        await RunRetention();

        (await TitlesWithPictures()).Should().Equal("parked", "unsent");
        (await Titles()).Should().HaveCount(3);
    }

    [DockerFact]
    public async Task The_sender_keeps_an_anonymous_report_that_Lookout_did_not_confirm()
    {
        await using var rig = await Rig();
        await Report("anonymous", pictures: 1);
        rig.Fake.Script(503, 204, FakeLookout.Abort, 400);

        for (var i = 0; i < 4; i++) await rig.Step();

        (await TitlesWithPictures()).Should().Equal("anonymous");
        var row = await Host.Db(db => db.LookoutOutbox.AsNoTracking().SingleAsync());
        (row.SentAt, row.ParkedStatus).Should().Be(((DateTime?)null, 400));
    }

    [DockerFact]
    public async Task Reports_and_pictures_deleted_early_still_count_toward_the_daily_limits()
    {
        await using var rig = await Rig();
        var bytes = SyntheticImages.Png().Length;
        await Host.Db(async db =>
        {
            db.FeedbackIntake.AddRange(Enumerable.Range(0, FeedbackService.MaxFeedbackPerDay - 2).Select(_ => new FeedbackIntake { CreatedAt = Now }));
            db.FeedbackIntake.Add(new FeedbackIntake { CreatedAt = Now, ImageBytes = FeedbackService.MaxFeedbackImageBytesPerDay - bytes });
            return await db.SaveChangesAsync();
        });
        await Report("last one", pictures: 1);
        await rig.Step();
        (await Titles()).Should().BeEmpty();

        Task<bool> Save(int pictures) => Host.Service<FeedbackService, bool>(s => s.SaveFeedbackAsync(null, "web", null, "more",
            images: [.. Enumerable.Range(0, pictures).Select(_ => new FeedbackImage { ContentType = "image/png", Data = SyntheticImages.Png() })]));
        (await Save(0)).Should().BeFalse("300 were accepted");
        await Host.Db(db => db.FeedbackIntake.Where(i => i.ImageBytes == 0).ExecuteDeleteAsync());
        (await Save(1)).Should().BeFalse("the pictures of the day are used up");
        (await Save(0)).Should().BeTrue();
    }

    [DockerFact]
    public async Task Deleting_an_account_does_not_free_the_daily_limits()
    {
        var user = await NewUser();
        await Report("counted", user, pictures: 1);

        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(user));

        (await Host.Db(db => db.FeedbackIntake.AsNoTracking().SingleAsync())).ImageBytes.Should().Be(SyntheticImages.Png().Length);
    }

    [DockerFact]
    public async Task Deleting_the_account_of_a_sent_report_still_queues_its_deletion_at_Lookout()
    {
        await using var rig = await Rig();
        var user = await NewUser();
        await Report("linked", user);
        await rig.Step();

        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(user));
        await rig.Step();

        (await Titles()).Should().BeEmpty();
        rig.Fake.Requests.Select(r => r.Method).Should().Equal("PUT", "DELETE");
    }
}
