using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests;

/// <summary>The account settings on <c>/api/me</c>: local day, language, grace days, time zone, and the Today order next to new habits.</summary>
public class MeSettingsApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static async Task<JsonElement> MeAsync(ApiClient user) => await (await user.Get("/api/me")).ExpectJson();

    private static async Task<string?> LocalTodayAsync(ApiClient user) => (await MeAsync(user)).GetProperty("localToday").GetString();

    private static string[] ErrorMessages(JsonElement problem, string field)
        => problem.GetProperty("errors").GetProperty(field).EnumerateArray().Select(e => e.GetString()!).ToArray();

    [DockerFact]
    public async Task Local_today_follows_a_half_hour_zone_just_before_and_after_its_midnight()
    {
        var user = await Host.SignedInAsync();
        await (await user.Patch("/api/me", new { timezone = "Asia/Kolkata" })).Expect(HttpStatusCode.OK);

        Host.Time.SetUtcNow(new DateTimeOffset(2026, 11, 1, 18, 29, 59, TimeSpan.Zero));
        var before = await LocalTodayAsync(user);
        Host.Time.SetUtcNow(new DateTimeOffset(2026, 11, 1, 18, 30, 0, TimeSpan.Zero));
        var after = await LocalTodayAsync(user);

        before.Should().Be("2026-11-01");
        after.Should().Be("2026-11-02");
    }

    [DockerFact]
    public async Task Local_today_is_the_utc_day_while_the_stored_zone_is_unknown()
    {
        var user = await Host.SignedInAsync();
        var userId = await user.IdAsync();
        await Host.Db(db => db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Timezone, "Mars/Olympus")));
        Host.Time.SetUtcNow(new DateTimeOffset(2026, 11, 1, 23, 30, 0, TimeSpan.Zero));

        (await LocalTodayAsync(user)).Should().Be("2026-11-01");
    }

    [DockerFact]
    public async Task A_new_account_speaks_english_and_patching_a_supported_language_stores_it()
    {
        var user = await Host.SignedInAsync();
        (await MeAsync(user)).GetProperty("language").GetString().Should().Be("en");

        var patched = await (await user.Patch("/api/me", new { language = "de" })).ExpectJson();

        patched.GetProperty("language").GetString().Should().Be("de");
        (await MeAsync(user)).GetProperty("language").GetString().Should().Be("de");
    }

    [DockerFact]
    public async Task Patching_an_unsupported_language_is_a_400_and_writes_nothing()
    {
        var user = await Host.SignedInAsync();

        var problem = await (await user.Patch("/api/me", new { language = "fr", graceDaysPerWeek = 2 })).ExpectProblem(HttpStatusCode.BadRequest, "validation");

        ErrorMessages(problem, "language").Should().Equal("Must be en or de.");
        var me = await MeAsync(user);
        me.GetProperty("language").GetString().Should().Be("en");
        me.GetProperty("graceDaysPerWeek").GetInt32().Should().Be(1);
    }

    [DockerFact]
    public async Task Patching_grace_days_accepts_zero_to_three_and_rejects_values_outside()
    {
        var user = await Host.SignedInAsync();

        var zero = await (await user.Patch("/api/me", new { graceDaysPerWeek = 0 })).ExpectJson();
        var three = await (await user.Patch("/api/me", new { graceDaysPerWeek = 3 })).ExpectJson();
        var below = await (await user.Patch("/api/me", new { graceDaysPerWeek = -1 })).ExpectProblem(HttpStatusCode.BadRequest, "validation");
        var above = await (await user.Patch("/api/me", new { graceDaysPerWeek = 4 })).ExpectProblem(HttpStatusCode.BadRequest, "validation");

        zero.GetProperty("graceDaysPerWeek").GetInt32().Should().Be(0);
        three.GetProperty("graceDaysPerWeek").GetInt32().Should().Be(3);
        ErrorMessages(below, "graceDaysPerWeek").Should().Equal("Must be between 0 and 3.");
        ErrorMessages(above, "graceDaysPerWeek").Should().Equal("Must be between 0 and 3.");
        (await MeAsync(user)).GetProperty("graceDaysPerWeek").GetInt32().Should().Be(3);
    }

    [DockerFact]
    public async Task Patching_an_unknown_or_windows_zone_answers_the_unknown_iana_timezone_error()
    {
        var user = await Host.SignedInAsync();

        var unknown = await (await user.Patch("/api/me", new { timezone = "Mars/Olympus" })).ExpectProblem(HttpStatusCode.BadRequest, "validation");
        var windows = await (await user.Patch("/api/me", new { timezone = "W. Europe Standard Time" })).ExpectProblem(HttpStatusCode.BadRequest, "validation");

        ErrorMessages(unknown, "timezone").Should().Equal("Unknown IANA timezone.");
        ErrorMessages(windows, "timezone").Should().Equal("Unknown IANA timezone.");
    }

    [DockerFact]
    public async Task A_habit_added_after_saving_the_today_order_stays_out_of_the_stored_order()
    {
        var user = await Host.SignedInAsync();
        var first = (await user.CreateHabitAsync()).GetProperty("id").GetInt32();
        var second = (await user.CreateHabitAsync()).GetProperty("id").GetInt32();
        await (await user.Put("/api/me/today-order", new { order = new[] { $"h:{second}", $"h:{first}" } })).Expect(HttpStatusCode.OK);

        var added = (await user.CreateHabitAsync()).GetProperty("id").GetInt32();

        var order = (await MeAsync(user)).GetProperty("todayOrder").EnumerateArray().Select(k => k.GetString()).ToArray();
        order.Should().Equal($"h:{second}", $"h:{first}");
        order.Should().NotContain($"h:{added}");
    }

    [DockerFact]
    public async Task A_patch_after_the_session_cookie_is_revalidated_answers_the_stored_settings()
    {
        var user = await Host.SignedInAsync();
        await (await user.Patch("/api/me", new { language = "de", timezone = "Asia/Kolkata" })).Expect(HttpStatusCode.OK);
        Host.Time.Advance(TimeSpan.FromMinutes(10));

        var patched = await (await user.Patch("/api/me", new { language = "en", timezone = "Pacific/Auckland" })).ExpectJson();

        patched.GetProperty("language").GetString().Should().Be("en");
        patched.GetProperty("timezone").GetString().Should().Be("Pacific/Auckland");
    }
}
