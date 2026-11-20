using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ember.Api.Auth;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests;

public class WidgetCheckInApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string WidgetKey = "/api/me/widget-key";

    /// <summary>A consenting user signed in with a bearer token only, like the native app.</summary>
    private async Task<(ApiClient Bearer, Guid UserId)> BearerAsync()
    {
        var bearer = await Host.BearerAsync();
        return (bearer, await bearer.IdAsync());
    }

    private static async Task<int> HabitAsync(ApiClient user, int timesPerDay = 1)
        => (await (await user.Post("/api/habits", new { name = "Walk", frequency = "daily", timesPerDay })).ExpectJson(HttpStatusCode.Created))
            .GetProperty("id").GetInt32();

    private static async Task<string> IssueAsync(ApiClient bearer)
        => (await (await bearer.Post(WidgetKey)).ExpectJson()).GetProperty("key").GetString()!;

    private Task<HttpResponseMessage> CheckIn(string? authorization, int habitId, DateOnly? date = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/widget/check-in")
        {
            Content = JsonContent.Create(new { habitId, date = (date ?? Host.Today).Iso() }),
        };
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return Host.NewClient().Http.SendAsync(request);
    }

    private Task<int?> CountAsync(int habitId)
        => Host.Db(db => db.HabitLogs.Where(l => l.HabitId == habitId && l.Date == Host.Today).Select(l => (int?)l.Count).SingleOrDefaultAsync());

    private static async Task ExpectUnauthorized(HttpResponseMessage response)
        => (await response.ExpectProblem(HttpStatusCode.Unauthorized, "unauthenticated")).Has("detail").Should().BeFalse();

    private static void ExpectDay(JsonElement day, string state, int count, int timesPerDay)
    {
        day.GetProperty("state").GetString().Should().Be(state);
        day.GetProperty("count").GetInt32().Should().Be(count);
        day.GetProperty("timesPerDay").GetInt32().Should().Be(timesPerDay);
    }

    [DockerFact]
    public async Task Issuing_returns_a_32_byte_key_and_stores_only_its_hash_on_one_session_of_the_caller()
    {
        var (bearer, userId) = await BearerAsync();

        var key = await IssueAsync(bearer);

        Convert.FromBase64String(key.Replace('-', '+').Replace('_', '/') + "=").Should().HaveCount(32);
        var hashes = await Host.Db(db => db.AuthSessions.Where(s => s.WidgetKeyHash != null).Select(s => new { s.UserId, s.WidgetKeyHash }).ToListAsync());
        hashes.Should().ContainSingle();
        hashes[0].UserId.Should().Be(userId);
        hashes[0].WidgetKeyHash.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    [DockerFact]
    public async Task A_cookie_session_gets_404_and_no_key()
    {
        var user = await Host.SignedInAsync();

        await (await user.Post(WidgetKey)).ExpectProblem(HttpStatusCode.NotFound, "not_found");

        (await Host.Db(db => db.AuthSessions.AnyAsync(s => s.WidgetKeyHash != null))).Should().BeFalse();
    }

    [DockerFact]
    public async Task A_key_adds_one_check_in_capped_at_the_target_without_extending_the_session()
    {
        var (bearer, userId) = await BearerAsync();
        var habitId = await HabitAsync(bearer, timesPerDay: 2);
        var key = await IssueAsync(bearer);
        var expires = await Host.Db(db => db.AuthSessions.Where(s => s.WidgetKeyHash != null).Select(s => s.ExpiresUtc).SingleAsync());
        Host.Time.Advance(TimeSpan.FromMinutes(10));

        ExpectDay(await (await CheckIn($"Widget {key}", habitId)).ExpectJson(), "partial", 1, 2);
        ExpectDay(await (await CheckIn($"widget {key}", habitId)).ExpectJson(), "done", 2, 2);
        ExpectDay(await (await CheckIn($"Widget {key}", habitId)).ExpectJson(), "done", 2, 2);

        (await CountAsync(habitId)).Should().Be(2);
        (await Host.Db(db => db.AuthSessions.Where(s => s.UserId == userId && s.WidgetKeyHash != null).Select(s => s.ExpiresUtc).SingleAsync()))
            .Should().Be(expires);
    }

    [DockerFact]
    public async Task On_a_low_day_a_check_in_short_of_the_target_reads_rest()
    {
        var (bearer, _) = await BearerAsync();
        var habitId = await HabitAsync(bearer, timesPerDay: 2);
        await (await bearer.Put($"/api/low-days/{Host.Today.Iso()}")).Expect(HttpStatusCode.NoContent);
        var key = await IssueAsync(bearer);

        ExpectDay(await (await CheckIn($"Widget {key}", habitId)).ExpectJson(), "rest", 1, 2);
    }

    [DockerFact]
    public async Task Reissuing_replaces_the_key()
    {
        var (bearer, _) = await BearerAsync();
        var habitId = await HabitAsync(bearer, timesPerDay: 3);
        var old = await IssueAsync(bearer);

        var key = await IssueAsync(bearer);

        await ExpectUnauthorized(await CheckIn($"Widget {old}", habitId));
        await (await CheckIn($"Widget {key}", habitId)).Expect(HttpStatusCode.OK);
        (await CountAsync(habitId)).Should().Be(1);
    }

    [DockerFact]
    public async Task Signing_out_revokes_the_key()
    {
        var (bearer, _) = await BearerAsync();
        var habitId = await HabitAsync(bearer);
        var key = await IssueAsync(bearer);

        await (await bearer.Post("/api/auth/logout")).Expect(HttpStatusCode.NoContent);

        await ExpectUnauthorized(await CheckIn($"Widget {key}", habitId));
        (await CountAsync(habitId)).Should().BeNull();
    }

    [DockerFact]
    public async Task An_expired_session_revokes_the_key()
    {
        var (bearer, _) = await BearerAsync();
        var habitId = await HabitAsync(bearer);
        var key = await IssueAsync(bearer);

        Host.Time.Advance(AuthSessions.Lifetime + TimeSpan.FromMinutes(1));

        await ExpectUnauthorized(await CheckIn($"Widget {key}", habitId));
        (await Host.Db(db => db.HabitLogs.AnyAsync(l => l.HabitId == habitId))).Should().BeFalse();
    }

    [DockerFact]
    public async Task Another_users_habit_is_404_and_unchanged()
    {
        var (bearer, _) = await BearerAsync();
        var (other, _) = await BearerAsync();
        var othersHabit = await HabitAsync(other);
        var key = await IssueAsync(bearer);

        await (await CheckIn($"Widget {key}", othersHabit)).ExpectProblem(HttpStatusCode.NotFound, "not_found");

        (await CountAsync(othersHabit)).Should().BeNull();
    }

    [DockerFact]
    public async Task A_date_other_than_the_users_today_is_a_validation_problem_on_date()
    {
        var (bearer, _) = await BearerAsync();
        var habitId = await HabitAsync(bearer);
        var key = await IssueAsync(bearer);

        foreach (var date in new[] { Host.Today.AddDays(-1), Host.Today.AddDays(1) })
        {
            var problem = await (await CheckIn($"Widget {key}", habitId, date)).ExpectProblem(HttpStatusCode.BadRequest, "validation");
            problem.GetProperty("errors").Has("date").Should().BeTrue();
        }
        (await Host.Db(db => db.HabitLogs.AnyAsync(l => l.HabitId == habitId))).Should().BeFalse();
    }

    [DockerFact]
    public async Task The_date_check_uses_the_users_time_zone()
    {
        var (bearer, _) = await BearerAsync();
        await (await bearer.Patch("/api/me", new { timezone = "Pacific/Kiritimati" })).Expect(HttpStatusCode.OK);
        var habitId = await HabitAsync(bearer);
        var key = await IssueAsync(bearer);
        var localToday = DateOnly.FromDateTime(Host.Time.GetUtcNow().UtcDateTime.AddHours(14));
        localToday.Should().NotBe(Host.Today);

        var problem = await (await CheckIn($"Widget {key}", habitId, Host.Today)).ExpectProblem(HttpStatusCode.BadRequest, "validation");
        problem.GetProperty("errors").Has("date").Should().BeTrue();
        ExpectDay(await (await CheckIn($"Widget {key}", habitId, localToday)).ExpectJson(), "done", 1, 1);

        (await Host.Db(db => db.HabitLogs.Where(l => l.HabitId == habitId).Select(l => l.Date).ToListAsync())).Should().Equal(localToday);
    }

    [DockerFact]
    public async Task A_signed_in_route_refuses_the_key()
    {
        var (bearer, _) = await BearerAsync();
        var habitId = await HabitAsync(bearer);
        var key = await IssueAsync(bearer);
        var client = Host.NewClient();
        client.Http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Widget {key}");

        (await client.Http.GetAsync("/api/habits")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await (await CheckIn($"Widget {key}", habitId)).Expect(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task Missing_malformed_unknown_or_bearer_credentials_are_401_without_detail()
    {
        var (bearer, _) = await BearerAsync();
        var habitId = await HabitAsync(bearer);
        var key = await IssueAsync(bearer);
        var accessToken = bearer.Http.DefaultRequestHeaders.Authorization!.Parameter;

        foreach (var authorization in new[]
        {
            null, "Widget", "Widget ", $"Bearer {key}", $"Bearer {accessToken}", $"Widget {key}x", $"Widget {key[..^1]}",
            $"Widget {new string('A', 129)}", $"Widget{key}",
        })
            await ExpectUnauthorized(await CheckIn(authorization, habitId));

        (await CountAsync(habitId)).Should().BeNull();
    }
}

public class WidgetCheckInRateLimitTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override Dictionary<string, string?>? Settings => new()
    {
        ["Ember:RateLimits:WidgetPerIpPerMinute"] = "3",
        ["Ember:RateLimits:AuthPerIpPerMinute"] = "3",
    };

    [DockerFact]
    public async Task Check_ins_are_limited_per_client_ip_apart_from_sign_in()
    {
        async Task<HttpResponseMessage> CheckIn(string ip)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/widget/check-in") { Content = JsonContent.Create(new { habitId = 1, date = Host.Today.Iso() }) };
            request.Headers.TryAddWithoutValidation("Authorization", "Widget unknown-key");
            request.Headers.Add("X-Forwarded-For", ip);
            return await Host.NewClient().Http.SendAsync(request);
        }

        for (var i = 0; i < 3; i++)
            await (await CheckIn("203.0.113.40")).Expect(HttpStatusCode.Unauthorized);

        await (await CheckIn("203.0.113.40")).ExpectProblem((HttpStatusCode)429, "rate_limited");
        await (await CheckIn("203.0.113.41")).Expect(HttpStatusCode.Unauthorized);

        var user = await Host.ConfirmedUserAsync();
        var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new Credentials(user.Email, ApiClient.Passphrase)) };
        login.Headers.Add("X-Forwarded-For", "203.0.113.40");
        await (await Host.NewClient().Http.SendAsync(login)).ExpectJson();
    }
}
