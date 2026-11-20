using System.Formats.Tar;
using System.Globalization;
using Ember.Core;
using Ember.Core.Data;
using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Feedback;

/// <summary>
/// Operator commands on the stored feedback, run inside the API container (<c>docker compose exec -T api dotnet Ember.Api.dll
/// ...</c>) against <c>ConnectionStrings:DefaultConnection</c> from the environment, without starting the web host. Never
/// reachable over HTTP. Any failure prints the exception type and message (no stack trace) to stderr and exits 1.
/// <list type="bullet">
/// <item><c>--list-feedback [--all]</c>: read-only; prints the untrusted-content notice, then the open, planned and implemented reports (with <c>--all</c> every report), oldest
/// first: id, status, created and closed time (UTC), platform, version, <c>user {id}</c> or <c>anonymous</c>, the title and text quoted with
/// <c>&gt; </c> and the picture ids. Exit 0.</item>
/// <item><c>--feedback-images &lt;id&gt;...</c>: each id a report id (integer, all its pictures) or a picture id (GUID); writes a
/// tar stream to stdout with entries <c>{reportId}/{pictureId}.{jpg|png|webp}</c>, the stored (already stripped) bytes. Unknown
/// ids and reports without pictures go to stderr; exit 1 when nothing was found.</item>
/// <item><c>--feedback-status &lt;open|planned|implemented|released|declined&gt; &lt;reportId&gt;...</c>: sets each report's
/// <see cref="Feedback.Status"/>. Released or declined closes it (sets <see cref="Feedback.ResolvedAt"/> to now unless already
/// closed), which starts the <see cref="FeedbackDigest.ImageRetentionDays"/> until its pictures are deleted; any other status
/// clears it. Prints each change or that the report already has the status; an invalid status and invalid or unknown ids go to
/// stderr; exit 1 when any input was invalid.</item>
/// </list>
/// </summary>
internal static class FeedbackCli
{
    /// <summary>Whether <paramref name="args"/> start with one of the verbs.</summary>
    public static bool Handles(string[] args) => args is ["--list-feedback", ..] or ["--feedback-images", ..] or ["--feedback-status", ..];

