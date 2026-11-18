using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Ember.Ops.Tests;

public sealed class CiTests : IAsyncDisposable
{
    private const string RunsUrl = "https://api.github.com/repos/example/repo/actions/runs?per_page=50";

    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeHandler handler = new();
    private readonly MemoryCache cache;
    private readonly OpsDatabases databases = new(
        NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1"), NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1"));
    private readonly SystemInfo system;

    public CiTests()
    {
        cache = new(new MemoryCacheOptions { Clock = new Clock(time) });
        system = new(
            Options.Create(new OpsOptions { GitHubRepo = "example/repo", StatusFile = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json") }),
            databases, new Factory(handler), cache, time, NullLogger<SystemInfo>.Instance);
    }

    private static string Run(string branch, string createdAt, string sha) =>
        $$"""{"head_branch":"{{branch}}","status":"completed","conclusion":"success","html_url":"https://example.test/{{sha}}","created_at":"{{createdAt}}","head_sha":"{{sha}}"}""";

    private static HttpResponseMessage Runs(string? etag, params string[] runs)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"workflow_runs":[{{string.Join(',', runs)}}]}""", Encoding.UTF8, "application/json"),
        };
        if (etag is not null) response.Headers.ETag = new(etag);
        return response;
    }

    [Fact]
    public async Task One_request_shows_the_newest_run_per_branch()
    {
        handler.Respond = _ => Runs(null,
            Run("main", "2026-11-18T09:00:00Z", "main-old"),
            Run("feature/x", "2026-11-18T11:30:00Z", "feature"),
            Run("main", "2026-11-18T10:00:00Z", "main-new"),
            Run("v2/next", "2026-11-18T08:00:00Z", "next-old"),
            Run("test", "2026-11-18T07:00:00Z", "test"),
            Run("v2/next", "2026-11-18T11:00:00Z", "next-new"));

        var report = await system.GetAsync(CancellationToken.None);

        report.Errors.Should().NotContainKey("ci");
        report.Ci!.Select(r => (r.Branch, r.Sha)).Should().Equal(("main", "main-new"), ("test", "test"), ("v2/next", "next-new"));
        handler.Requests.Select(r => r.Url).Should().Equal(RunsUrl);
    }

    [Fact]
    public async Task After_the_cache_expires_a_304_keeps_the_last_runs()
    {
        handler.Respond = _ => Runs("\"abc\"", Run("main", "2026-11-18T10:00:00Z", "main-sha"));
        var first = await system.GetAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(11));
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        var second = await system.GetAsync(CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[1].IfNoneMatch.Should().Be("\"abc\"");
        second.Errors.Should().NotContainKey("ci");
        second.Ci.Should().Equal(first.Ci);
        second.Ci!.Single().Sha.Should().Be("main-sha");
    }

    [Fact]
    public async Task A_rate_limit_shows_the_reset_time_in_Berlin_and_waits_for_it()
    {
        var reset = new DateTimeOffset(2026, 11, 18, 12, 40, 0, TimeSpan.Zero);
        handler.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString());
            return response;
        };

        (await system.GetAsync(CancellationToken.None)).Errors["ci"].Should().Be("GitHub rate limit, back at 13:40");

        time.Advance(TimeSpan.FromMinutes(39));
        cache.Remove("system:ci");
        (await system.GetAsync(CancellationToken.None)).Errors["ci"].Should().Be("GitHub rate limit, back at 13:40");
        handler.Requests.Should().HaveCount(1);

        time.Advance(TimeSpan.FromMinutes(2));
        handler.Respond = _ => Runs(null, Run("main", "2026-11-18T12:30:00Z", "main-sha"));
        var after = await system.GetAsync(CancellationToken.None);

        handler.Requests.Select(r => r.Url).Should().Equal(RunsUrl, RunsUrl);
        after.Errors.Should().NotContainKey("ci");
        after.Ci!.Single().Sha.Should().Be("main-sha");
    }

    public async ValueTask DisposeAsync()
    {
        cache.Dispose();
        await databases.DisposeAsync();
    }

    private sealed record Seen(string Url, string? IfNoneMatch);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        public List<Seen> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!.ToString(),
                request.Headers.TryGetValues("If-None-Match", out var etags) ? string.Join(",", etags) : null));
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Clock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}
