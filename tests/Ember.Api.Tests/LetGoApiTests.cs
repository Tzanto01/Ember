using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests;

/// <summary>Let-go habits (contract "Let go habits v1.21").</summary>
public class LetGoApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static async Task<int> CreateLetGoAsync(ApiClient user, string name = "Coffee")
        => (await (await user.Post("/api/habits", new { name, kind = "letGo", frequency = "daily" })).ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();

    private static async Task ExpectFieldAsync(HttpResponseMessage response, string field)
        => (await response.ExpectProblem(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").Has(field).Should().BeTrue();

    [DockerFact]
    public async Task A_let_go_habit_carries_its_kind_days_free_stats_and_a_today_without_check_in_fields()
    {
        var user = await Host.SignedInAsync();

        var letGo = await (await user.Post("/api/habits", new { name = "Coffee", kind = "letGo", frequency = "daily" })).ExpectJson(HttpStatusCode.Created);
        var build = await user.CreateHabitAsync("Water");

        letGo.GetProperty("kind").GetString().Should().Be("letGo");
        letGo.GetProperty("frequency").GetString().Should().Be("daily");
        letGo.GetProperty("stats").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("daysFree", "longestStretch", "freeDaysThisMonth", "slipDaysLast30");
        letGo.GetProperty("today").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("date", "state", "slips");
        letGo.GetProperty("today").GetProperty("state").GetString().Should().Be("free");
        letGo.GetProperty("stats").GetProperty("daysFree").GetInt32().Should().Be(1);
        build.GetProperty("kind").GetString().Should().Be("build");
        build.GetProperty("stats").Has("streak").Should().BeTrue();
        build.GetProperty("today").GetProperty("state").GetString().Should().Be("pending");
        (await (await user.Post("/api/habits", new { name = "Run", kind = "other" })).ExpectProblem(HttpStatusCode.BadRequest, "validation"))
            .GetProperty("errors").Has("kind").Should().BeTrue();
    }

    [DockerFact]
    public async Task Let_go_constraints_are_a_400_on_the_offending_field_for_create_and_patch_and_the_kind_cannot_change()
    {
        var user = await Host.SignedInAsync();

        await ExpectFieldAsync(await user.Post("/api/habits", new { name = "A", kind = "letGo", frequency = "weekly", weeklyTarget = 2 }), "frequency");
        await ExpectFieldAsync(await user.Post("/api/habits", new { name = "A", kind = "letGo", frequency = "daily", timesPerDay = 2 }), "timesPerDay");
        await ExpectFieldAsync(await user.Post("/api/habits", new { name = "A", kind = "letGo", frequency = "daily", weeklyTarget = 2 }), "weeklyTarget");
        await ExpectFieldAsync(await user.Post("/api/habits", new { name = "A", kind = "letGo", frequency = "daily", days = new[] { "mon" } }), "days");
        await ExpectFieldAsync(await user.Post("/api/habits", new { name = "A", kind = "letGo", frequency = "daily", reminderTimes = new[] { "20:00", "21:00" } }), "reminderTimes");
        (await (await user.Get("/api/habits")).ExpectJson()).GetArrayLength().Should().Be(0);

        var id = await CreateLetGoAsync(user);
        await ExpectFieldAsync(await user.Patch($"/api/habits/{id}", new { frequency = "weekly", weeklyTarget = 2 }), "frequency");
        await ExpectFieldAsync(await user.Patch($"/api/habits/{id}", new { timesPerDay = 3 }), "timesPerDay");
        await ExpectFieldAsync(await user.Patch($"/api/habits/{id}", new { reminderTimes = new[] { "20:00", "21:00" } }), "reminderTimes");
        await ExpectFieldAsync(await user.Patch($"/api/habits/{id}", new { kind = "build" }), "kind");
        var build = (await user.CreateHabitAsync()).GetProperty("id").GetInt32();
        await ExpectFieldAsync(await user.Patch($"/api/habits/{build}", new { kind = "letGo" }), "kind");

        var quiet = await (await user.Patch($"/api/habits/{id}", new { reminderTime = "21:30", kind = "letGo" })).ExpectJson();
        quiet.GetProperty("reminderTimes").EnumerateArray().Select(t => t.GetString()).Should().Equal("21:30");
    }

    [DockerFact]
    public async Task A_slip_adds_to_the_days_count_appends_each_reason_once_and_shows_in_the_habit()
    {
        var user = await Host.SignedInAsync();
        var id = await CreateLetGoAsync(user);
        var url = $"/api/habits/{id}/slips";
        var today = Host.Today.Iso();

        await (await user.Post(url, new { date = today, reason = "stressed" })).ExpectJson();
        await (await user.Post(url, new { date = today, reason = "stressed" })).ExpectJson();
        var habit = await (await user.Post(url, new { date = today, reason = "sad" })).ExpectJson();

        habit.GetProperty("today").GetProperty("state").GetString().Should().Be("slipped");
        habit.GetProperty("today").GetProperty("slips").GetInt32().Should().Be(3);
        habit.GetProperty("stats").GetProperty("daysFree").GetInt32().Should().Be(0);
        habit.GetProperty("stats").GetProperty("slipDaysLast30").GetInt32().Should().Be(1);
        var days = await (await user.Get($"{url}?from={today}&to={today}")).ExpectJson();
        var day = days.EnumerateArray().Single();
        day.GetProperty("date").GetString().Should().Be(today);
        day.GetProperty("count").GetInt32().Should().Be(3);
        day.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).Should().Equal("stressed", "sad");
    }

    [DockerFact]
    public async Task Slips_are_refused_for_an_unknown_reason_a_future_day_a_day_before_the_start_and_a_build_habit()
    {
        var user = await Host.SignedInAsync();
        var id = await CreateLetGoAsync(user);
        var build = (await user.CreateHabitAsync()).GetProperty("id").GetInt32();

        await ExpectFieldAsync(await user.Post($"/api/habits/{id}/slips", new { date = Host.Today.Iso(), reason = "hungry" }), "reason");
        await ExpectFieldAsync(await user.Post($"/api/habits/{id}/slips", new { date = Host.Today.AddDays(1).Iso() }), "date");
        await ExpectFieldAsync(await user.Post($"/api/habits/{id}/slips", new { date = Host.Today.AddDays(-1).Iso() }), "date");
        await ExpectFieldAsync(await user.Post($"/api/habits/{id}/slips", new { date = "yesterday" }), "date");
        await ExpectFieldAsync(await user.Post($"/api/habits/{build}/slips", new { date = Host.Today.Iso() }), "kind");
        await (await user.Post("/api/habits/999999/slips", new { date = Host.Today.Iso() })).ExpectProblem(HttpStatusCode.NotFound, "not_found");

        (await (await user.Get($"/api/habits/{id}/slips")).ExpectJson()).GetArrayLength().Should().Be(0);
    }

    [DockerFact]
    public async Task Deleting_a_days_slips_undoes_them_and_a_day_without_slips_is_still_a_200_while_another_users_habit_is_a_404()
    {
        var user = await Host.SignedInAsync();
        var id = await CreateLetGoAsync(user);
        var today = Host.Today.Iso();
        await (await user.Post($"/api/habits/{id}/slips", new { date = today })).ExpectJson();
        await (await user.Post($"/api/habits/{id}/slips", new { date = today })).ExpectJson();

        var undone = await (await user.Delete($"/api/habits/{id}/slips/{today}")).ExpectJson();
        var again = await (await user.Delete($"/api/habits/{id}/slips/{today}")).ExpectJson();

        undone.GetProperty("today").GetProperty("state").GetString().Should().Be("free");
        undone.GetProperty("today").GetProperty("slips").GetInt32().Should().Be(0);
        again.GetProperty("stats").GetProperty("daysFree").GetInt32().Should().Be(1);
        (await (await user.Get($"/api/habits/{id}/slips?from={today}&to={today}")).ExpectJson()).GetArrayLength().Should().Be(0);
        var other = await Host.SignedInAsync();
        await (await other.Delete($"/api/habits/{id}/slips/{today}")).ExpectProblem(HttpStatusCode.NotFound, "not_found");
    }

    [DockerFact]
    public async Task Listing_slips_returns_the_range_oldest_first_and_validates_it_like_days()
    {
        var user = await Host.SignedInAsync();
        var id = await CreateLetGoAsync(user);
        Host.Time.Advance(TimeSpan.FromDays(3));
        var today = Host.Today;
        await (await user.Post($"/api/habits/{id}/slips", new { date = today.Iso() })).ExpectJson();
        await (await user.Post($"/api/habits/{id}/slips", new { date = today.AddDays(-2).Iso(), reason = "tired" })).ExpectJson();

        var all = await (await user.Get($"/api/habits/{id}/slips?from={today.AddDays(-3).Iso()}")).ExpectJson();
        var recent = await (await user.Get($"/api/habits/{id}/slips?from={today.AddDays(-1).Iso()}")).ExpectJson();

        all.EnumerateArray().Select(s => s.GetProperty("date").GetString()).Should().Equal(today.AddDays(-2).Iso(), today.Iso());
        recent.EnumerateArray().Select(s => s.GetProperty("date").GetString()).Should().Equal(today.Iso());
        await ExpectFieldAsync(await user.Get($"/api/habits/{id}/slips?to={today.AddDays(1).Iso()}"), "to");
        await ExpectFieldAsync(await user.Get($"/api/habits/{id}/slips?from={today.Iso()}&to={today.AddDays(-1).Iso()}"), "from");
    }

    [DockerFact]
    public async Task Check_in_days_of_a_let_go_habit_are_a_400_on_kind_for_read_and_write()
    {
        var user = await Host.SignedInAsync();
        var id = await CreateLetGoAsync(user);
        var url = $"/api/habits/{id}/days/{Host.Today.Iso()}";

        await ExpectFieldAsync(await user.Put(url, new { completed = true }), "kind");
        await ExpectFieldAsync(await user.Get($"/api/habits/{id}/days"), "kind");
    }

    [DockerFact]
    public async Task A_routine_cannot_hold_a_let_go_habit()
    {
        var user = await Host.SignedInAsync();
        var letGo = await CreateLetGoAsync(user);
        var build = (await user.CreateHabitAsync()).GetProperty("id").GetInt32();

        await ExpectFieldAsync(await user.Post("/api/routines", new { name = "Morning", habitIds = new[] { build, letGo } }), "habitIds");
        var routine = (await (await user.Post("/api/routines", new { name = "Morning", habitIds = new[] { build } })).ExpectJson(HttpStatusCode.Created))
            .GetProperty("id").GetInt32();
        await ExpectFieldAsync(await user.Patch($"/api/routines/{routine}", new { habitIds = new[] { build, letGo } }), "habitIds");

        (await (await user.Get("/api/routines")).ExpectJson()).EnumerateArray().Single().GetProperty("habitIds").GetArrayLength().Should().Be(1);
    }

    [DockerFact]
    public async Task Weekly_and_year_stats_ignore_let_go_habits_even_with_slips()
    {
        var user = await Host.SignedInAsync();
        var build = (await user.CreateHabitAsync("Water")).GetProperty("id").GetInt32();
        var letGo = await CreateLetGoAsync(user);
        Host.Time.Advance(TimeSpan.FromDays(3));
        var today = Host.Today;
        await (await user.Put($"/api/habits/{build}/days/{today.AddDays(-1).Iso()}", new { completed = true })).ExpectJson();
        await (await user.Post($"/api/habits/{letGo}/slips", new { date = today.AddDays(-2).Iso() })).ExpectJson();

        var weekly = await (await user.Get("/api/stats/weekly")).ExpectJson();
        var days = await (await user.Get($"/api/stats/days?from={today.AddDays(-2).Iso()}&to={today.AddDays(-1).Iso()}")).ExpectJson();

        weekly.GetProperty("habits").EnumerateArray().Select(h => h.GetProperty("habitId").GetInt32()).Should().Equal(build);
        weekly.GetProperty("totalDone").GetInt32().Should().Be(1);
        weekly.GetProperty("totalPossible").GetInt32().Should().Be(2);
        days.EnumerateArray().Select(d => (d.GetProperty("scheduled").GetInt32(), d.GetProperty("done").GetInt32(), d.GetProperty("showedUp").GetBoolean()))
            .Should().Equal((1, 0, false), (1, 1, true));
    }

    [DockerFact]
    public async Task Deleting_a_let_go_habit_deletes_its_slips()
    {
        var user = await Host.SignedInAsync();
        var id = await CreateLetGoAsync(user);
        await (await user.Post($"/api/habits/{id}/slips", new { date = Host.Today.Iso() })).ExpectJson();

        await (await user.Delete($"/api/habits/{id}")).Expect(HttpStatusCode.NoContent);

        (await Host.Db(db => db.HabitSlips.CountAsync())).Should().Be(0);
    }
}
