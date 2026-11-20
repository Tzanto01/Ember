using System.Diagnostics;

namespace Ember.Api.Hosting.Metrics;

/// <summary>Times every request as the outermost middleware and files it under its endpoint's route template; the health check is left out.</summary>
public sealed class RequestMetricsStartupFilter(RequestMetrics metrics, IConfiguration configuration) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        if (ApiMetrics.Enabled(configuration)) app.Use(async (context, inner) =>
        {
            var start = Stopwatch.GetTimestamp();
            var failed = false;
            try
            {
                await inner(context);
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
                if (route != "/health")
                    metrics.Record(route, failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode, Stopwatch.GetElapsedTime(start));
            }
        });
        next(app);
    };
}