    /// <summary>Runs the verb in <paramref name="args"/> and returns the exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var options = ServiceCollectionExtensions.EmberDbOptions(configuration);
            await using var db = new EmberDbContext(options);
            return args switch
            {
                ["--list-feedback"] => await ListAsync(db, all: false),
                ["--list-feedback", "--all"] => await ListAsync(db, all: true),
                ["--feedback-images", _, ..] => await ImagesAsync(db, args[1..]),
                ["--feedback-status", _, _, ..] => await StatusAsync(db, args[1], args[2..]),
                _ => Usage(),
            };
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: --list-feedback [--all] | --feedback-images <reportId|pictureId>... | --feedback-status <open|planned|implemented|released|declined> <reportId>...");
        return 1;
    }

    private static async Task<int> ListAsync(EmberDbContext db, bool all)
    {
        var items = await db.Feedback.AsNoTracking()
            .Where(f => all || (f.Status != FeedbackStatus.Released && f.Status != FeedbackStatus.Declined))
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .Select(f => new { f.Id, f.Status, f.CreatedAt, f.ResolvedAt, f.Platform, f.Version, f.UserId, f.Title, f.Text, Pictures = f.Images.OrderBy(i => i.Id).Select(i => i.Id).ToList() })
            .ToListAsync();
        var output = Console.Out;
        await output.WriteLineAsync(FeedbackDigest.UntrustedNotice);
        await output.WriteLineAsync();
        foreach (var item in items)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"#{item.Id} · {Name(item.Status)} · {Utc(item.CreatedAt)}{(item.ResolvedAt is { } closed ? " · closed " + Utc(closed) : "")} · {item.Platform} {item.Version ?? "unknown version"} · {(item.UserId is { } userId ? $"user {userId}" : "anonymous")}"));
            foreach (var line in FeedbackDigest.Quote(item.Title)) await output.WriteLineAsync(line);
            await output.WriteLineAsync(">");
            foreach (var line in FeedbackDigest.Quote(item.Text)) await output.WriteLineAsync(line);
            if (item.Pictures.Count > 0) await output.WriteLineAsync("Pictures: " + string.Join(", ", item.Pictures));
            await output.WriteLineAsync();
        }

        return 0;
    }

    private static async Task<int> ImagesAsync(EmberDbContext db, string[] ids)
    {
        var reportIds = new List<int>();
        var pictureIds = new List<Guid>();
        foreach (var id in ids)
        {
            if (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var reportId)) reportIds.Add(reportId);
            else if (Guid.TryParse(id, out var pictureId)) pictureIds.Add(pictureId);
            else await Console.Error.WriteLineAsync($"Not a report or picture id: {id}");
        }

        var found = await db.FeedbackImages.AsNoTracking()
            .Where(i => reportIds.Contains(i.FeedbackId) || pictureIds.Contains(i.Id))
            .OrderBy(i => i.FeedbackId).ThenBy(i => i.Id)
            .Select(i => new { i.Id, i.FeedbackId, i.ContentType })
            .ToListAsync();
        var existingReports = await db.Feedback.Where(f => reportIds.Contains(f.Id)).Select(f => f.Id).ToListAsync();
        foreach (var reportId in reportIds.Distinct())
        {
            if (!existingReports.Contains(reportId)) await Console.Error.WriteLineAsync($"Report {reportId} not found.");
            else if (!found.Any(i => i.FeedbackId == reportId)) await Console.Error.WriteLineAsync($"Report {reportId} has no pictures.");
        }
        foreach (var pictureId in pictureIds.Distinct().Where(p => !found.Any(i => i.Id == p)))
            await Console.Error.WriteLineAsync($"Picture {pictureId} not found.");
        if (found.Count == 0) return 1;

        await using var tar = new TarWriter(Console.OpenStandardOutput(), TarEntryFormat.Pax, leaveOpen: false);
        foreach (var image in found.DistinctBy(i => i.Id))
        {
            var data = await db.FeedbackImages.Where(i => i.Id == image.Id).Select(i => i.Data).FirstOrDefaultAsync();
            if (data is null) continue;

            var extension = image.ContentType switch { "image/png" => "png", "image/webp" => "webp", _ => "jpg" };
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, $"{image.FeedbackId}/{image.Id}.{extension}")
            {
                DataStream = new MemoryStream(data, writable: false),
            });
        }

        return 0;
    }

    private static async Task<int> StatusAsync(EmberDbContext db, string statusName, string[] ids)
    {
        if (Enum.GetValues<FeedbackStatus>().Where(s => Name(s) == statusName).Select(s => (FeedbackStatus?)s).FirstOrDefault() is not { } status)
        {
            await Console.Error.WriteLineAsync($"Not a status: {statusName} (open, planned, implemented, released or declined)");
            return 1;
        }

        var closes = status is FeedbackStatus.Released or FeedbackStatus.Declined;
        var exitCode = 0;
        foreach (var id in ids)
        {
            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var reportId))
            {
                await Console.Error.WriteLineAsync($"Not a report id: {id}");
                exitCode = 1;
                continue;
            }

            var report = await db.Feedback.FindAsync(reportId);
            if (report is null)
            {
                await Console.Error.WriteLineAsync($"Report {reportId} not found.");
                exitCode = 1;
            }
            else if (report.Status == status)
                await Console.Out.WriteLineAsync($"Report {reportId} is already {Name(status)}.");
            else
            {
                var previous = report.Status;
                report.Status = status;
                report.ResolvedAt = closes ? report.ResolvedAt ?? DateTime.UtcNow : null;
                await db.SaveChangesAsync();
                await Console.Out.WriteLineAsync($"Report {reportId}: {Name(previous)} → {Name(status)}");
            }
        }

        return exitCode;
    }

    private static string Name(FeedbackStatus status) => status.ToString().ToLowerInvariant();

    private static string Utc(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
