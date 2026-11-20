using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ember.Api.Hosting.Metrics;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Api.Tests;

public sealed class ApiMetricsTests : IDisposable
{
    private const string Key = "synthetic-metrics-key-0123456789abcdef";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 10, 0, 30, TimeSpan.Zero));
    private readonly string _keyFile = Path.GetTempFileName();

    public ApiMetricsTests() => File.WriteAllText(_keyFile, Key + "\n");

    public void Dispose() => File.Delete(_keyFile);

    [Fact]
    public void Requests_are_filed_per_minute_and_route_with_their_5xx_count_and_latency_bucket()
    {
        var metrics = new RequestMetrics(_time);

        metrics.Record("/api/habits/{id:int}", 200, TimeSpan.FromMilliseconds(3));
        metrics.Record("/api/habits/{id:int}", 503, TimeSpan.FromMilliseconds(120));
        metrics.Record("/api/habits/{id:int}", 500, TimeSpan.FromSeconds(6));
        metrics.Record(null, 404, TimeSpan.FromMilliseconds(5));
        _time.Advance(TimeSpan.FromMinutes(1));
        metrics.Record("/api/habits/{id:int}", 200, TimeSpan.FromMilliseconds(5));

        var minutes = metrics.Snapshot(null).Minutes;

        minutes.Select(m => (m.Minute.Minute, m.Route, m.Count, m.Errors)).Should().Equal(
            (0, RequestMetrics.NoRoute, 1, 0), (0, "/api/habits/{id:int}", 3, 2), (1, "/api/habits/{id:int}", 1, 0));
        minutes[1].Latency.Should().Equal(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1);
        minutes[0].Latency[0].Should().Be(1, "5 ms is the upper bound of the first bucket");
        minutes[0].Minute.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Only_the_last_24_hours_are_kept_and_since_cuts_the_older_minutes()
    {
        var metrics = new RequestMetrics(_time);
        metrics.Record("/a", 200, TimeSpan.FromMilliseconds(1));
        _time.Advance(TimeSpan.FromHours(1));
        metrics.Record("/b", 200, TimeSpan.FromMilliseconds(1));

        metrics.Snapshot(new DateTime(2026, 11, 18, 10, 30, 0, DateTimeKind.Utc)).Minutes.Select(m => m.Route).Should().Equal("/b");

        _time.Advance(TimeSpan.FromHours(23));
        metrics.Snapshot(null).Minutes.Select(m => m.Route).Should().Equal("/b");
        _time.Advance(TimeSpan.FromHours(1));
        metrics.Record("/c", 200, TimeSpan.FromMilliseconds(1));
        metrics.Snapshot(null).Minutes.Select(m => m.Route).Should().Equal("/c");
    }

    [Theory]
    [InlineData("Bearer " + Key, true)]
    [InlineData("bearer " + Key, true)]
    [InlineData("Bearer wrong-key-0123456789abcdef0123456789", false)]
    [InlineData(Key, false)]
    [InlineData(null, false)]
    public void The_key_must_match_the_file(string? authorization, bool matches) =>
        new MetricsKey(_keyFile).Matches(authorization).Should().Be(matches);

    [Fact]
    public void A_short_missing_or_unset_key_matches_nothing()
    {
        File.WriteAllText(_keyFile, "short");

        new MetricsKey(_keyFile).Matches("Bearer short").Should().BeFalse();
        new MetricsKey(_keyFile + ".missing").Matches("Bearer " + Key).Should().BeFalse();
        new MetricsKey(null).Matches("Bearer " + Key).Should().BeFalse();
    }

    [Fact]
    public void The_key_is_kept_after_a_match_and_read_again_only_after_a_mismatch()
    {
        var key = new MetricsKey(_keyFile);
        key.Matches("Bearer " + Key).Should().BeTrue();
        var next = "synthetic-metrics-key-next-0123456789abcdef";
        File.WriteAllText(_keyFile, next);

        key.Matches("Bearer " + Key).Should().BeTrue("a match does not read the file");
        key.Matches("Bearer " + next).Should().BeTrue("a mismatch reads the new key");
        key.Matches("Bearer " + Key).Should().BeFalse("the old key is gone once the new one was read");
    }

    [Fact]
    public void A_snapshot_takes_minutes_before_until_and_at_most_max_cells_oldest_first_and_says_it_was_cut()
    {
        var metrics = new RequestMetrics(_time);
        foreach (var route in new[] { "/b", "/a", "/c" }) metrics.Record(route, 200, TimeSpan.FromMilliseconds(1));
        _time.Advance(TimeSpan.FromMinutes(1));
        metrics.Record("/a", 200, TimeSpan.FromMilliseconds(1));

        var cut = metrics.Snapshot(null, max: 2);
        var before = metrics.Snapshot(null, until: new DateTime(2026, 11, 18, 10, 1, 0, DateTimeKind.Utc));
        var all = metrics.Snapshot(null);

        cut.Minutes.Select(m => (m.Minute.Minute, m.Route)).Should().Equal((0, "/a"), (0, "/b"));
        cut.Truncated.Should().BeTrue();
        before.Minutes.Select(m => m.Route).Should().Equal("/a", "/b", "/c");
        before.Truncated.Should().BeFalse();
        all.Minutes.Should().HaveCount(4);
        all.Minutes[0].Latency.Should().NotBeSameAs(metrics.Snapshot(null).Minutes[0].Latency, "each snapshot copies the histogram");
    }

    [Fact]
    public async Task The_metrics_port_answers_only_the_key_and_only_on_its_path()
    {
        var metrics = new RequestMetrics(_time);
        metrics.Record("/api/habits", 200, TimeSpan.FromMilliseconds(30));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ops:MetricsKeyFile"] = _keyFile,
            ["Ops:MetricsPort"] = "0",
        }).Build();
        await using var server = new MetricsServer(metrics, configuration, NullLogger<MetricsServer>.Instance);
        await server.StartAsync(default);
        using var http = new HttpClient { BaseAddress = new Uri(server.Address!.Replace("0.0.0.0", "127.0.0.1")) };

        (await http.GetAsync(ApiMetrics.Path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await http.GetAsync("/api/habits")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        var body = await http.GetFromJsonAsync<JsonElement>(ApiMetrics.Path);

        body.GetProperty("bucketsMs").GetArrayLength().Should().Be(RequestMetrics.BucketsMs.Length);
        var minute = body.GetProperty("minutes").EnumerateArray().Single();
        (minute.GetProperty("route").GetString(), minute.GetProperty("count").GetInt32(), minute.GetProperty("minute").GetString())
            .Should().Be(("/api/habits", 1, "2026-11-18T10:00:00Z"));
        await server.StopAsync(default);
    }

    [Fact]
    public async Task A_hosting_listener_scoped_to_the_outer_apps_start_never_configures_the_metrics_app()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ops:MetricsKeyFile"] = _keyFile,
            ["Ops:MetricsPort"] = "0",
        }).Build();
        await using var server = new MetricsServer(new RequestMetrics(_time), configuration, NullLogger<MetricsServer>.Instance);
        using var listener = new StartScopedHostingListener();

        await listener.RunInScopeAsync(() => server.StartAsync(default));

        listener.HostsConfigured.Should().Be(0);
        server.Address.Should().NotBeNull();
        await server.StopAsync(default);
    }

    /// <summary>
    /// Stands in for the hosting listener of <c>WebApplicationFactory</c> while the factory still waits for its app: it configures every
    /// host built in its own async flow for the test server, which has no addresses.
    /// </summary>
    private sealed class StartScopedHostingListener : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private static readonly AsyncLocal<StartScopedHostingListener?> Current = new();
        private readonly IDisposable _subscription;
        private readonly List<IDisposable> _listeners = [];
        private int _configured;

        public StartScopedHostingListener() => _subscription = DiagnosticListener.AllListeners.Subscribe(this);

        public int HostsConfigured => Volatile.Read(ref _configured);

        public async Task RunInScopeAsync(Func<Task> action)
        {
            Current.Value = this;
            await action();
        }

        public void OnNext(DiagnosticListener value)
        {
            if (Current.Value == this && value.Name == "Microsoft.Extensions.Hosting")
                lock (_listeners) _listeners.Add(value.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Current.Value != this || value.Key != "HostBuilding") return;
            ((IHostBuilder)value.Value!).ConfigureWebHost(web => web.UseTestServer());
            Interlocked.Increment(ref _configured);
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose()
        {
            _subscription.Dispose();
            lock (_listeners) _listeners.ForEach(l => l.Dispose());
        }
    }
}

