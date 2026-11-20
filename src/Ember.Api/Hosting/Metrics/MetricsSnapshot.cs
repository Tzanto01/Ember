namespace Ember.Api;

/// <param name="Truncated">True when more minutes matched than <see cref="RequestMetrics.MaxCells"/>; <paramref name="Minutes"/> are then the oldest.</param>
public sealed record MetricsSnapshot(int[] BucketsMs, IReadOnlyList<MinuteTimings> Minutes, bool Truncated);
