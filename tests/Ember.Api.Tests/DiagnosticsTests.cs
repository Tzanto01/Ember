using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ember.Api.Diagnostics;
using Ember.Api.Hosting;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ember.Api.Tests;

/// <summary>The shared golden fixtures of <c>tests/fixtures/diagnostics</c> and the upload helpers of the diagnostics tests.</summary>
internal static class DiagnosticsFixtures
{
    public const string GoldenRef = "EMB-7K3M9QX2";

    public static readonly DateTimeOffset GoldenCreatedAt = new(2026, 11, 20, 18, 45, 12, 345, TimeSpan.Zero);

    public static string Read(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "tests", "fixtures", "diagnostics", name));

    public static JsonObject Request() => JsonNode.Parse(Read("request.json"))!.AsObject();

    public static StringContent Json(JsonNode request) => new(request.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>Posts <paramref name="request"/> (the golden request unless given) as raw JSON, without any antiforgery header.</summary>
    public static Task<HttpResponseMessage> Upload(HttpClient http, JsonNode? request = null) => http.PostAsync("/api/diagnostics", Json(request ?? Request()));

    public static JsonObject WithInstallation(string installation)
    {
        var request = Request();
        request["installation"] = installation;
        return request;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ember.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Ember.slnx not found above the test output directory.");
    }
}

public class DiagnosticsBodyTests
{
    [Theory]
    [InlineData("request.json", "body.json")]
    [InlineData("request-escapes.json", "body-escapes.json")]
    public void The_body_of_a_golden_request_equals_the_golden_body_byte_for_byte(string requestFile, string bodyFile)
    {
        var request = JsonSerializer.Deserialize<DiagnosticsRequest>(DiagnosticsFixtures.Read(requestFile), JsonSerializerOptions.Web)!;

        var body = DiagnosticsBody.Serialize(DiagnosticsFixtures.GoldenRef, DiagnosticsFixtures.GoldenCreatedAt.UtcDateTime, request);

        DiagnosticsValidation.Errors(request, DiagnosticsFixtures.GoldenCreatedAt.UtcDateTime).Should().BeEmpty();
        Encoding.UTF8.GetString(body).Should().Be(DiagnosticsFixtures.Read(bodyFile));
    }

    [Fact]
    public void New_refs_match_Lookouts_ref_grammar_and_differ()
    {
        var refs = Enumerable.Range(0, 200).Select(_ => DiagnosticsBody.NewRef()).ToList();

        refs.Should().OnlyContain(r => System.Text.RegularExpressions.Regex.IsMatch(r, @"\AEMB-[0-9A-HJKMNP-TV-Z]{8}\z"));
        refs.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_installation_goes_into_the_limiter_keys_only_as_a_stable_hash()
    {
        const string installation = "Zk3vQ8xLm2Ty7pWa9cHn4g";
        var address = IPAddress.Parse("203.0.113.7");

        var keys = RateLimits.DiagnosticsKeys(installation, address);
        var again = RateLimits.DiagnosticsKeys(installation, address);
        var other = RateLimits.DiagnosticsKeys("Qm9vdHN0cmFwX2luc3RhbGw", address);

        keys.Should().HaveCount(3).And.OnlyContain(k => !k.Contains(installation, StringComparison.OrdinalIgnoreCase));
        keys[1].Should().MatchRegex(@"\A[^:]+:[0-9A-F]{64}\z");
        again.Should().Equal(keys);
        other[1].Should().NotBe(keys[1]);
        (other[0], other[2]).Should().Be((keys[0], keys[2]));
        keys.Should().NotIntersectWith(RateLimits.FeedbackKeys(null, address))
            .And.NotIntersectWith(RateLimits.MailKeys("register", installation, address));
    }
}

public class DiagnosticsApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private Task<List<LookoutDiagnostic>> Rows() => Host.Db(db => db.LookoutDiagnostics.AsNoTracking().OrderBy(d => d.Id).ToListAsync());

    private HttpClient Anonymous() => Host.NewClient().Http;

