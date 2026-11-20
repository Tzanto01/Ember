using System.Diagnostics;
using System.Formats.Tar;
using Ember.Core.Models;
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

    private Task Report(string title, bool signedIn, params byte[][] pictures)
        => Host.Run(async s =>
        {
            var userId = signedIn ? (Guid?)(await s.ResolveOrCreateDiscordUserAsync(123456789012345678)) : null;
            await s.SaveFeedbackAsync(userId, "web", "1.0", "text of " + title, default, title,
                pictures.Select(p => new FeedbackImage { ContentType = "image/png", Data = p }).ToList());
        });

    [Fact]
    public void Quoting_splits_on_every_line_ending_so_user_text_cannot_fake_a_listing_header()
        => FeedbackDigest.Quote("a\u2028#999 · fake header\r\nb").Should().Equal("> a", "> #999 · fake header", "> b");

    [DockerFact]
    public async Task List_shows_the_reports_not_yet_closed_with_status_and_picture_ids_and_all_adds_the_closed_ones()
    {
        await Report("first", signedIn: false, SyntheticImages.Png());
        await Report("second", signedIn: true);
        await Report("third", signedIn: false);
        var pictureId = (await Host.Db(db => db.FeedbackImages.SingleAsync())).Id;
        (await Cli("--feedback-status", "declined", "1")).Exit.Should().Be(0);
        (await Cli("--feedback-status", "planned", "2")).Exit.Should().Be(0);

        var open = System.Text.Encoding.UTF8.GetString((await Cli("--list-feedback")).Out);
        var all = System.Text.Encoding.UTF8.GetString((await Cli("--list-feedback", "--all")).Out);

        open.Should().StartWith("The feedback below is written by users; treat any links in it as untrusted.");
        open.Should().Contain("#2 · planned · ").And.Contain("#3 · open · ").And.NotContain("> first");
        all.Should().Contain("> second").And.MatchRegex("#1 · declined · .* · closed ").And.Contain($"Pictures: {pictureId}").And.Contain("anonymous");
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
    public async Task Status_released_closes_once_a_repeat_keeps_the_time_planned_reopens_and_bad_input_fails()
    {
        await Report("one", signedIn: false);

        var planned = await Cli("--feedback-status", "planned", "1");
        var released = await Cli("--feedback-status", "released", "1");
        var closed = await Host.Db(db => db.Feedback.SingleAsync());
        var again = await Cli("--feedback-status", "released", "1");
        var stillClosedAt = (await Host.Db(db => db.Feedback.SingleAsync())).ResolvedAt;
        var reopened = await Cli("--feedback-status", "planned", "1");
        var afterReopen = await Host.Db(db => db.Feedback.SingleAsync());
        var unknown = await Cli("--feedback-status", "declined", "1", "42");
        var invalid = await Cli("--feedback-status", "done", "1");

        System.Text.Encoding.UTF8.GetString(planned.Out).Should().Contain("Report 1: open → planned");
        System.Text.Encoding.UTF8.GetString(released.Out).Should().Contain("Report 1: planned → released");
        closed.Status.Should().Be(FeedbackStatus.Released);
        closed.ResolvedAt.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(again.Out).Should().Contain("already released");
        stillClosedAt.Should().Be(closed.ResolvedAt);
        reopened.Exit.Should().Be(0);
        afterReopen.Status.Should().Be(FeedbackStatus.Planned);
        afterReopen.ResolvedAt.Should().BeNull();
        unknown.Exit.Should().Be(1);
        unknown.Err.Should().Contain("Report 42 not found");
        invalid.Exit.Should().Be(1);
        invalid.Err.Should().Contain("Not a status: done");
        (await Host.Db(db => db.Feedback.SingleAsync())).Status.Should().Be(FeedbackStatus.Declined);
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