public sealed class ApiMetricsHostTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly string KeyFile = Path.Combine(Path.GetTempPath(), $"ember-metrics-{Guid.NewGuid():N}.key");

    static ApiMetricsHostTests() => File.WriteAllText(KeyFile, "synthetic-metrics-key-0123456789abcdef");

    protected override Dictionary<string, string?> Settings => new() { ["Ops:MetricsKeyFile"] = KeyFile, ["Ops:MetricsPort"] = "0" };

    [DockerFact]
    public async Task Requests_are_recorded_by_route_template_without_ids_or_query_values_and_the_health_check_is_left_out()
    {
        using var client = Host.CreateClient();
        (await client.GetAsync("/api/habits/987654?note=synthetic-secret")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await client.GetAsync("/health");

        var minutes = Host.Services.GetRequiredService<RequestMetrics>().Snapshot(null).Minutes;

        minutes.Select(m => m.Route).Should().Equal("/api/habits/{id:int}");
        var json = JsonSerializer.Serialize(minutes);
        json.Should().NotContain("987654").And.NotContain("synthetic-secret");
        Host.Services.GetServices<IHostedService>().OfType<MetricsServer>().Single().Address.Should().NotBeNull();
    }
}

public sealed class ApiMetricsWithoutKeyTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [DockerFact]
    public async Task Without_a_key_file_nothing_is_recorded_and_no_metrics_port_is_opened()
    {
        using var client = Host.CreateClient();
        await client.GetAsync("/api/habits/987654");

        Host.Services.GetRequiredService<RequestMetrics>().Snapshot(null).Minutes.Should().BeEmpty();
        Host.Services.GetServices<IHostedService>().OfType<MetricsServer>().Single().Address.Should().BeNull();
    }
}
