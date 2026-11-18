namespace Ember.Ops;

/// <summary>Every five minutes stores Ember's reminder id sequence values in emberops when they changed (<see cref="ReminderQueries.SnapshotAsync"/>).</summary>
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
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
