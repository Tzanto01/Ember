namespace Ember.Ops;

/// <summary>
/// Every five minutes stores Ember's reminder id sequence values in emberops when they changed (<see cref="ReminderQueries.SnapshotAsync"/>),
/// the day's and week's sign-in counts (<see cref="InsightQueries.SnapshotActivityAsync"/>),
/// and removes comments and card positions of deleted feedback (<see cref="FeedbackStore.PurgeOrphansAsync"/>).
/// </summary>
public sealed class ReminderSnapshotService(OpsDatabases db, TimeProvider time, ILogger<ReminderSnapshotService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await ReminderQueries.SnapshotAsync(db.Ember, db.Ops, time, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Reminder snapshot failed");
            }
            try
            {
                await InsightQueries.SnapshotActivityAsync(db.Ember, db.Ops, time, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Activity snapshot failed");
            }
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
