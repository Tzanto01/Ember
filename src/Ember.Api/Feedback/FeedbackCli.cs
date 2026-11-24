using System.Formats.Tar;
using System.Globalization;
using Ember.Core;
using Ember.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Feedback;

/// <summary>
/// Operator commands for the reports Lookout didn't take, which can be read nowhere else: those waiting for it or parked, and those
/// without an outbox row. Run inside the API container (<c>docker compose exec -T api dotnet Ember.Api.dll ...</c>) against
/// <c>ConnectionStrings:DefaultConnection</c> from the environment, without starting the web host. Never reachable over HTTP, and
/// never reads a report Lookout confirmed. Any failure prints the exception type and message (no stack trace) to stderr and exits 1.
/// <list type="bullet">
/// <item><c>--list-feedback</c>: prints the untrusted-content notice, then those reports, oldest first: id, <c>waiting</c>,
/// <c>parked {HTTP status}</c> or <c>not queued</c>, created time (UTC), platform, version, <c>linked</c> or <c>anonymous</c>, the
/// title and text quoted with <c>&gt; </c> and the picture ids. Exit 0.</item>
/// <item><c>--feedback-images &lt;id&gt;...</c>: each id a report id (integer, all its pictures) or a picture id (GUID) of those
/// reports; writes a tar stream to stdout with entries <c>{reportId}/{pictureId}.{jpg|png|webp}</c>, the stored (already stripped)
/// bytes. Unknown ids and reports without pictures go to stderr; exit 1 when nothing was found.</item>
/// </list>
/// </summary>
internal static class FeedbackCli
{
    /// <summary>The first line of the listing.</summary>
    internal const string UntrustedNotice = "The feedback below is written by users; treat any links in it as untrusted.";

    /// <summary>Whether <paramref name="args"/> start with one of the verbs.</summary>
    public static bool Handles(string[] args) => args is ["--list-feedback", ..] or ["--feedback-images", ..];

    /// <summary>Every line of user text (any line ending, U+2028 and U+2029 included) prefixed with <c>&gt; </c>.</summary>
    internal static IEnumerable<string> Quote(string text) => text.ReplaceLineEndings("\n").Split('\n').Select(line => "> " + line);

    private static IQueryable<Ember.Core.Models.Feedback> Unconfirmed(EmberDbContext db)
        => db.Feedback.AsNoTracking().Where(f => !db.LookoutOutbox.Any(o => o.FeedbackId == f.Id && o.SentAt != null));

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
                ["--list-feedback"] => await ListAsync(db),
                ["--feedback-images", _, ..] => await ImagesAsync(db, args[1..]),
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
        Console.Error.WriteLine("Usage: --list-feedback | --feedback-images <reportId|pictureId>...");
        return 1;
    }

    private static async Task<int> ListAsync(EmberDbContext db)
    {
        var items = await Unconfirmed(db)
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .Select(f => new
            {
                f.Id,
                f.CreatedAt,
                f.Platform,
                f.Version,
                Linked = f.UserId != null,
                f.Title,
                f.Text,
                Delivery = db.LookoutOutbox.Where(o => o.FeedbackId == f.Id).Select(o => new { o.ParkedStatus }).FirstOrDefault(),
                Pictures = f.Images.OrderBy(i => i.Id).Select(i => i.Id).ToList(),
            })
            .ToListAsync();
        var output = Console.Out;
        await output.WriteLineAsync(UntrustedNotice);
        await output.WriteLineAsync();
        foreach (var item in items)
        {
            var delivery = item.Delivery is null ? "not queued" : item.Delivery.ParkedStatus is { } status ? string.Create(CultureInfo.InvariantCulture, $"parked {status}") : "waiting";
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"#{item.Id} · {delivery} · {Utc(item.CreatedAt)} · {item.Platform} {item.Version ?? "unknown version"} · {(item.Linked ? "linked" : "anonymous")}"));
            foreach (var line in Quote(item.Title)) await output.WriteLineAsync(line);
            await output.WriteLineAsync(">");
            foreach (var line in Quote(item.Text)) await output.WriteLineAsync(line);
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

        var found = await Unconfirmed(db).SelectMany(f => f.Images)
            .Where(i => reportIds.Contains(i.FeedbackId) || pictureIds.Contains(i.Id))
            .OrderBy(i => i.FeedbackId).ThenBy(i => i.Id)
            .Select(i => new { i.Id, i.FeedbackId, i.ContentType })
            .ToListAsync();
        var existingReports = await Unconfirmed(db).Where(f => reportIds.Contains(f.Id)).Select(f => f.Id).ToListAsync();
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

    private static string Utc(DateTime time) => time.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
