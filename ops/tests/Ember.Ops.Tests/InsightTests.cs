using Ember.Ops.Lookout;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ember.Ops.Tests;

/// <summary>Every number is counted by hand from <see cref="Dataset"/>; today is Wednesday 2026-11-18, its week starts 2026-11-16.</summary>
[Collection(V2Collection.Name)]
public sealed class InsightTests(V2DatabaseFixture fixture) : IDisposable
{
    private static readonly DateOnly Today = DatabaseFixture.Today;
    private readonly OpsApiFactory _factory = new(fixture, hubAddress: OpsApiFactory.HubIp, lookoutKey: OpsApiFactory.LookoutKey);

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> PageAsync(string id)
    {
        using var client = _factory.ClientFrom(OpsApiFactory.HubIp);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {OpsApiFactory.LookoutKey}");
        return await client.GetFromJsonAsync<JsonElement>($"/lookout/v1/pages/{id}");
    }

    private static double[] Values(JsonElement bars, string label) =>
        bars.GetProperty("bars").EnumerateArray().Single(b => b.GetProperty("label").GetString() == label).GetProperty("values").EnumerateArray().Select(v => v.GetDouble()).ToArray();

    [Fact]
    public async Task Overview_series_carry_the_user_trend_the_rolling_active_7_and_the_earlier_periods()
    {
        await using var db = fixture.OpenOpsDatabases();
        var series = await OverviewQueries.SeriesAsync(db.Ember, await EmberFeatures.DetectAsync(db.Ember, default), fixture.Time, default);

        series.UsersBefore.Should().Be(1, "u4's first habit is 100 days old");
        series.Active7PerDay.Select(d => d.Count).Should().Equal(0, 0, 0, 0, 0, 0, 1, 2, 2, 2, 3, 3, 3, 2);
        series.Active7PerDay[^1].Date.Should().Be(Today);
        (series.Active7Before, series.Active30Before).Should().Be((1, 1), "u3 completed 7 days ago and u4 30 days ago");
    }

    [Fact]
    public async Task The_overview_tiles_carry_the_v1_1_fields()
    {
        var tiles = LookoutShape.Widget(await PageAsync("overview"), "tiles");

        var users = LookoutShape.Item(tiles, "Users");
        users.GetProperty("trend").EnumerateArray().Select(t => t.GetDouble()).Should().Equal(1, 1, 1, 1, 1, 1, 1, 1, 2, 4, 4, 5);
        users.GetProperty("delta").GetProperty("value").GetDouble().Should().Be(1);
        users.GetProperty("delta").GetProperty("better").GetString().Should().Be("up");
        var active7 = LookoutShape.Item(tiles, "Active 7 days");
        active7.GetProperty("max").GetDouble().Should().Be(6);
        active7.GetProperty("trend").GetArrayLength().Should().Be(14);
        active7.GetProperty("delta").GetProperty("value").GetDouble().Should().Be(1);
        LookoutShape.Item(tiles, "Active 30 days").GetProperty("delta").GetProperty("value").GetDouble().Should().Be(2);
        LookoutShape.Item(tiles, "Streak users").GetProperty("max").GetDouble().Should().Be(6);
    }

    [Fact]
    public async Task Overview_check_in_journal_and_low_day_bars_leave_out_days_with_one_or_two_people()
    {
        var overview = await PageAsync("overview");

        var checkIns = LookoutShape.Widget(overview, "check-ins");
        checkIns.GetProperty("bars").GetArrayLength().Should().Be(84, "six days had check-ins from one or two people");
        checkIns.GetProperty("bars").EnumerateArray().Should().OnlyContain(b => b.GetProperty("values")[0].GetDouble() == 0);

        var journal = LookoutShape.Widget(overview, "journal");
        journal.GetProperty("bars").GetArrayLength().Should().Be(89, "yesterday had one writer");
        Values(journal, Today.ToString("yyyy-MM-dd")).Should().Equal(3);
        journal.GetProperty("better").GetString().Should().Be("up");
        var lowDays = LookoutShape.Widget(overview, "low-days");
        lowDays.GetProperty("bars").EnumerateArray().Should().OnlyContain(b => b.GetProperty("values")[0].GetDouble() == 0);
        lowDays.GetProperty("better").GetString().Should().Be("down");
        lowDays.GetProperty("note").GetString().Should().Contain("1 or 2 people");
    }