    private void AtGoldenTime() => Host.Time.SetUtcNow(DiagnosticsFixtures.GoldenCreatedAt);

    private async Task ExpectRefused(Action<JsonObject> change, string field, string? leaked = null)
    {
        var request = DiagnosticsFixtures.Request();
        change(request);

        var response = await DiagnosticsFixtures.Upload(Anonymous(), request);

        var problem = await response.ExpectProblem(HttpStatusCode.BadRequest, "validation");
        problem.GetProperty("errors").Has(field).Should().BeTrue($"{field} is named in {problem}");
        if (leaked is not null) problem.GetRawText().Should().NotContain(leaked);
        (await Rows()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task An_upload_stores_the_golden_body_under_the_returned_ref_and_answers_201()
    {
        AtGoldenTime();

        var created = await (await DiagnosticsFixtures.Upload(Anonymous())).ExpectJson(HttpStatusCode.Created);

        var reference = created.GetProperty("ref").GetString()!;
        reference.Should().MatchRegex(@"\AEMB-[0-9A-HJKMNP-TV-Z]{8}\z");
        var row = (await Rows()).Should().ContainSingle().Subject;
        row.Ref.Should().Be(reference);
        row.Key.Should().MatchRegex(@"\A[0-9a-f]{32}\z");
        (row.Attempts, row.NextAttemptAt).Should().Be((0, (DateTime?)null));
        Encoding.UTF8.GetString(row.Body).Should().Be(DiagnosticsFixtures.Read("body.json").Replace(DiagnosticsFixtures.GoldenRef, reference));
        Encoding.UTF8.GetString(row.Body).Should().NotContain("Zk3vQ8xLm2Ty7pWa9cHn4g");
    }

    [DockerFact]
    public async Task Events_outside_the_vocabulary_are_refused()
    {
        AtGoldenTime();
        foreach (var name in new[]
                 {
                     "launch cold=yes", "launch source=user cold=true", "launch route=/habits/7", "page_loaded ms=1840 extra=1",
                     "page_loaded ms=1840\n", "route_ignored reason=moment_other_package", "open_habit", "page_loaded  ms=1",
                 })
            await ExpectRefused(r => r["launches"]![0]!["event"] = name, "launches[0].event", name);
    }

    [DockerFact]
    public async Task Unknown_exit_reasons_and_signals_are_refused()
    {
        AtGoldenTime();
        await ExpectRefused(r => r["exits"]![0]!["reason"] = "REASON_CRASH", "exits[0].reason", "REASON_CRASH");
        await ExpectRefused(r => r["exits"]![2]!["signal"] = "SIGZZTOP", "exits[2].signal", "SIGZZTOP");
    }

    [DockerFact]
    public async Task Frames_and_exception_names_outside_the_grammars_are_refused()
    {
        AtGoldenTime();
        foreach (var frame in new[]
                 {
                     "at app.ember.Main.run(Main.kt:1)", "app.ember.Main.run(Main.kt:1)\n", "app.ember.Mäin.run(Main.kt:1)",
                     "#00 pc 4e1d4 libc.so (__epoll_pwait+12)", "Caused by: secret message", "#01 pc 0004e1d4 libc.so\n",
                 })
            await ExpectRefused(r => r["exits"]![1]!["frames"]![0] = frame, "exits[1].frames", frame.Trim());
        await ExpectRefused(r => r["exits"]![0]!["exception"]![0] = "java.lang.IllegalStateException: user text", "exits[0].exception", "user text");
    }

    [DockerFact]
    public async Task Too_many_launches_exits_frames_or_exception_names_are_refused()
    {
        AtGoldenTime();
        var launch = DiagnosticsFixtures.Request()["launches"]![0]!;
        var exit = DiagnosticsFixtures.Request()["exits"]![2]!;
        var frame = "app.ember.Main.run(Main.kt:1)";

        await ExpectRefused(r => r["launches"] = new JsonArray([.. Enumerable.Range(0, 51).Select(_ => launch.DeepClone())]), "launches");
        await ExpectRefused(r => r["exits"] = new JsonArray([.. Enumerable.Range(0, 6).Select(_ => exit.DeepClone())]), "exits");
        await ExpectRefused(r => r["exits"]![0]!["frames"] = new JsonArray([.. Enumerable.Range(0, 41).Select(_ => (JsonNode)frame)]), "exits[0].frames");
        await ExpectRefused(r => r["exits"]![0]!["exception"] = new JsonArray([.. Enumerable.Range(0, 11).Select(_ => (JsonNode)"java.lang.Error")]),
            "exits[0].exception");
    }

    [DockerFact]
    public async Task A_bad_installation_platform_version_os_device_or_time_is_refused_by_field()
    {
        AtGoldenTime();
        await ExpectRefused(r => r["installation"] = "short-id", "installation", "short-id");
        await ExpectRefused(r => r.Remove("installation"), "installation");
        await ExpectRefused(r => r["platform"] = "web", "platform");
        await ExpectRefused(r => r["version"] = "1.0 beta", "version", "1.0 beta");
        await ExpectRefused(r => r["os"] = "Android 14", "os");
        await ExpectRefused(r => r["os"] = "Android 14 (API 34)\n", "os");
        await ExpectRefused(r => r["device"] = "Pixel\u0007 7", "device");
        await ExpectRefused(r => r["launches"]![1]!["at"] = "2026-11-20T18:40:30Z", "launches[1].at");
        await ExpectRefused(r => r["exits"]![0]!["at"] = "2026-11-20 18:39", "exits[0].at");
    }

    [DockerFact]
    public async Task Launches_and_exits_that_are_not_newest_first_are_refused()
    {
        AtGoldenTime();
        await ExpectRefused(r => r["launches"]![0]!["at"] = "2026-11-19T07:01:00Z", "launches");
        await ExpectRefused(r => r["exits"]![2]!["at"] = "2026-11-20T18:40:00Z", "exits");
    }

    [DockerFact]
    public async Task Entries_older_than_seven_days_or_more_than_an_hour_ahead_are_refused_as_clock_and_the_edges_stored_as_sent()
    {
        AtGoldenTime();
        await ExpectRefused(r => r["launches"]![0]!["at"] = "2026-11-20T19:46:00Z", "clock", "19:46");
        await ExpectRefused(r => r["exits"]![2]!["at"] = "2026-11-13T18:46:00Z", "clock", "11-13");

        var request = DiagnosticsFixtures.Request();
        request["launches"] = new JsonArray(
            new JsonObject { ["at"] = "2026-11-20T19:45:00Z", ["event"] = "page_loaded ms=1" },
            new JsonObject { ["at"] = "2026-11-13T18:47:00Z", ["event"] = "page_loaded ms=2" });
        request["exits"]![2]!["at"] = "2026-11-13T18:47:00Z";

        await (await DiagnosticsFixtures.Upload(Anonymous(), request)).Expect(HttpStatusCode.Created);

        var body = JsonElement.Parse((await Rows()).Single().Body);
        body.GetProperty("launches").EnumerateArray().Select(l => l.GetProperty("at").GetString())
            .Should().Equal("2026-11-20T19:45:00Z", "2026-11-13T18:47:00Z");
        body.GetProperty("exits").EnumerateArray().Select(e => e.GetProperty("at").GetString()).Last().Should().Be("2026-11-13T18:47:00Z");
    }

    [DockerFact]
    public async Task A_body_over_64000_bytes_is_refused_with_413_and_not_stored()
    {
        AtGoldenTime();
        var frame = "app." + new string('b', 248) + "(" + new string('c', 64) + ")";
        var request = DiagnosticsFixtures.Request();
        request["exits"] = new JsonArray([.. Enumerable.Range(0, 5).Select(i => (JsonNode)new JsonObject
        {
            ["at"] = "2026-11-20T18:0" + (9 - i) + ":00Z",
            ["reason"] = "ANR",
            ["signal"] = null,
            ["exception"] = new JsonArray(),
            ["frames"] = new JsonArray([.. Enumerable.Range(0, 40).Select(_ => (JsonNode)frame)]),
        })]);
        var json = request.ToJsonString();

        var response = await DiagnosticsFixtures.Upload(Anonymous(), request);

        Encoding.UTF8.GetByteCount(json).Should().BeInRange(64_000, (int)ApiApp.MaxRequestBodyBytes);
        await response.ExpectProblem(HttpStatusCode.RequestEntityTooLarge, "too_large");
        (await Rows()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_full_queue_answers_503_busy_until_an_upload_expires_after_30_days()
    {
        AtGoldenTime();
        var now = Host.Time.GetUtcNow().UtcDateTime;
        await Host.Db(db =>
        {
            db.LookoutDiagnostics.AddRange(Enumerable.Range(0, DiagnosticsEndpoints.MaxQueued).Select(i => new LookoutDiagnostic
            {
                Key = Guid.NewGuid().ToString("N"),
                Ref = $"EMB-{i:D8}",
                Body = [1],
                CreatedAt = i == 0 ? now.AddDays(-29) : now,
            }));
            return db.SaveChangesAsync();
        });

        var busy = await DiagnosticsFixtures.Upload(Anonymous());
        Host.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        var request = DiagnosticsFixtures.Request();
        request["launches"] = new JsonArray();
        request["exits"] = new JsonArray();
        var afterExpiry = await DiagnosticsFixtures.Upload(Anonymous(), request);

        await busy.ExpectProblem(HttpStatusCode.ServiceUnavailable, "busy");
        await afterExpiry.Expect(HttpStatusCode.Created);
        var rows = await Rows();
        rows.Should().HaveCount(DiagnosticsEndpoints.MaxQueued).And.NotContain(r => r.Ref == "EMB-00000000");
    }

    [DockerFact]
    public async Task Signed_in_callers_by_cookie_or_bearer_upload_unlinked_without_an_antiforgery_token()
    {
        AtGoldenTime();
        var cookie = await Host.SignedInAsync();
        cookie.Csrf = null;
        var bearer = await Host.BearerAsync();
        var users = new[] { await cookie.IdAsync(), await bearer.IdAsync() };
        var emails = await Host.Db(db => db.Users.Where(u => users.Contains(u.Id)).Select(u => u.Email!).ToListAsync());

        var viaCookie = await DiagnosticsFixtures.Upload(cookie.Http, DiagnosticsFixtures.WithInstallation("cookieInstallation0001"));
        var viaBearer = await DiagnosticsFixtures.Upload(bearer.Http, DiagnosticsFixtures.WithInstallation("bearerInstallation0001"));

        await viaCookie.Expect(HttpStatusCode.Created);
        await viaBearer.Expect(HttpStatusCode.Created);
        emails.Should().HaveCount(2);
        var rows = await Rows();
        rows.Should().HaveCount(2);
        foreach (var row in rows)
        {
            var body = Encoding.UTF8.GetString(row.Body);
            JsonElement.Parse(body).GetProperty("userRef").ValueKind.Should().Be(JsonValueKind.Null);
            foreach (var user in users) body.Should().NotContain(user.ToString("N")).And.NotContain(user.ToString());
            foreach (var email in emails) body.Should().NotContain(email);
        }
        (await Host.Db(db => db.LookoutUserRefs.CountAsync(r => users.Contains(r.UserId)))).Should().Be(0);
    }

    [DockerFact]
    public async Task A_native_preflight_for_a_json_post_is_allowed()
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/diagnostics");
        request.Headers.Add("Origin", "https://localhost");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        var response = await Anonymous().SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue();
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be("https://localhost");
        response.Headers.GetValues("Access-Control-Allow-Methods").Single().Should().Contain("POST");
        response.Headers.GetValues("Access-Control-Allow-Headers").Single().ToLowerInvariant().Should().Contain("content-type");
    }

    [DockerFact]
    public async Task Nothing_of_an_upload_reaches_the_logs()
    {
        AtGoldenTime();
        var logs = new CapturingLoggerProvider();
        await using var logged = Host.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(logs).AddFilter<CapturingLoggerProvider>(_ => true)));
        var http = logged.CreateDefaultClient(new Uri("https://localhost"));
        var refused = DiagnosticsFixtures.Request();
        refused["exits"]![0]!["frames"]![0] = "Caused by: zz-secret-frame";

        var created = await (await DiagnosticsFixtures.Upload(http)).ExpectJson(HttpStatusCode.Created);
        await (await DiagnosticsFixtures.Upload(http, refused)).Expect(HttpStatusCode.BadRequest);

        var all = logs.All;
        all.Should().NotBeEmpty();
        foreach (var secret in new[]
                 {
                     created.GetProperty("ref").GetString()!, "Zk3vQ8xLm2Ty7pWa9cHn4g", "MainActivity", "page_loaded", "Pixel", "epoll",
                     "IllegalStateException", "zz-secret-frame", "0.10.0-beta.4",
                 })
            all.Should().NotContain(secret);
    }
}

public class DiagnosticsRateLimitTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override Dictionary<string, string?>? Settings => new()
    {
        ["Ember:RateLimits:DiagnosticsPerIpPerDay"] = "6",
        ["Ember:RateLimits:DiagnosticsPerInstallationPerDay"] = "3",
        ["Ember:RateLimits:DiagnosticsPerHour"] = "20",
    };

