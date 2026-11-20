using System.Security.Cryptography;
using System.Text;

namespace Ember.Api.Hosting.Metrics;

/// <summary>
/// Request timings for Ember ops: per minute and route template (never a raw path, id or query value) the request count, the 5xx count and
/// a latency histogram, kept 24 hours in memory and served at <c>GET /metrics/v1</c> on a port of its own (<c>Ops:MetricsPort</c>, default 9090)
/// (<c>?since=&amp;until=</c>, at most <see cref="RequestMetrics.MaxCells"/> cells, oldest first) to a bearer equal to the key in <c>Ops:MetricsKeyFile</c>. Without a key file configured nothing is recorded and the port stays closed.
/// </summary>
public static class ApiMetrics
{
    public const string Path = "/metrics/v1";
    public const int MinKeyBytes = 32;

    public static WebApplicationBuilder Add(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<RequestMetrics>();
        builder.Services.AddTransient<IStartupFilter, RequestMetricsStartupFilter>();
        builder.Services.AddHostedService<MetricsServer>();
        return builder;
    }

    /// <summary>Whether <c>Ops:MetricsKeyFile</c> is set.</summary>
    public static bool Enabled(IConfiguration configuration) => !string.IsNullOrWhiteSpace(configuration["Ops:MetricsKeyFile"]);

    internal static byte[]? ReadKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var key = Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
            return key.Length >= MinKeyBytes ? SHA256.HashData(key) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
