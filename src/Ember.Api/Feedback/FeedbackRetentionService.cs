using Ember.Core.Rules;

namespace Ember.Api.Feedback;

/// <summary>
/// Runs <see cref="FeedbackRetention"/> every day at <see cref="RunAt"/> Europe/Berlin time (daylight saving included), each run in
/// its own scope; a failed run is logged and the next day's runs as usual. Held back, with a warning, while
/// <see cref="EnabledSetting"/> is false.
/// </summary>
internal sealed class FeedbackRetentionService(
    IServiceScopeFactory scopes, IConfiguration configuration, TimeProvider time, ILogger<FeedbackRetentionService> logger) : BackgroundService
{
    public const string EnabledSetting = "Feedback:RetentionEnabled";

    public static readonly TimeOnly RunAt = new(8, 0);

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue(EnabledSetting, true))
        {
            logger.LogWarning("Feedback retention is off ({Setting}); no feedback is deleted on schedule.", EnabledSetting);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow().UtcDateTime;
            await Task.Delay(TimeZones.NextOccurrenceUtc(RunAt, Zone, now) - now, time, stoppingToken);
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<FeedbackRetention>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError("The feedback retention failed: {ExceptionType}", e.GetType().Name);
            }
        }
    }
}
