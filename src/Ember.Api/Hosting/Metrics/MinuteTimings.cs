namespace Ember.Api.Hosting.Metrics;

/// <summary>One minute of one route template: requests, 5xx responses and requests per <see cref="RequestMetrics.BucketsMs"/> bucket (the last is above all bounds).</summary>
public sealed record MinuteTimings(DateTime Minute, string Route, int Count, int Errors, int[] Latency);
