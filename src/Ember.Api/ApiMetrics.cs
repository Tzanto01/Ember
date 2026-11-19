using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Ember.Api;

/// <summary>
/// Request timings for Ember ops: per minute and route template (never a raw path, id or query value) the request count, the 5xx count and
/// a latency histogram, kept 24 hours in memory and served at <c>GET /metrics/v1</c> on a port of its own (<c>Ops:MetricsPort</c>, default 9090)
/// to a bearer equal to the key in <c>Ops:MetricsKeyFile</c>. Without a key file configured nothing is recorded and the port stays closed.
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

    /// <summary>True when the bearer of <paramref name="authorization"/> equals the key in <paramref name="keyFile"/> (at least 32 bytes), in constant time.</summary>
    public static bool KeyMatches(string? keyFile, string? authorization)
    {
        const string scheme = "Bearer ";
        if (ReadKey(keyFile) is not { } key || authorization is null || !authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(authorization[scheme.Length..])), SHA256.HashData(key));
    }

    private static byte[]? ReadKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var key = Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
            return key.Length >= MinKeyBytes ? key : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>One minute of one route template: requests, 5xx responses and requests per <see cref="RequestMetrics.BucketsMs"/> bucket (the last is above all bounds).</summary>
public sealed record MinuteTimings(DateTime Minute, string Route, int Count, int Errors, int[] Latency);

public sealed record MetricsSnapshot(int[] BucketsMs, IReadOnlyList<MinuteTimings> Minutes);

/// <summary>The last 24 hours of request timings, per minute and route template. At most 1,440 minutes times the app's route templates.</summary>
public sealed class RequestMetrics(TimeProvider time)
{
    /// <summary>Upper bounds of the latency buckets in milliseconds; one more bucket counts everything slower.</summary>
    public static readonly int[] BucketsMs = [5, 10, 25, 50, 100, 150, 200, 300, 500, 750, 1000, 2000, 5000];

    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);

    /// <summary>The route of requests that matched no endpoint, such as static files.</summary>
    public const string NoRoute = "(static or unmatched)";

    private sealed class Cell
    {
        public int Count;
        public int Errors;
        public readonly int[] Latency = new int[BucketsMs.Length + 1];
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<(DateTime Minute, string Route), Cell> _cells = [];
    private DateTime _pruned;

    public void Record(string? route, int status, TimeSpan elapsed)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var bucket = Array.FindIndex(BucketsMs, b => elapsed.TotalMilliseconds <= b);
        lock (_lock)
        {
            if (minute != _pruned)
            {
                foreach (var key in _cells.Keys.Where(k => k.Minute <= minute - Keep).ToList()) _cells.Remove(key);
                _pruned = minute;
            }
            if (!_cells.TryGetValue((minute, route ?? NoRoute), out var cell)) _cells[(minute, route ?? NoRoute)] = cell = new();
            cell.Count++;
            if (status >= 500) cell.Errors++;
            cell.Latency[bucket < 0 ? BucketsMs.Length : bucket]++;
        }
    }

    /// <summary>The minutes of the last 24 hours from <paramref name="since"/> on, oldest first.</summary>
    public MetricsSnapshot Snapshot(DateTime? since)
    {
        var cutoff = time.GetUtcNow().UtcDateTime - Keep;
        lock (_lock)
            return new(BucketsMs, _cells.Where(c => c.Key.Minute > cutoff && (since is null || c.Key.Minute >= since))
                .OrderBy(c => c.Key.Minute).ThenBy(c => c.Key.Route, StringComparer.Ordinal)
                .Select(c => new MinuteTimings(c.Key.Minute, c.Key.Route, c.Value.Count, c.Value.Errors, [.. c.Value.Latency])).ToList());
    }
}

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

/// <summary>Serves <see cref="ApiMetrics.Path"/> on its own Kestrel port, apart from the app's routes.</summary>
public sealed class MetricsServer(RequestMetrics metrics, IConfiguration configuration, ILogger<MetricsServer> logger) : IHostedService, IAsyncDisposable
{
    private WebApplication? _app;

    /// <summary>The address the server listens on once started.</summary>
    public string? Address { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ApiMetrics.Enabled(configuration)) return;
        var keyFile = configuration["Ops:MetricsKeyFile"];
        var port = configuration.GetValue("Ops:MetricsPort", 9090);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, port));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet(ApiMetrics.Path, (HttpRequest request, DateTime? since) =>
            ApiMetrics.KeyMatches(keyFile, request.Headers.Authorization.Count == 1 ? request.Headers.Authorization[0] : null)
                ? Results.Ok(metrics.Snapshot(since?.ToUniversalTime()))
                : Results.StatusCode(StatusCodes.Status401Unauthorized));
        await app.StartAsync(cancellationToken);
        _app = app;
        Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        logger.LogInformation("Request metrics on {Address}{Path}", Address, ApiMetrics.Path);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null) await _app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
