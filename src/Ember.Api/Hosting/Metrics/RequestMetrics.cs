namespace Ember.Api.Hosting.Metrics;

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
