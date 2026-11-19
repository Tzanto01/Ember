using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ember.Ops;

/// <summary>Requests, 5xx responses and response time percentiles in milliseconds of one route template or of all of them.</summary>
public sealed record RouteTiming(string Route, int Requests, int Errors, double P50, double P95);

/// <summary>One slot of <see cref="ApiTimings.SlotHours"/> hours from <paramref name="Start"/>, <paramref name="Minutes"/> of which have passed.</summary>
public sealed record TimingSlot(DateTime Start, double Minutes, RouteTiming Timing);

/// <summary>Ember's API timings: the last 7 days in slots, the last 24 hours in total and the 10 slowest route templates of those hours.</summary>
/// <param name="Unavailable">Why there are none; null after a good pull.</param>
public sealed record ApiHealth(DateTime GeneratedAt, string? Unavailable, IReadOnlyList<TimingSlot> Slots, TimingSlot? Day, IReadOnlyList<RouteTiming> Slowest);

/// <summary>
/// Pulls the request timings Ember's API keeps per minute and route template (24 hours, in memory) and stores them per hour in
/// <c>api_hours</c> for 30 days. Counts and latency histograms only; the API never reports paths, ids or query values.
/// </summary>
public sealed class ApiTimings(IOptions<OpsOptions> options, OpsDatabases db, IHttpClientFactory http, TimeProvider time, ILogger<ApiTimings> logger)
{
    public const string HttpClientName = "api-metrics";
    public const string NotReporting = "Ember's API doesn't report timings yet";
    public const int SlotHours = 2;
    public const int Days = 7;
    public const int Slowest = 10;
    public const string AllRoutes = "All routes";
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    /// <summary>Upper bounds in milliseconds of the API's latency buckets, plus one bucket above them; the pull refuses any other layout.</summary>
    public static readonly int[] BucketsMs = [5, 10, 25, 50, 100, 150, 200, 300, 500, 750, 1000, 2000, 5000];

    private sealed record Pulled(int[] BucketsMs, List<PulledMinute> Minutes);

    private sealed record PulledMinute(DateTime Minute, string Route, int Count, int Errors, int[] Latency);

    private volatile string? _error = NotReporting;

    /// <summary>Why the last pull brought nothing, or null after a good one.</summary>
    public string? Error => _error;

