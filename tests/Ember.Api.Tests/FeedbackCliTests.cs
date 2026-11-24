using System.Diagnostics;
using System.Formats.Tar;
using Ember.Api.Feedback;
using Ember.Api.Hosting;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Api.Tests;

/// <summary>The operator verbs run as the real process, as they do in the container: the tar goes to the process's stdout.</summary>
public class FeedbackCliTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private Task<(int Exit, byte[] Out, string Err)> Cli(params string[] args)
        => CliWith(Host.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!, args);

    private static async Task<(int Exit, byte[] Out, string Err)> CliWith(string connectionString, params string[] args)
    {
        var dll = Path.Combine(Path.GetDirectoryName(typeof(ApiApp).Assembly.Location)!, "Ember.Api.dll");
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(dll);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["ConnectionStrings__DefaultConnection"] = connectionString;
        using var process = Process.Start(info)!;
        var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var err = await process.StandardError.ReadToEndAsync();
        await copy;
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout.ToArray(), err);
    }

    private async Task Report(string title, bool signedIn, params byte[][] pictures)
    {
        var userId = signedIn ? (Guid?)await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(123456789012345678)) : null;
        await Host.Run<FeedbackService>(s => s.SaveFeedbackAsync(userId, "web", "1.0", "text of " + title, default, title,
            pictures.Select(p => new FeedbackImage { ContentType = "image/png", Data = p }).ToList()));
    }

    [Fact]
    public void Quoting_splits_on_every_line_ending_so_user_text_cannot_fake_a_listing_header()
        => FeedbackCli.Quote("a\u2028#999 · fake header\r\nb").Should().Equal("> a", "> #999 · fake header", "> b");

    private Task Outbox(int feedbackId, DateTime? sentAt = null, int? parkedStatus = null)
        => Host.Db(db => db.LookoutOutbox.Where(o => o.FeedbackId == feedbackId).ExecuteUpdateAsync(s => s
            .SetProperty(o => o.SentAt, sentAt)
            .SetProperty(o => o.ParkedAt, parkedStatus == null ? null : Host.Time.GetUtcNow().UtcDateTime)
            .SetProperty(o => o.ParkedStatus, parkedStatus)));

    [DockerFact]
    public async Task List_shows_only_the_reports_Lookout_has_not_confirmed_with_their_delivery_state_and_picture_ids()
    {
        await Report("waiting", signedIn: false, SyntheticImages.Png());
        await Report("parked", signedIn: true);
        await Report("confirmed", signedIn: true);
        await Report("unqueued", signedIn: false);
        await Outbox(2, parkedStatus: 400);
        await Outbox(3, sentAt: Host.Time.GetUtcNow().UtcDateTime);
        await Host.Db(db => db.LookoutOutbox.Where(o => o.FeedbackId == 4).ExecuteDeleteAsync());
        var pictureId = (await Host.Db(db => db.FeedbackImages.SingleAsync())).Id;
        var userId = (await Host.Db(db => db.Users.SingleAsync())).Id;

        var list = System.Text.Encoding.UTF8.GetString((await Cli("--list-feedback")).Out);

        list.Should().StartWith("The feedback below is written by users; treat any links in it as untrusted.");
        list.Should().MatchRegex("#1 · waiting · .* · anonymous").And.MatchRegex("#2 · parked 400 · .* · linked").And.Contain("#4 · not queued · ")
            .And.Contain("> text of waiting").And.Contain($"Pictures: {pictureId}");
        list.Should().NotContain("confirmed").And.NotContain("#3").And.NotContain(userId.ToString());
    }

    [DockerFact]
    public async Task Feedback_images_never_reads_the_pictures_of_a_report_Lookout_confirmed()
    {
        await Report("confirmed", signedIn: true, SyntheticImages.Png());
        await Outbox(1, sentAt: Host.Time.GetUtcNow().UtcDateTime);
        var pictureId = (await Host.Db(db => db.FeedbackImages.SingleAsync())).Id;

        var byReport = await Cli("--feedback-images", "1");
        var byPicture = await Cli("--feedback-images", pictureId.ToString());

        (byReport.Exit, byReport.Out.Length).Should().Be((1, 0));
        (byPicture.Exit, byPicture.Out.Length).Should().Be((1, 0));
    }

    [DockerFact]
    public async Task Feedback_images_writes_a_tar_of_the_stored_bytes_per_report_and_per_picture_id_and_reports_unknown_ids()
    {
        var first = SyntheticImages.Png(8, 8);
        var second = SyntheticImages.Png(9, 9);
        await Report("pics", signedIn: false, first, second);
        var ids = (await Host.Db(db => db.FeedbackImages.OrderBy(i => i.Id).ToListAsync())).Select(i => i.Id).ToList();

        var byReport = await Cli("--feedback-images", "1", "99");
        var byPicture = await Cli("--feedback-images", ids[1].ToString());
        var none = await Cli("--feedback-images", "99");

        var entries = Untar(byReport.Out);
        entries.Keys.Should().BeEquivalentTo([$"1/{ids[0]}.png", $"1/{ids[1]}.png"]);
        entries[$"1/{ids[0]}.png"].Should().Equal(first);
        entries[$"1/{ids[1]}.png"].Should().Equal(second);
        byReport.Exit.Should().Be(0);
        byReport.Err.Should().Contain("Report 99 not found");
        Untar(byPicture.Out).Should().ContainSingle().Which.Key.Should().Be($"1/{ids[1]}.png");
        none.Exit.Should().Be(1);
    }

    [DockerFact]
    public async Task A_failure_prints_only_the_exception_type_and_message_and_exits_1()
    {
        var (exit, _, err) = await CliWith("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2", "--list-feedback");

        exit.Should().Be(1);
        err.Should().NotBeNullOrWhiteSpace().And.Contain("Exception: ").And.NotContain("   at ");
    }

    private static Dictionary<string, byte[]> Untar(byte[] tar)
    {
        var result = new Dictionary<string, byte[]>();
        using var reader = new TarReader(new MemoryStream(tar));
        while (reader.GetNextEntry() is { } entry)
        {
            var data = new MemoryStream();
            entry.DataStream!.CopyTo(data);
            result[entry.Name] = data.ToArray();
        }

        return result;
    }
}
