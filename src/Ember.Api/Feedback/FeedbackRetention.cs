using Ember.Core.Data;
using Ember.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Feedback;

/// <summary>Deletes feedback Ember no longer keeps. Lookout has its own retention.</summary>
internal sealed class FeedbackRetention(EmberDbContext db, IConfiguration configuration, TimeProvider time, ILogger<FeedbackRetention> logger)
{
    /// <summary>How long feedback is kept after it was submitted.</summary>
    public const int Months = 12;

    /// <summary>How long the pictures of a report Lookout confirmed are kept after it was submitted.</summary>
    public const int ImageDays = 30;

    /// <summary>
    /// Whether the copies Lookout confirmed are deleted right away (by the Lookout sender, and here for those it missed or sent before
    /// this was on): anonymous reports whole, every report's pictures. Off by default.
    /// </summary>
    public const string DeleteSentCopiesSetting = "Feedback:DeleteSentCopies";

    /// <summary>
    /// Deletes feedback older than <see cref="Months"/>; a report Lookout never confirmed goes with its outbox row, and their count is
    /// logged. Deletes pictures a day before their report reaches <see cref="Months"/>, and those of a confirmed report a day before it
    /// reaches <see cref="ImageDays"/>, so a daily run never lets them outlive either. With <see cref="DeleteSentCopiesSetting"/> deletes
    /// confirmed anonymous reports and every picture of a confirmed report. Deletes intake counts older than <see cref="FeedbackService.LimitWindow"/>.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var deleteSentCopies = configuration.GetValue(DeleteSentCopiesSetting, false);
        var now = time.GetUtcNow().UtcDateTime;
        var cutoff = now.AddMonths(-Months);
        var unsent = await db.LookoutOutbox.CountAsync(o => o.SentAt == null && o.ParkedAt == null && o.Feedback!.CreatedAt < cutoff, cancellationToken);
        await db.Feedback.Where(f => f.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        if (unsent > 0) logger.LogWarning("Deleted {Count} feedback reports after {Months} months that Lookout never received.", unsent, Months);

        var sent = db.Feedback.Where(f => db.LookoutOutbox.Any(o => o.FeedbackId == f.Id && o.SentAt != null));
        if (deleteSentCopies) await sent.Where(f => f.UserId == null).ExecuteDeleteAsync(cancellationToken);

        var lastDay = cutoff.AddDays(1);
        var imageCutoff = now.AddDays(1 - ImageDays);
        await db.FeedbackImages
            .Where(i => db.Feedback.Any(f => f.Id == i.FeedbackId && f.CreatedAt < lastDay)
                || sent.Any(f => f.Id == i.FeedbackId && (deleteSentCopies || f.CreatedAt < imageCutoff)))
            .ExecuteDeleteAsync(cancellationToken);

        var window = now - FeedbackService.LimitWindow;
        await db.FeedbackIntake.Where(i => i.CreatedAt <= window).ExecuteDeleteAsync(cancellationToken);
    }
}
