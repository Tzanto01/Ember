using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ember.Api.Auth;
using FluentAssertions;

namespace Ember.Api.Tests;

public class WidgetTodayApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string Today = "/api/widget/today";

    private static async Task<int> HabitAsync(ApiClient user, string name, int timesPerDay = 1, string kind = "build")
        => (await (await user.Post("/api/habits", new { name, frequency = "daily", timesPerDay, kind })).ExpectJson(HttpStatusCode.Created))
            .GetProperty("id").GetInt32();

    private static async Task<string> IssueAsync(ApiClient bearer)
        => (await (await bearer.Post("/api/me/widget-key")).ExpectJson()).GetProperty("key").GetString()!;

    private Task<HttpResponseMessage> Get(string? authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Today);
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return Host.NewClient().Http.SendAsync(request);
    }

    private Task<HttpResponseMessage> CheckIn(string key, int habitId, DateOnly date)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/widget/check-in") { Content = JsonContent.Create(new { habitId, date = date.Iso() }) };
        request.Headers.TryAddWithoutValidation("Authorization", $"Widget {key}");
        return Host.NewClient().Http.SendAsync(request);
    }

    private async Task<JsonElement> DayAsync(string key) => await (await Get($"Widget {key}")).ExpectJson();

    private static async Task ExpectUnauthorized(HttpResponseMessage response)
        => (await response.ExpectProblem(HttpStatusCode.Unauthorized, "unauthenticated")).Has("detail").Should().BeFalse();

    private static IEnumerable<int> Ids(JsonElement day) => day.GetProperty("habits").EnumerateArray().Select(h => h.GetProperty("id").GetInt32());

    private static string Rows(JsonElement day)
        => string.Join(", ", day.GetProperty("habits").EnumerateArray().Select(h =>
            $"{h.GetProperty("name").GetString()} {h.GetProperty("state").GetString()} {h.GetProperty("count").GetInt32()}/{h.GetProperty("timesPerDay").GetInt32()}"));

    [DockerFact]
    public async Task A_key_returns_its_users_rows_for_today_and_tomorrow_in_the_saved_order_with_the_me_fields()
    {
        var bearer = await Host.BearerAsync();
        await (await bearer.Patch("/api/me", new { timezone = "Europe/Berlin" })).Expect(HttpStatusCode.OK);
        var pills = await HabitAsync(bearer, "Pills", timesPerDay: 2);
        var walk = await HabitAsync(bearer, "Walk");
        await HabitAsync(bearer, "Coffee", kind: "letGo");
        var brush = await HabitAsync(bearer, "Brush");
        var routine = (await (await bearer.Post("/api/routines", new { name = "Morning", habitIds = new[] { brush } })).ExpectJson(HttpStatusCode.Created))
            .GetProperty("id").GetInt32();
        await (await bearer.Put("/api/me/today-order", new { order = new[] { $"h:{walk}", $"r:{routine}", $"h:{pills}" } })).Expect(HttpStatusCode.OK);
        var key = await IssueAsync(bearer);
        var me = await (await bearer.Get("/api/me")).ExpectJson();
        await (await CheckIn(key, pills, Host.Today)).Expect(HttpStatusCode.OK);
        await (await CheckIn(key, walk, Host.Today)).Expect(HttpStatusCode.OK);

        var response = await Get($"Widget {key}");
        var day = await response.ExpectJson();

        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        day.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("userId", "timeZone", "date", "habits", "more", "done", "total", "next");
        day.GetProperty("userId").GetString().Should().Be(me.GetProperty("id").GetString());
        day.GetProperty("timeZone").GetString().Should().Be("Europe/Berlin").And.Be(me.GetProperty("timezone").GetString());
        day.GetProperty("date").GetString().Should().Be(me.GetProperty("localToday").GetString());
        Rows(day).Should().Be("Walk done 1/1, Brush open 0/1, Pills partial 1/2");
        (day.GetProperty("more").GetInt32(), day.GetProperty("done").GetInt32(), day.GetProperty("total").GetInt32()).Should().Be((0, 1, 3));

        var next = day.GetProperty("next");
        next.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("date", "habits", "more", "done", "total");
        next.GetProperty("date").GetString().Should().Be(Host.Today.AddDays(1).Iso());
        Rows(next).Should().Be("Walk open 0/1, Brush open 0/1, Pills open 0/2");
        (next.GetProperty("more").GetInt32(), next.GetProperty("done").GetInt32(), next.GetProperty("total").GetInt32()).Should().Be((0, 0, 3));
    }

    [DockerFact]
    public async Task Each_key_sees_only_its_own_users_habits()
    {
        var first = await Host.BearerAsync();
        var second = await Host.BearerAsync();
        var firstHabit = await HabitAsync(first, "Read");
        var secondHabit = await HabitAsync(second, "Read");
        var firstKey = await IssueAsync(first);
        var secondKey = await IssueAsync(second);

        var firstDay = await DayAsync(firstKey);
        var secondDay = await DayAsync(secondKey);

        Ids(firstDay).Should().Equal(firstHabit);
        Ids(firstDay.GetProperty("next")).Should().Equal(firstHabit);
        firstDay.GetProperty("userId").GetString().Should().Be((await first.IdAsync()).ToString());
        Ids(secondDay).Should().Equal(secondHabit);
        Ids(secondDay.GetProperty("next")).Should().Equal(secondHabit);
        secondDay.GetProperty("userId").GetString().Should().Be((await second.IdAsync()).ToString());
    }

    [DockerFact]
    public async Task Missing_malformed_unknown_bearer_or_cookie_credentials_are_401_without_detail()
    {
        var bearer = await Host.BearerAsync();
        await HabitAsync(bearer, "Walk");
        var key = await IssueAsync(bearer);
        var accessToken = bearer.Http.DefaultRequestHeaders.Authorization!.Parameter;

        foreach (var authorization in new[]
        {
            null, "Widget", "Widget ", $"Bearer {key}", $"Bearer {accessToken}", $"Widget {key}x", $"Widget {key[..^1]}",
            $"Widget {new string('A', 129)}", $"Widget{key}",
        })
            await ExpectUnauthorized(await Get(authorization));

        await ExpectUnauthorized(await (await Host.SignedInAsync()).Get(Today));
    }

    [DockerFact]
    public async Task Signing_out_revokes_only_that_key_and_session_expiry_revokes_the_rest()
    {
        var leaving = await Host.BearerAsync();
        var staying = await Host.BearerAsync();
        var leavingKey = await IssueAsync(leaving);
        var stayingKey = await IssueAsync(staying);

        await (await leaving.Post("/api/auth/logout")).Expect(HttpStatusCode.NoContent);

        await ExpectUnauthorized(await Get($"Widget {leavingKey}"));
        await (await Get($"Widget {stayingKey}")).Expect(HttpStatusCode.OK);

        Host.Time.Advance(AuthSessions.Lifetime + TimeSpan.FromMinutes(1));

        await ExpectUnauthorized(await Get($"Widget {stayingKey}"));
    }

    [DockerFact]
    public async Task Withdrawn_consent_is_403_without_any_habit()
    {
        var bearer = await Host.BearerAsync();
        await HabitAsync(bearer, "Walk");
        var key = await IssueAsync(bearer);

        await (await bearer.Post("/api/me/consent/withdraw")).Expect(HttpStatusCode.NoContent);

        var problem = await (await Get($"Widget {key}")).ExpectProblem(HttpStatusCode.Forbidden, "consent_required");
        problem.Has("habits").Should().BeFalse();
    }

    [DockerFact]
    public async Task Today_and_tomorrow_move_at_the_users_local_midnight_and_yesterdays_check_in_drops_out()
    {
        var bearer = await Host.BearerAsync();
        await (await bearer.Patch("/api/me", new { timezone = "Europe/Berlin" })).Expect(HttpStatusCode.OK);
        var habitId = await HabitAsync(bearer, "Walk");
        var key = await IssueAsync(bearer);
        var sunday = new DateOnly(2026, 11, 1);
        await (await CheckIn(key, habitId, sunday)).Expect(HttpStatusCode.OK);

        Host.Time.SetUtcNow(new DateTimeOffset(2026, 11, 1, 22, 59, 59, TimeSpan.Zero));
        var before = await DayAsync(key);

        Host.Time.SetUtcNow(new DateTimeOffset(2026, 11, 1, 23, 0, 0, TimeSpan.Zero));
        var after = await DayAsync(key);

        before.GetProperty("date").GetString().Should().Be(sunday.Iso());
        Rows(before).Should().Be("Walk done 1/1");
        before.GetProperty("next").GetProperty("date").GetString().Should().Be(sunday.AddDays(1).Iso());
        after.GetProperty("date").GetString().Should().Be(sunday.AddDays(1).Iso());
        Rows(after).Should().Be("Walk open 0/1");
        after.GetProperty("done").GetInt32().Should().Be(0);
        after.GetProperty("next").GetProperty("date").GetString().Should().Be(sunday.AddDays(2).Iso());
    }
}

public class WidgetTodayRateLimitTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override Dictionary<string, string?>? Settings => new() { ["Ember:RateLimits:WidgetPerIpPerMinute"] = "3" };

    [DockerFact]
    public async Task Reading_the_day_shares_the_per_ip_widget_limit_with_check_ins()
    {
        async Task<HttpResponseMessage> Send(HttpMethod method, string url, string ip)
        {
            var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post) request.Content = JsonContent.Create(new { habitId = 1, date = Host.Today.Iso() });
            request.Headers.TryAddWithoutValidation("Authorization", "Widget unknown-key");
            request.Headers.Add("X-Forwarded-For", ip);
            return await Host.NewClient().Http.SendAsync(request);
        }

        await (await Send(HttpMethod.Post, "/api/widget/check-in", "203.0.113.50")).Expect(HttpStatusCode.Unauthorized);
        await (await Send(HttpMethod.Post, "/api/widget/check-in", "203.0.113.50")).Expect(HttpStatusCode.Unauthorized);
        await (await Send(HttpMethod.Get, "/api/widget/today", "203.0.113.50")).Expect(HttpStatusCode.Unauthorized);

        await (await Send(HttpMethod.Get, "/api/widget/today", "203.0.113.50")).ExpectProblem((HttpStatusCode)429, "rate_limited");
        await (await Send(HttpMethod.Get, "/api/widget/today", "203.0.113.51")).Expect(HttpStatusCode.Unauthorized);
    }
}
