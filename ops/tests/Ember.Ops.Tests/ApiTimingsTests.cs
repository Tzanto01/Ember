using System.Net;
using System.Text;
using System.Text.Json;
using Ember.Ops.Lookout;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Ember.Ops.Tests;

/// <summary>The pull of Ember's API timings, the hourly upsert into emberops and what the health page makes of it. Now is 2026-11-18 10:00Z.</summary>
[Collection(V2Collection.Name)]
public sealed class ApiTimingsTests(V2DatabaseFixture fixture) : IAsyncLifetime
{
    private const string Url = "http://api.example.test:9090/metrics/v1";
    private const string Key = "synthetic-metrics-key-0123456789abcdef";
    private readonly string _keyFile = Path.GetTempFileName();
    private readonly StubApi _api = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero));
    private OpsDatabases _db = null!;

    public async Task InitializeAsync()
    {
        await File.WriteAllTextAsync(_keyFile, Key + "\n");
        _db = fixture.OpenOpsDatabases();
        await OpsMigrator.ApplyAsync(_db.Ops);
        await RunAsync("TRUNCATE api_hours");
    }

    public async Task DisposeAsync()
    {
        await RunAsync("TRUNCATE api_hours");
        await _db.DisposeAsync();
        File.Delete(_keyFile);
    }

    private ApiTimings Timings(string? keyFile) =>
        new(Options.Create(new OpsOptions { ApiMetricsUrl = Url, ApiMetricsKeyFile = keyFile }), _db, new StubFactory(_api), _time, NullLogger<ApiTimings>.Instance);

    private async Task RunAsync(string sql, params object[] parameters)
    {
        await using var connection = await fixture.OpenAsync("emberops");
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters) command.Parameters.Add(new NpgsqlParameter { Value = p });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> StoredAsync()
    {
        await using var connection = await fixture.OpenAsync("emberops");
        await using var command = new NpgsqlCommand(
            "SELECT to_char(hour AT TIME ZONE 'UTC', 'HH24:MI') || ' ' || route || ' ' || requests || ' ' || errors || ' ' || p50 || ' ' || p95 FROM api_hours ORDER BY hour, route",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    /// <summary>A minute of one route with <paramref name="counts"/> requests per latency bucket.</summary>
    private static StubMinute Minute(string minute, string route, int errors, params int[] counts)
    {
        var latency = new int[ApiTimings.BucketsMs.Length + 1];
        counts.CopyTo(latency, 0);
        return new(DateTime.Parse(minute, null, System.Globalization.DateTimeStyles.AdjustToUniversal), route, new { minute, route, count = latency.Sum(), errors, latency });
    }

    [Fact]
    public void Percentiles_interpolate_inside_the_bucket_and_cap_at_the_last_bound()
    {
        int[] H(params (int Bucket, int Count)[] counts)
        {
            var h = new int[ApiTimings.BucketsMs.Length + 1];
            foreach (var (bucket, count) in counts) h[bucket] = count;
            return h;
        }

        ApiTimings.Percentile(H((0, 10)), 0.5).Should().Be(2.5, "half of the 0 to 5 ms bucket");
        ApiTimings.Percentile(H((0, 50), (4, 50)), 0.95).Should().Be(50 + 50 * 0.9, "the 95th of 100 sits 45 of 50 into the 50 to 100 ms bucket");
        ApiTimings.Percentile(H((13, 4)), 0.5).Should().Be(5000);
        ApiTimings.Percentile(H(), 0.95).Should().Be(0);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_pulled_and_the_timings_are_unavailable()
    {
        var timings = Timings(keyFile: null);

        await timings.PullAsync(default);

        _api.Requests.Should().BeEmpty();
        (await timings.ReadAsync(default)).Unavailable.Should().Be(ApiTimings.NotReporting);
        LookoutPages.Health(await timings.ReadAsync(default)).Widgets.Should().OnlyContain(w => w.Unavailable == ApiTimings.NotReporting);
    }

    [Fact]
    public async Task A_pull_stores_each_hour_and_route_with_its_percentiles_and_sends_the_key_and_since()
    {
        _api.Minutes =
        [
            Minute("2026-11-18T09:10:00Z", "/api/habits", 0, 10),
            Minute("2026-11-18T09:40:00Z", "/api/habits", 1, 0, 0, 0, 0, 10),
            Minute("2026-11-18T09:59:00Z", "/api/habits/{id:int}", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4),
            Minute("2026-11-18T10:00:00Z", "/api/habits", 0, 2),
        ];
        var timings = Timings(_keyFile);

        await timings.PullAsync(default);

        timings.Error.Should().BeNull();
        _api.Requests.Should().HaveCount(25, "one window per hour from 24 hours back up to this one").And.OnlyContain(q => q.Authorization == "Bearer " + Key);
        _api.Requests[0].Uri.Should().Be(Url + "?since=2026-11-17T10:00:00.0000000Z&until=2026-11-17T11:00:00.0000000Z");
        (await StoredAsync()).Should().Equal(
            "09:00 /api/habits 20 1 5 95",
            "09:00 /api/habits/{id:int} 4 0 625 737.5",
            "10:00 /api/habits 2 0 2.5 4.75");

        await timings.PullAsync(default);
        _api.Requests.Skip(25).Single().Uri.Should().EndWith("since=2026-11-18T10:00:00.0000000Z&until=2026-11-18T11:00:00.0000000Z",
            "the next pull starts at the newest stored hour");
    }

    [Fact]
    public async Task An_hour_is_only_replaced_by_one_with_at_least_as_many_requests_and_rows_older_than_30_days_go()
    {
        await RunAsync("INSERT INTO api_hours VALUES ($1, '/old', 1, 0, 1, 1, '{1,0,0,0,0,0,0,0,0,0,0,0,0,0}')", new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc));
        var timings = Timings(_keyFile);
        _api.Minutes = [Minute("2026-11-18T09:10:00Z", "/api/habits", 0, 10)];
        await timings.PullAsync(default);

        _api.Minutes = [Minute("2026-11-18T09:50:00Z", "/api/habits", 0, 0, 5)];
        await timings.PullAsync(default);
        (await StoredAsync()).Should().Equal(new[] { "09:00 /api/habits 10 0 2.5 4.75" }, "the API restarted and reports fewer requests for that hour");

        _api.Minutes = [Minute("2026-11-18T09:10:00Z", "/api/habits", 0, 10), Minute("2026-11-18T09:50:00Z", "/api/habits", 0, 0, 5)];
        await timings.PullAsync(default);
        (await StoredAsync()).Should().Equal("09:00 /api/habits 15 0 3.75 9.25");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null, "answered 401")]
    [InlineData(HttpStatusCode.OK, """{"bucketsMs":[1,2,3],"minutes":[]}""", "unknown latency buckets")]
    [InlineData(HttpStatusCode.OK, "not json", "invalid")]
    public async Task A_refused_or_unreadable_answer_makes_the_timings_unavailable_with_the_reason(HttpStatusCode status, string? body, string reason)
    {
        _api.Status = status;
        _api.Body = body ?? "";
        var timings = Timings(_keyFile);

        await timings.PullAsync(default);

        timings.Error.Should().StartWith(ApiTimings.NotReporting).And.Contain(reason);
        (await StoredAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_health_page_shows_7_days_in_2_hour_slots_the_last_24_hours_and_the_slowest_routes()
    {
        _api.Minutes =
        [
            Minute("2026-11-18T09:10:00Z", "/api/habits", 1, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            Minute("2026-11-18T08:10:00Z", "/api/stats", 0, 0, 0, 0, 0, 0, 0, 0, 0, 2),
            Minute("2026-11-17T12:10:00Z", "/api/me", 0, 0, 0, 0, 10, 10),
        ];
        var timings = Timings(_keyFile);
        await timings.PullAsync(default);
        await RunAsync("INSERT INTO api_hours VALUES ($1, '/api/me', 5, 5, 1, 1, '{5,0,0,0,0,0,0,0,0,0,0,0,0,0}')", new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc));

        var health = await timings.ReadAsync(default);
        var page = LookoutPages.Health(health);

        page.Widgets.Select(w => w.Id).Should().Equal("api", "requests", "latency", "errors", "slowest");
        health.Slots.Should().HaveCount(84);
        health.Slots[^1].Start.Should().Be(new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc));
        var latency = (BarsWidget)page.Widgets.Single(w => w.Id == "latency");
        latency.Bars.Select(b => b.Label).Should().Equal("11-12 09:00", "11-17 13:00", "11-18 09:00");
        latency.Better.Should().Be("down");
        var errors = (BarsWidget)page.Widgets.Single(w => w.Id == "errors");
        errors.Bars.Should().HaveCount(84);
        errors.Bars.Single(b => b.Label == "11-18 09:00").Values.Should().Equal(Math.Round(100.0 / 12, 2));
        var tiles = ((StatsWidget)page.Widgets[0]).Items;
        tiles.Select(i => i.Label).Should().Equal("Requests per minute", "p50", "API p95", "5xx rate");
        tiles[3].Value.Should().Be(1d / 32, "the 32 requests of the last 24 hours had one 5xx; 2026-11-12 is older");
        ((StatsWidget)page.Widgets.Single(w => w.Id == "slowest")).Items.Select(i => (i.Label, i.Value)).Should().Equal(
            ("/api/stats", 490d), ("/api/me", 95d), ("/api/habits", 5d));
    }

    [Fact]
    public async Task A_failed_window_keeps_the_windows_before_it_and_the_next_pull_resumes_from_the_newest_stored_hour()
    {
        _api.Minutes = [Minute("2026-11-18T07:10:00Z", "/api/habits", 0, 10), Minute("2026-11-18T08:10:00Z", "/api/habits", 0, 4)];
        _api.FailSince = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
        var timings = Timings(_keyFile);

        await timings.PullAsync(default);

        timings.Error.Should().Contain("answered 500");
        (await StoredAsync()).Should().Equal("07:00 /api/habits 10 0 2.5 4.75", "08:00 /api/habits 4 0 2.5 4.75");
        _api.Requests.Should().HaveCount(24, "the windows from 2026-11-17 10:00 to 09:00, the last of which failed");

        _api.FailSince = null;
        await timings.PullAsync(default);

        timings.Error.Should().BeNull();
        _api.Requests.Skip(24).Select(q => q.Uri[(Url.Length + 7)..(Url.Length + 26)]).Should().Equal("2026-11-18T08:00:00", "2026-11-18T09:00:00", "2026-11-18T10:00:00");
    }

    [Fact]
    public async Task A_cut_short_answer_is_continued_from_its_last_minute_and_that_minute_counts_once()
    {
        _api.Minutes =
        [
            Minute("2026-11-18T09:10:00Z", "/a", 0, 1), Minute("2026-11-18T09:10:00Z", "/b", 0, 1),
            Minute("2026-11-18T09:20:00Z", "/a", 0, 1), Minute("2026-11-18T09:20:00Z", "/b", 0, 1),
        ];
        _api.PageSize = 3;
        var timings = Timings(_keyFile);

        await timings.PullAsync(default);

        timings.Error.Should().BeNull();
        _api.Requests.Where(q => q.Uri.Contains("until=2026-11-18T10:00")).Select(q => q.Uri[(Url.Length + 7)..(Url.Length + 26)])
            .Should().Equal("2026-11-18T09:00:00", "2026-11-18T09:20:00");
        (await StoredAsync()).Should().Equal("09:00 /a 2 0 2.5 4.75", "09:00 /b 2 0 2.5 4.75");
    }

    [Fact]
    public async Task A_minute_with_more_routes_than_one_answer_holds_stops_the_pull_instead_of_looping()
    {
        _api.Minutes = [Minute("2026-11-18T09:10:00Z", "/a", 0, 1), Minute("2026-11-18T09:10:00Z", "/b", 0, 1)];
        _api.PageSize = 1;
        var timings = Timings(_keyFile);

        await timings.PullAsync(default);

        timings.Error.Should().Contain("more routes than the API returns at once");
    }

    private sealed record StubMinute(DateTime At, string Route, object Json);

    /// <summary>Answers like the API: the minutes from <c>since</c> to before <c>until</c>, oldest first, at most <see cref="PageSize"/> with <c>truncated</c>.</summary>
    private sealed class StubApi : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public List<StubMinute> Minutes { get; set; } = [];

        public int PageSize { get; set; } = int.MaxValue;

        /// <summary>A raw body instead of <see cref="Minutes"/>.</summary>
        public string? Body { get; set; }

        /// <summary>Answers 500 to the window starting here.</summary>
        public DateTime? FailSince { get; set; }

        public List<(string Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            Requests.Add((uri, request.Headers.Authorization?.ToString()));
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var since = DateTime.Parse(query["since"]!, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            var until = DateTime.Parse(query["until"]!, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            if (since == FailSince) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var matching = Minutes.Where(m => m.At >= since && m.At < until).OrderBy(m => m.At).ThenBy(m => m.Route, StringComparer.Ordinal).ToList();
            var body = Body ?? JsonSerializer.Serialize(
                new { bucketsMs = ApiTimings.BucketsMs, minutes = matching.Take(PageSize).Select(m => m.Json), truncated = matching.Count > PageSize },
                JsonSerializerOptions.Web);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