    private HttpClient At(string ip)
    {
        var client = Host.NewClient().Http;
        client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        return client;
    }

    private static string Installation(int n) => $"installation-{n:D6}";

    private async Task<int> Status(string ip, int installation)
        => (int)(await DiagnosticsFixtures.Upload(At(ip), DiagnosticsFixtures.WithInstallation(Installation(installation)))).StatusCode;

    [DockerFact]
    public async Task One_installation_gets_three_uploads_a_day_across_ips_while_another_still_passes()
    {
        Host.Time.SetUtcNow(DiagnosticsFixtures.GoldenCreatedAt);
        var statuses = new[] { await Status("203.0.113.1", 1), await Status("203.0.113.2", 1), await Status("203.0.113.3", 1) };

        var limited = await DiagnosticsFixtures.Upload(At("203.0.113.4"), DiagnosticsFixtures.WithInstallation(Installation(1)));
        var other = await Status("203.0.113.4", 2);

        statuses.Should().OnlyContain(s => s == 201);
        await limited.ExpectProblem((HttpStatusCode)429, "rate_limited");
        limited.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.FromHours(23));
        other.Should().Be(201);
        (await Host.Db(db => db.LookoutDiagnostics.CountAsync())).Should().Be(4);
    }

    [DockerFact]
    public async Task One_ip_gets_six_uploads_a_day_across_installations_while_another_ip_still_passes()
    {
        Host.Time.SetUtcNow(DiagnosticsFixtures.GoldenCreatedAt);
        var statuses = new List<int>();
        for (var i = 0; i < 7; i++) statuses.Add(await Status("203.0.113.1", i));
        var otherIp = await Status("203.0.113.2", 7);

        statuses.Should().Equal(201, 201, 201, 201, 201, 201, 429);
        otherIp.Should().Be(201);
    }

    [DockerFact]
    public async Task All_senders_together_get_twenty_uploads_an_hour()
    {
        Host.Time.SetUtcNow(DiagnosticsFixtures.GoldenCreatedAt);
        var statuses = new List<int>();
        for (var i = 0; i < 21; i++) statuses.Add(await Status($"203.0.113.{i + 1}", i));

        statuses.Take(20).Should().OnlyContain(s => s == 201);
        statuses[20].Should().Be(429);
    }
}

