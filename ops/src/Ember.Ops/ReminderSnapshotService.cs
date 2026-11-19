namespace Ember.Ops;

/// <summary>
/// Every five minutes stores the day's and week's sign-in and retention counts in emberops (<see cref="InsightQueries.SnapshotActivityAsync"/>)
/// and Ember's API request timings (<see cref="ApiTimings.PullAsync"/>), and removes comments and card positions of deleted feedback
/// (<see cref="FeedbackStore.PurgeOrphansAsync"/>).
/// </summary>
public sealed class ReminderSnapshotService(OpsDatabases db, ApiTimings timings, TimeProvider time, ILogger<ReminderSnapshotService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await InsightQueries.SnapshotActivityAsync(db.Ember, db.Ops, time, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Activity snapshot failed");
            }
            await timings.PullAsync(stoppingToken);
            try
            {
                await FeedbackStore.PurgeOrphansAsync(db, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Feedback comment purge failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
