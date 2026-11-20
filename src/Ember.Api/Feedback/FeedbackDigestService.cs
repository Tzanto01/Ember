using Ember.Core.Rules;

namespace Ember.Api;

/// <summary>
/// Runs <see cref="FeedbackDigest"/> every day at <see cref="RunAt"/> Europe/Berlin time (daylight saving included), each run in
/// its own scope; a failed run is logged and the next day's runs as usual, so a run missed while the API was down is caught up
/// then. Does nothing when <c>Feedback:DigestEnabled</c> is false (tests call <see cref="FeedbackDigest.RunAsync"/> directly).
/// </summary>
internal sealed class FeedbackDigestService(
    IServiceScopeFactory scopes, IConfiguration configuration, TimeProvider time, ILogger<FeedbackDigestService> logger) : BackgroundService
{
    public static readonly TimeOnly RunAt = new(8, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Feedback:DigestEnabled", true)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow().UtcDateTime;
            await Task.Delay(TimeZones.NextOccurrenceUtc(RunAt, FeedbackDigest.Zone, now) - now, time, stoppingToken);
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<FeedbackDigest>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError("The feedback digest failed: {ExceptionType}", e.GetType().Name);
            }
        }
    }
}
