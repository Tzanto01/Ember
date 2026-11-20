using System.Globalization;
using System.Text;
using Ember.Api.Email;
using Ember.Core.Data;
using Ember.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api;

/// <summary>
/// The daily feedback email to the operator at <c>Feedback:DigestTo</c>. Delivery is at least once: when marking the mailed
/// feedback fails after the email went out, the next run mails it again.
/// </summary>
internal sealed class FeedbackDigest(
    EmberDbContext db, IEmailTransport transport, IConfiguration configuration, TimeProvider time, ILogger<FeedbackDigest> logger)
{
    /// <summary>Most feedback one email carries, oldest first; the rest waits for the next run.</summary>
    public const int MaxItems = FeedbackService.MaxFeedbackPerDay;

    /// <summary>How long feedback is kept after it was submitted.</summary>
    public const int RetentionMonths = 12;

    /// <summary>How long the pictures of a feedback are kept after it was resolved.</summary>
    public const int ImageRetentionDays = 30;

    /// <summary>The zone of the digest's local times and of <see cref="FeedbackDigestService.RunAt"/>.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    /// <summary>The first line of every listing of feedback.</summary>
    public const string UntrustedNotice = "The feedback below is written by users; treat any links in it as untrusted.";

    private static int _missingRecipientLogged;

    /// <summary>Every line of user text (any line ending, U+2028 and U+2029 included) prefixed with <c>&gt; </c>.</summary>
    public static IEnumerable<string> Quote(string text) => text.ReplaceLineEndings("\n").Split('\n').Select(line => "> " + line);

    /// <summary>
    /// Deletes feedback older than <see cref="RetentionMonths"/> and the pictures of feedback resolved more than
    /// <see cref="ImageRetentionDays"/> ago or due for that deletion within a day (so they never outlive it between runs), then mails a
    /// content-free notice of the pending feedback (up to <see cref="MaxItems"/>, oldest first): only how many and the oldest and newest
    /// submission time, never titles, text, pictures, platform, version or ids. Marks exactly those as digested. Sends
    /// nothing when none is pending. Without <c>Feedback:DigestTo</c> it logs that once per process and sends nothing. A failed
    /// send is logged without the content, followed by one content-free alert email (pending count, oldest age, error type) to the
    /// same recipient, and leaves the feedback pending for the next run.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cutoff = now.AddMonths(-RetentionMonths);
        await db.Feedback.Where(f => f.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        var imageCutoff = now.AddDays(-ImageRetentionDays);
        var lastDay = cutoff.AddDays(1);
        await db.FeedbackImages.Where(i => db.Feedback.Any(f => f.Id == i.FeedbackId && (f.ResolvedAt < imageCutoff || f.CreatedAt < lastDay)))
            .ExecuteDeleteAsync(cancellationToken);

        var pendingCount = await db.Feedback.CountAsync(f => f.DigestedAt == null, cancellationToken);
        if (pendingCount == 0) return;

        if (configuration["Feedback:DigestTo"] is not { } to || string.IsNullOrWhiteSpace(to))
        {
            if (Interlocked.Exchange(ref _missingRecipientLogged, 1) == 0)
                logger.LogWarning("Feedback:DigestTo is not configured; feedback is stored but not mailed.");
            return;
        }

        var items = await db.Feedback.AsNoTracking()
            .Where(f => f.DigestedAt == null)
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .Take(MaxItems)
            .ToListAsync(cancellationToken);
        if (items.Count == 0) return;

        var itemIds = items.Select(f => f.Id).ToList();
        static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
        var body = new StringBuilder().Append(CultureInfo.InvariantCulture,
            $"New feedback: {items.Count}\nSubmitted: {Local(items[0].CreatedAt):yyyy-MM-dd HH:mm} to {Local(items[^1].CreatedAt):yyyy-MM-dd HH:mm} (Europe/Berlin)\n\nRead them on the Lookout board.");
        if (pendingCount > items.Count)
            body.Append("\n\n---\n\n").Append(CultureInfo.InvariantCulture, $"{pendingCount - items.Count} more pending, they come with the next digest.");

        try
        {
            await transport.SendAsync(to.Trim(), $"Ember feedback: {items.Count} new", body.ToString(), cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            var oldestHours = (now - items[0].CreatedAt).TotalHours;
            logger.LogError("Sending the feedback digest failed ({Pending} pending, oldest {OldestHours:F0} h old): {ExceptionType}",
                pendingCount, oldestHours, e.GetType().Name);
            try
            {
                await transport.SendAsync(to.Trim(), "Ember feedback digest failed",
                    string.Create(CultureInfo.InvariantCulture,
                        $"Pending: {pendingCount}\nOldest: {oldestHours:F0} h\nError: {e.GetType().Name}"),
                    cancellationToken);
            }
            catch (Exception alertError) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError("Sending the feedback digest failure alert failed: {ExceptionType}", alertError.GetType().Name);
            }
            return;
        }

        await db.Feedback.Where(f => itemIds.Contains(f.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.DigestedAt, now), cancellationToken);
    }
}