public class DiagnosticsSenderTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly TimeSpan Zero = TimeSpan.Zero;

    private Task<List<LookoutDiagnostic>> Rows() => Host.Db(db => db.LookoutDiagnostics.AsNoTracking().OrderBy(d => d.Id).ToListAsync());

    private Task<List<LookoutOutbox>> Outbox() => Host.Db(db => db.LookoutOutbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync());

    private static string Path(LookoutDiagnostic row) => "/ingest/v1/diagnostics/" + row.Key;

    private async Task<string> Upload(int n)
    {
        var created = await (await DiagnosticsFixtures.Upload(Host.NewClient().Http, DiagnosticsFixtures.WithInstallation($"installation-{n:D6}")))
            .ExpectJson(HttpStatusCode.Created);
        return created.GetProperty("ref").GetString()!;
    }

    private Task Report(Guid? userId, string title)
        => Host.Run<FeedbackService>(s => s.SaveFeedbackAsync(userId, "web", "1.2.3", "text of " + title, default, title, []));

    [DockerFact]
    public async Task Uploads_go_out_after_tickets_with_their_stored_bytes_and_are_deleted_on_200_or_201()
    {
        Host.Time.SetUtcNow(DiagnosticsFixtures.GoldenCreatedAt);
        await using var rig = await SenderRig.StartAsync(Host);
        await Upload(1);
        await Upload(2);
        await Report(null, "A ticket");
        var rows = await Rows();
        var ticket = (await Outbox()).Single();
        rig.Fake.Script(201, 200, 201);

        var waits = new[] { await rig.Step(), await rig.Step(), await rig.Step() };
        var idle = await rig.Step();

        waits.Should().OnlyContain(w => w == Zero);
        idle.Should().Be(LookoutSender.PollInterval);
        var requests = rig.Fake.Requests;
        requests.Select(r => (r.Method, r.Path)).Should().Equal(("PUT", "/ingest/v1/tickets/" + ticket.Key), ("PUT", Path(rows[0])), ("PUT", Path(rows[1])));
        requests.Should().OnlyContain(r => r.Authorization == "Bearer " + rig.Key);
        requests[1].Body.Should().Equal(rows[0].Body);
        requests[2].Body.Should().Equal(rows[1].Body);
        (await Rows()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Final_refusals_drop_the_upload_and_log_only_id_and_status()
    {
        Host.Time.SetUtcNow(DiagnosticsFixtures.GoldenCreatedAt);
        await using var rig = await SenderRig.StartAsync(Host);
        var refs = new List<string>();
        for (var i = 0; i < 4; i++) refs.Add(await Upload(i));
        var rows = await Rows();
        rig.Fake.Script(400, 409, 413, 415);

        var waits = new[] { await rig.Step(), await rig.Step(), await rig.Step(), await rig.Step() };

        waits.Should().OnlyContain(w => w == Zero);
        rig.Fake.Requests.Select(r => r.Path).Should().Equal(rows.Select(Path));
        (await Rows()).Should().BeEmpty();
        var logs = rig.Logs.All;
        logs.Should().Contain("400").And.Contain("409").And.Contain("413").And.Contain("415");
        foreach (var secret in refs.Concat(rows.Select(r => r.Key)).Concat(["MainActivity", "Pixel", rig.Key]))
            logs.Should().NotContain(secret);
    }

    [DockerFact]
    public async Task A_throttled_or_failing_upload_backs_off_on_its_own_and_a_newer_ticket_goes_first()
    {
        Host.Time.SetUtcNow(DiagnosticsFixtures.GoldenCreatedAt);
        await using var rig = await SenderRig.StartAsync(Host);
        await Upload(1);
        var upload = (await Rows()).Single();
        rig.Fake.Script(429);

        var throttled = await rig.Step();
        var afterThrottle = (await Rows()).Single();
        await Report(null, "Newer ticket");
        var ticket = (await Outbox()).Single();
        rig.Fake.Script(503, 201);
        var ticketFailure = await rig.Step();
        var ticketSent = await rig.Step();
        var notDue = await rig.Step();
        var requestsBeforeDue = rig.Fake.Requests.Count;
        Host.Time.Advance(TimeSpan.FromSeconds(5));
        rig.Fake.Script(503);
        var busy = await rig.Step();
        var afterBusy = (await Rows()).Single();
        Host.Time.Advance(TimeSpan.FromSeconds(10));
        rig.Fake.Script(FakeLookout.Abort);
        var aborted = await rig.Step();
        var afterAbort = (await Rows()).Single();
        Host.Time.Advance(TimeSpan.FromSeconds(20));
        var delivered = await rig.Step();

        var now = DiagnosticsFixtures.GoldenCreatedAt.UtcDateTime;
        (throttled, busy, aborted, delivered).Should().Be((Zero, Zero, Zero, Zero));
        (afterThrottle.Attempts, afterThrottle.NextAttemptAt).Should().Be((1, now.AddSeconds(5)));
        ticketFailure.Should().Be(TimeSpan.FromSeconds(5), "an upload's failures never count toward the ticket backoff");
        (ticketSent, notDue).Should().Be((Zero, LookoutSender.PollInterval));
        requestsBeforeDue.Should().Be(3);
        (afterBusy.Attempts, afterBusy.NextAttemptAt).Should().Be((2, now.AddSeconds(15)));
        (afterAbort.Attempts, afterAbort.NextAttemptAt).Should().Be((3, now.AddSeconds(35)));
        rig.Fake.Requests.Select(r => r.Path).Should()
            .Equal(Path(upload), "/ingest/v1/tickets/" + ticket.Key, "/ingest/v1/tickets/" + ticket.Key, Path(upload), Path(upload), Path(upload));
        (await Rows()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Uploads_unsent_for_30_days_are_dropped_even_while_a_ticket_is_stuck()
    {
        Host.Time.SetUtcNow(DiagnosticsFixtures.GoldenCreatedAt);
        await using var rig = await SenderRig.StartAsync(Host);
        await Upload(1);
        Host.Time.Advance(TimeSpan.FromDays(2));
        await Upload(2);
        var rows = await Rows();
        await Report(null, "Stuck ticket");
        Host.Time.Advance(TimeSpan.FromDays(28) + TimeSpan.FromMinutes(1));
        rig.Fake.Script(503);

        var stuck = await rig.Step();

        stuck.Should().Be(TimeSpan.FromSeconds(5));
        (await Rows()).Select(r => r.Id).Should().Equal(rows[1].Id);
        rig.Fake.Requests.Should().ContainSingle().Which.Path.Should().StartWith("/ingest/v1/tickets/");
        rig.Logs.All.Should().Contain("Dropped 1 diagnostics");
    }

    [DockerFact]
    public async Task An_account_deletion_with_an_upload_queued_is_sent_first_and_the_upload_stays_unlinked()
    {
        Host.Time.SetUtcNow(DiagnosticsFixtures.GoldenCreatedAt);
        await using var rig = await SenderRig.StartAsync(Host);
        var client = await Host.SignedInAsync();
        var user = await client.IdAsync();
        await Report(user, "Mine");
        var userRef = await Host.Db(db => db.LookoutUserRefs.Where(r => r.UserId == user).Select(r => r.Ref).SingleAsync());
        await (await DiagnosticsFixtures.Upload(client.Http)).Expect(HttpStatusCode.Created);
        var before = (await Rows()).Single();

        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(user));
        var after = (await Rows()).Single();
        var deletion = await rig.Step();
        var upload = await rig.Step();

        after.Id.Should().Be(before.Id);
        after.Body.Should().Equal(before.Body);
        JsonElement.Parse(after.Body).GetProperty("userRef").ValueKind.Should().Be(JsonValueKind.Null);
        Encoding.UTF8.GetString(after.Body).Should().NotContain(userRef);
        (deletion, upload).Should().Be((Zero, Zero));
        rig.Fake.Requests.Select(r => (r.Method, r.Path)).Should().Equal(("DELETE", "/ingest/v1/users/" + userRef), ("PUT", Path(before)));
        (await Rows()).Should().BeEmpty();
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public string All => string.Join("\n", _lines);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
