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

    [Fact]
    public async Task The_overview_tiles_carry_the_user_trend_and_the_sign_in_windows()
    {
        var tiles = LookoutShape.Widget(await PageAsync("overview"), "tiles");

        var users = LookoutShape.Item(tiles, "Users");
        users.GetProperty("trend").EnumerateArray().Select(t => t.GetDouble()).Should().Equal(1, 1, 1, 1, 1, 1, 1, 1, 2, 4, 4, 5);
        users.GetProperty("delta").GetProperty("value").GetDouble().Should().Be(1);
        users.GetProperty("delta").GetProperty("better").GetString().Should().Be("up");
        var active7 = LookoutShape.Item(tiles, "Active 7 days");
        (active7.GetProperty("value").GetInt32(), active7.GetProperty("max").GetDouble()).Should().Be((2, 6));
        active7.TryGetProperty("trend", out _).Should().BeFalse("there is no snapshot yet");
        active7.TryGetProperty("delta", out _).Should().BeFalse();
        var active14 = LookoutShape.Item(tiles, "Active 14 days");
        active14.GetProperty("value").GetInt32().Should().Be(4);
        active14.TryGetProperty("delta", out _).Should().BeFalse();
        var streak = LookoutShape.Item(tiles, "Streak users");
        (streak.GetProperty("value").GetInt32(), streak.GetProperty("hint").GetString(), streak.GetProperty("max").GetDouble()).Should().Be((3, "50% of users", 6));
    }

    [Fact]
    public async Task Habits_counts_schedules_check_ins_per_day_reminder_hours_and_habits_per_user()
    {
        await using var db = fixture.OpenOpsDatabases();
        var h = await InsightQueries.HabitsAsync(db.Ember, fixture.Time, default);

        (h.Habits, h.UsersWithHabits, h.InRoutine, h.WithReminderTime, h.Routines).Should().Be((8, 5, 1, 4, 3));
        h.BySchedule.Should().Equal(("Daily", 6), ("Times per week", 1), ("Set weekdays", 1));
        h.ByTimesPerDay.Should().Equal(("1", 6), ("2", 0), ("3", 1), ("4+", 1));
        h.ReminderHours.Where(r => r.Habits > 0 || r.Routines > 0).Should().Equal((7, 0, 1), (8, 2, 0), (9, 1, 0), (20, 1, 0), (21, 1, 0));
        h.UsersByHabits.Should().Equal(("0", 1), ("1", 2), ("2–3", 3), ("4–6", 0), ("7+", 0));
        h.CreatedPerWeek.Where(w => w.Habits > 0 || w.Routines > 0)
            .Should().Equal((new DateOnly(2026, 10, 26), 1, 0), (new DateOnly(2026, 11, 2), 3, 0), (new DateOnly(2026, 11, 9), 1, 1), (new DateOnly(2026, 11, 16), 2, 2));

        LookoutShape.Widget(await PageAsync("habits"), "times-per-day").GetProperty("title").GetString().Should().Be("Habits by times per day");
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
