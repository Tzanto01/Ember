using Ember.Core.Rules;

namespace Ember.Api.Feedback;

/// <summary>
/// Runs <see cref="FeedbackRetention"/> every day at <see cref="RunAt"/> Europe/Berlin time (daylight saving included), each run in
/// its own scope; a failed run is logged and the next day's runs as usual. Held back, with a warning, while
/// <see cref="EnabledSetting"/> is false. A value of either setting that is no boolean stops the API at startup.
/// </summary>
internal sealed class FeedbackRetentionService(
    IServiceScopeFactory scopes, IConfiguration configuration, TimeProvider time, ILogger<FeedbackRetentionService> logger) : BackgroundService
{
    public const string EnabledSetting = "Feedback:RetentionEnabled";

    public static readonly TimeOnly RunAt = new(8, 0);

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private readonly bool _enabled = Enabled(configuration);

    private static bool Enabled(IConfiguration configuration)
    {
        _ = configuration.GetValue(FeedbackRetention.DeleteSentCopiesSetting, false);
        return configuration.GetValue(EnabledSetting, true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
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