    [Fact]
    public async Task Activity_counts_users_per_day_week_and_active_days_and_suppresses_small_buckets()
    {
        await using var db = fixture.OpenOpsDatabases();
        var a = await InsightQueries.ActivityAsync(db.Ember, fixture.Time, default);

        a.ActivePerDay.Where(d => d.Count > 0).Select(d => (Today.DayNumber - d.Date.DayNumber, d.Count))
            .Should().BeEquivalentTo([(0, 2), (3, 1), (6, 1), (7, 1), (29, 1), (30, 1)]);
        a.ActivePerWeek.Where(w => w.Users > 0).Select(w => (w.Label, w.Users)).Should().Equal(("2026-10-19", 2), ("2026-11-09", 3), ("2026-11-16", 2));
        a.ActiveDays.Select(b => b.Users).Should().Equal(0, 3, 0, 0, 0);
        a.Cohorts.Where(c => c.Users > 0).Should().Equal(new Cohort(new(2026, 10, 26), 1, 0, 0), new Cohort(new(2026, 11, 2), 2, 2, 0), new Cohort(new(2026, 11, 16), 1, 0, 0));
        (a.Journal30, a.Writers30, a.PrivateJournals).Should().Be((4, 4, 1));
        a.JournalPerWeek.Single(w => w.Count > 0).Should().Be(new UserBucket("2026-11-16", 4, 4));
        a.LowDaysPerWeek.Single(w => w.Count > 0).Should().Be(new UserBucket("2026-11-16", 3, 2));

        var page = await PageAsync("activity");
        var tiles = LookoutShape.Widget(page, "tiles");
        LookoutShape.Item(tiles, "Active today").GetProperty("value").GetString().Should().Be(LookoutPages.Suppressed);
        LookoutShape.Item(tiles, "Average per day").GetProperty("value").GetDouble().Should().Be(0.2);
        LookoutShape.Item(tiles, "Stickiness").GetProperty("value").GetDouble().Should().BeApproximately(0.2 / 3, 1e-9);
        var perWeek = LookoutShape.Widget(page, "active-per-week");
        perWeek.GetProperty("bars").GetArrayLength().Should().Be(10);
        Values(perWeek, "2026-11-09").Should().Equal(3);
        Values(LookoutShape.Widget(page, "active-days"), "2–4 days").Should().Equal(3);
        var retention = LookoutShape.Widget(page, "retention").GetProperty("children");
        var rates = retention[0];
        LookoutShape.Item(rates, "Started, 12 weeks").GetProperty("value").GetDouble().Should().Be(4);
        LookoutShape.Item(rates, "1 week later").GetProperty("value").GetString().Should().Be(LookoutPages.Suppressed, "two of three came back");
        LookoutShape.Item(rates, "4 weeks later").GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null, "no cohort is 4 weeks past");
        retention[1].GetProperty("bars").EnumerateArray().Should().OnlyContain(b => b.GetProperty("values").EnumerateArray().All(v => v.GetDouble() == 0));
        var journal = LookoutShape.Widget(page, "journal-activity").GetProperty("children");
        LookoutShape.Item(journal[0], "People who wrote").GetProperty("value").GetDouble().Should().Be(4);
        LookoutShape.Item(journal[0], "Private journal on").GetProperty("value").GetString().Should().Be(LookoutPages.Suppressed);
        Values(journal[1], "2026-11-16").Should().Equal(4);
        LookoutShape.Widget(page, "low-days-week").GetProperty("bars").GetArrayLength().Should().Be(11, "this week's low days come from two people");
    }

    [Fact]
    public async Task Habits_counts_schedules_check_ins_per_day_reminder_hours_and_habits_per_user()
    {
        await using var db = fixture.OpenOpsDatabases();
        var h = await InsightQueries.HabitsAsync(db.Ember, fixture.Time, default);

        (h.Habits, h.UsersWithHabits, h.InRoutine, h.WithReminderTime, h.Routines).Should().Be((8, 5, 1, 3, 3));
        h.BySchedule.Should().Equal(("Daily", 6), ("Times per week", 1), ("Set weekdays", 1));
        h.ByTimesPerDay.Should().Equal(("1", 6), ("2", 0), ("3", 1), ("4+", 1));
        h.ReminderHours.Where(r => r.Habits > 0 || r.Routines > 0).Should().Equal((7, 0, 1), (8, 2, 0), (20, 1, 0), (21, 1, 0));
        h.UsersByHabits.Should().Equal(("0", 1), ("1", 2), ("2–3", 3), ("4–6", 0), ("7+", 0));
        h.CreatedPerWeek.Where(w => w.Habits > 0 || w.Routines > 0)
            .Should().Equal((new DateOnly(2026, 10, 26), 1, 0), (new DateOnly(2026, 11, 2), 3, 0), (new DateOnly(2026, 11, 9), 1, 1), (new DateOnly(2026, 11, 16), 2, 2));
    }

    [Fact]
    public async Task Users_counts_sign_in_reminder_destinations_and_designs()
    {
        await using var db = fixture.OpenOpsDatabases();
        var u = await InsightQueries.UsersAsync(db.Ember, fixture.Time, default);

        (u.Users, u.DiscordLinked, u.DiscordDms, u.WebPush, u.Phone, u.NoDestination).Should().Be((6, 2, 2, 2, 1, 4));
        u.ByTheme.Should().Equal(("hearthlight", 0), ("fieldnotes", 0), ("tidepool", 2), ("marigold", 1), ("default", 3));

        var tiles = LookoutShape.Widget(await PageAsync("users"), "tiles");
        LookoutShape.Item(tiles, "With a reminder destination").GetProperty("value").GetDouble().Should().Be(2);
        LookoutShape.Item(tiles, "Discord linked").GetProperty("max").GetDouble().Should().Be(6);
    }
}
