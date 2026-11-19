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

/// <summary>The key in a file (at least 32 bytes), kept in memory and read again only when a bearer does not match it.</summary>
public sealed class MetricsKey(string? keyFile)
{
    private volatile byte[]? _hash;

    /// <summary>True when the bearer of <paramref name="authorization"/> equals the key, compared in constant time.</summary>
    public bool Matches(string? authorization)
    {
        const string scheme = "Bearer ";
        if (authorization is null || !authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var sent = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[scheme.Length..]));
        if (_hash is { } cached && CryptographicOperations.FixedTimeEquals(sent, cached)) return true;
        _hash = ApiMetrics.ReadKey(keyFile);
        return _hash is { } fresh && CryptographicOperations.FixedTimeEquals(sent, fresh);
    }
}

/// <summary>One minute of one route template: requests, 5xx responses and requests per <see cref="RequestMetrics.BucketsMs"/> bucket (the last is above all bounds).</summary>
public sealed record MinuteTimings(DateTime Minute, string Route, int Count, int Errors, int[] Latency);

/// <param name="Truncated">True when more minutes matched than <see cref="RequestMetrics.MaxCells"/>; <paramref name="Minutes"/> are then the oldest.</param>
public sealed record MetricsSnapshot(int[] BucketsMs, IReadOnlyList<MinuteTimings> Minutes, bool Truncated);

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

    /// <summary>The most cells one snapshot returns.</summary>
    public const int MaxCells = 20_000;

    /// <summary>
    /// The minutes of the last 24 hours from <paramref name="since"/> on and before <paramref name="until"/>, oldest first, at most
    /// <paramref name="max"/> cells. Only the copy happens under the lock that <see cref="Record"/> takes.
    /// </summary>
    public MetricsSnapshot Snapshot(DateTime? since, DateTime? until = null, int max = MaxCells)
    {
        var cutoff = time.GetUtcNow().UtcDateTime - Keep;
        var copied = new List<MinuteTimings>();
        lock (_lock)
            foreach (var (key, cell) in _cells)
                if (key.Minute > cutoff && (since is null || key.Minute >= since) && (until is null || key.Minute < until))
                    copied.Add(new(key.Minute, key.Route, cell.Count, cell.Errors, [.. cell.Latency]));
        var sorted = copied.OrderBy(m => m.Minute).ThenBy(m => m.Route, StringComparer.Ordinal);
        return new(BucketsMs, sorted.Take(max).ToList(), copied.Count > max);
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
        var key = new MetricsKey(configuration["Ops:MetricsKeyFile"]);
        var port = configuration.GetValue("Ops:MetricsPort", 9090);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, port));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet(ApiMetrics.Path, (HttpRequest request, DateTime? since, DateTime? until) =>
            key.Matches(request.Headers.Authorization.Count == 1 ? request.Headers.Authorization[0] : null)
                ? Results.Ok(metrics.Snapshot(since?.ToUniversalTime(), until?.ToUniversalTime()))
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