    /// <summary>Pulls since the newest stored hour and upserts every hour it covers; an hour is replaced only by one with at least as many requests.</summary>
    public async Task PullAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (ReadKey(o.ApiMetricsKeyFile) is not { } key)
        {
            _error = NotReporting;
            return;
        }
        try
        {
            await PullAsync(o.ApiMetricsUrl, key, ct);
            _error = null;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("API timings pull failed: {Reason}", e.Message);
            _error = e is TaskCanceledException ? $"{NotReporting} (timed out)" : $"{NotReporting} ({e.Message})";
        }
    }

    private async Task PullAsync(string url, string key, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        DateTime since;
        await using (var command = db.Ops.CreateCommand("SELECT max(hour) FROM api_hours"))
            since = await command.ExecuteScalarAsync(ct) is DateTime newest && newest > now.AddDays(-1) ? newest : now.AddDays(-1);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{url}?since={Uri.EscapeDataString(since.ToString("O"))}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"answered {(int)response.StatusCode}");
        var pulled = await response.Content.ReadFromJsonAsync<Pulled>(JsonSerializerOptions.Web, ct)
            ?? throw new InvalidDataException("empty answer");
        if (!pulled.BucketsMs.SequenceEqual(BucketsMs) || pulled.Minutes.Any(m => m.Latency.Length != BucketsMs.Length + 1))
            throw new InvalidDataException("unknown latency buckets");

        var hours = pulled.Minutes.GroupBy(m => (Hour: Hour(m.Minute.ToUniversalTime()), m.Route)).Select(g =>
        {
            var latency = Merge(g.Select(m => m.Latency));
            return Db.Command("""
                INSERT INTO api_hours (hour, route, requests, errors, p50, p95, latency) VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (hour, route) DO UPDATE SET requests = excluded.requests, errors = excluded.errors, p50 = excluded.p50, p95 = excluded.p95,
                    latency = excluded.latency
                WHERE excluded.requests >= api_hours.requests
                """, g.Key.Hour, g.Key.Route, g.Sum(m => m.Count), g.Sum(m => m.Errors), Percentile(latency, 0.5), Percentile(latency, 0.95), latency);
        });
        await using var connection = await db.Ops.OpenConnectionAsync(ct);
        await using var reader = await Db.RunAsync(connection, [.. hours, Db.Command("DELETE FROM api_hours WHERE hour < $1", now - Keep)], ct);
    }

    /// <summary>The stored timings of the last 7 days, or why there are none.</summary>
    public async Task<ApiHealth> ReadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (_error is { } error) return new(now, error, [], null, []);

        var last = new DateTime(now.Ticks - now.Ticks % TimeSpan.FromHours(SlotHours).Ticks, DateTimeKind.Utc);
        var first = last.AddHours(-SlotHours * (Days * 24 / SlotHours - 1));
        var rows = new List<(DateTime Hour, string Route, int Requests, int Errors, int[] Latency)>();
        await using (var command = db.Ops.CreateCommand("SELECT hour, route, requests, errors, latency FROM api_hours WHERE hour >= $1"))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = first });
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetDateTime(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetFieldValue<int[]>(4)));
        }

        var slots = Enumerable.Range(0, Days * 24 / SlotHours).Select(i => first.AddHours(SlotHours * i)).Select(start =>
            new TimingSlot(start, Math.Min(SlotHours * 60, (now - start).TotalMinutes), Timing(AllRoutes, rows.Where(r => r.Hour >= start && r.Hour < start.AddHours(SlotHours))))).ToList();
        var dayStart = Hour(now).AddHours(-23);
        var day = rows.Where(r => r.Hour >= dayStart).ToList();
        return new(now, null, slots, new(dayStart, (now - dayStart).TotalMinutes, Timing(AllRoutes, day)),
            day.GroupBy(r => r.Route).Select(g => Timing(g.Key, g)).OrderByDescending(t => t.P95).ThenBy(t => t.Route, StringComparer.Ordinal).Take(Slowest).ToList());
    }

    /// <summary>
    /// The <paramref name="q"/> quantile in milliseconds of a latency histogram, interpolated inside its bucket; requests above the last bound
    /// count as that bound. 0 without requests.
    /// </summary>
    public static double Percentile(int[] latency, double q)
    {
        var total = latency.Sum();
        if (total == 0) return 0;
        var rank = q * total;
        double seen = 0;
        for (var i = 0; i < latency.Length; i++)
        {
            if (latency[i] == 0 || seen + latency[i] < rank)
            {
                seen += latency[i];
                continue;
            }
            if (i >= BucketsMs.Length) return BucketsMs[^1];
            double lower = i == 0 ? 0 : BucketsMs[i - 1];
            return lower + (BucketsMs[i] - lower) * (rank - seen) / latency[i];
        }
        return BucketsMs[^1];
    }

    private static RouteTiming Timing(string route, IEnumerable<(DateTime Hour, string Route, int Requests, int Errors, int[] Latency)> rows)
    {
        var list = rows.ToList();
        var latency = Merge(list.Select(r => r.Latency));
        return new(route, list.Sum(r => r.Requests), list.Sum(r => r.Errors), Percentile(latency, 0.5), Percentile(latency, 0.95));
    }

    private static int[] Merge(IEnumerable<int[]> histograms)
    {
        var merged = new int[BucketsMs.Length + 1];
        foreach (var h in histograms)
            for (var i = 0; i < merged.Length && i < h.Length; i++) merged[i] += h[i];
        return merged;
    }

    private static DateTime Hour(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    private static string? ReadKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var key = File.ReadAllText(path).Trim();
            return key.Length > 0 ? key : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
