using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Ember.Ops.Tests;

/// <summary>
/// Sign-in activity: the snapshots and what is read back from them. The sessions are those of <see cref="Dataset"/> (2 users seen in the last 7 days,
/// 1 today, 2 this week); today is Wednesday 2026-11-18. The snapshot tables are emptied around every test.
/// </summary>
[Collection(V2Collection.Name)]
public sealed class ActivityTests(V2DatabaseFixture fixture) : IAsyncLifetime, IDisposable
{
    private static readonly DateOnly Today = DatabaseFixture.Today;
    private readonly OpsApiFactory _factory = new(fixture, hubAddress: OpsApiFactory.HubIp, lookoutKey: OpsApiFactory.LookoutKey);
    private Guid? _extraSession;

    public void Dispose() => _factory.Dispose();

    public async Task InitializeAsync()
    {
        await using var db = fixture.OpenOpsDatabases();
        await OpsMigrator.ApplyAsync(db.Ops);
        await ClearAsync();
    }

    public async Task DisposeAsync()
    {
        if (_extraSession is { } id) await RunAsync("emberdb", """DELETE FROM "AuthSessions" WHERE "Id" = $1""", id);
        await ClearAsync();
    }

    private Task ClearAsync() => RunAsync("emberops", "TRUNCATE activity_days, activity_weeks");

    private async Task RunAsync(string database, string sql, params object[] parameters)
    {
        await using var connection = await fixture.OpenAsync(database);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters) command.Parameters.Add(new NpgsqlParameter { Value = p });
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> TextsAsync(string database, string sql)
    {
        await using var connection = await fixture.OpenAsync(database);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    private Task<List<string>> StoredDaysAsync() =>
        TextsAsync("emberops", "SELECT day::text || ' ' || users || ' ' || active7 FROM activity_days ORDER BY day");

    private Task<List<string>> StoredWeeksAsync() =>
        TextsAsync("emberops", "SELECT week::text || ' ' || users FROM activity_weeks ORDER BY week");

    private async Task SeedAsync(IEnumerable<(int Ago, int Users, int Active7)> days, IEnumerable<(DateOnly Week, int Users)>? weeks = null)
    {
        foreach (var (ago, users, active7) in days)
            await RunAsync("emberops", "INSERT INTO activity_days (day, users, active7) VALUES ($1, $2, $3)", Today.AddDays(-ago), users, active7);
        foreach (var (week, users) in weeks ?? [])
            await RunAsync("emberops", "INSERT INTO activity_weeks (week, users) VALUES ($1, $2)", week, users);
    }

    private async Task<JsonElement> PageAsync(string id)
    {
        using var client = _factory.ClientFrom(OpsApiFactory.HubIp);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {OpsApiFactory.LookoutKey}");
        return await client.GetFromJsonAsync<JsonElement>($"/lookout/v1/pages/{id}");
    }

    [Fact]
    public async Task A_snapshot_stores_the_distinct_users_seen_today_in_the_7_days_up_to_today_and_this_week()
    {
        await using var db = fixture.OpenOpsDatabases();

        await InsightQueries.SnapshotActivityAsync(db.Ember, db.Ops, fixture.Time, default);

        (await StoredDaysAsync()).Should().Equal("2026-11-18 1 2");
        (await StoredWeeksAsync()).Should().Equal("2026-11-16 2");
    }

    [Fact]
    public async Task A_later_snapshot_raises_the_stored_counts_and_a_lower_one_after_that_does_not_lower_them()
    {
        await using var db = fixture.OpenOpsDatabases();
        _extraSession = Guid.NewGuid();
        await InsightQueries.SnapshotActivityAsync(db.Ember, db.Ops, fixture.Time, default);
        var seen = Today.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);
        await RunAsync("emberdb", """INSERT INTO "AuthSessions" ("Id", "UserId", "CreatedUtc", "ExpiresUtc", "LastSeenUtc") VALUES ($1, $2, $3, $4, $3)""",
            _extraSession.Value, fixture.Data.UserIds[2], seen, seen.AddDays(60));

        await InsightQueries.SnapshotActivityAsync(db.Ember, db.Ops, fixture.Time, default);

        (await StoredDaysAsync()).Should().Equal("2026-11-18 2 3");
        (await StoredWeeksAsync()).Should().Equal("2026-11-16 3");

        await RunAsync("emberdb", """DELETE FROM "AuthSessions" WHERE "Id" = $1""", _extraSession.Value);
        await InsightQueries.SnapshotActivityAsync(db.Ember, db.Ops, fixture.Time, default);

        (await StoredDaysAsync()).Should().Equal("2026-11-18 2 3");
        (await StoredWeeksAsync()).Should().Equal("2026-11-16 3");
    }

    [Fact]
    public async Task A_snapshot_just_after_Berlin_midnight_is_filed_under_the_Berlin_day()
    {
        await using var db = fixture.OpenOpsDatabases();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 23, 30, 0, TimeSpan.Zero));

        await InsightQueries.SnapshotActivityAsync(db.Ember, db.Ops, time, default);

        (await StoredDaysAsync()).Should().Equal("2026-11-18 1 2");
        (await StoredWeeksAsync()).Should().Equal("2026-11-16 2");
    }

    [Fact]
    public async Task The_snapshot_tables_hold_counts_only_and_no_user_ids()
    {
        (await TextsAsync("emberops", "SELECT column_name FROM information_schema.columns WHERE table_name = 'activity_days' ORDER BY ordinal_position"))
            .Should().Equal("day", "users", "active7");
        (await TextsAsync("emberops", "SELECT column_name FROM information_schema.columns WHERE table_name = 'activity_weeks' ORDER BY ordinal_position"))
            .Should().Equal("week", "users");
    }

    [Fact]
    public async Task Activity_reads_90_zero_filled_days_and_12_weeks_when_the_history_is_longer()
    {
        await SeedAsync([(100, 4, 5), (40, 7, 9), (0, 5, 6)], [(new DateOnly(2026, 8, 10), 3), (new DateOnly(2026, 9, 7), 8), (new DateOnly(2026, 11, 16), 6)]);
        await using var db = fixture.OpenOpsDatabases();

        var a = await InsightQueries.ActivityAsync(db.Ops, fixture.Time, default);

        a.Since.Should().Be(Today.AddDays(-100));
        a.PerDay.Should().HaveCount(90);
        a.PerDay[0].Date.Should().Be(Today.AddDays(-89));
        a.PerDay[^1].Date.Should().Be(Today);
        a.PerDay.Where(d => d.Count > 0).Should().BeEquivalentTo([new DayCount(Today.AddDays(-40), 7), new DayCount(Today, 5)]);
        a.PerWeek.Should().HaveCount(12);
        a.PerWeek[0].Label.Should().Be("2026-08-31");
        a.PerWeek.Where(w => w.Count > 0).Should().Equal(new UserBucket("2026-09-07", 8, 8), new UserBucket("2026-11-16", 6, 6));
    }

    [Fact]
    public async Task Activity_starts_the_days_at_the_first_snapshot_and_the_weeks_at_its_Monday()
    {
        await SeedAsync([(10, 3, 3), (0, 4, 6)], [(new DateOnly(2026, 11, 9), 5), (new DateOnly(2026, 11, 16), 6)]);
        await using var db = fixture.OpenOpsDatabases();

        var a = await InsightQueries.ActivityAsync(db.Ops, fixture.Time, default);

        a.Since.Should().Be(new DateOnly(2026, 11, 8));
        a.PerDay.Should().HaveCount(11);
        a.PerDay.Select(d => d.Count).Should().Equal(3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4);
        a.PerWeek.Select(w => (w.Label, w.Count)).Should().Equal(("2026-11-02", 0), ("2026-11-09", 5), ("2026-11-16", 6));
    }

    [Fact]
    public async Task Activity_without_snapshots_has_no_start_and_empty_lists()
    {
        await using var db = fixture.OpenOpsDatabases();

        var a = await InsightQueries.ActivityAsync(db.Ops, fixture.Time, default);

        a.Since.Should().BeNull();
        a.PerDay.Should().BeEmpty();
        a.PerWeek.Should().BeEmpty();
    }

    [Fact]
    public async Task The_overview_series_lists_the_active_7_snapshots_of_the_last_14_days_and_the_one_of_7_days_ago()
    {
        await SeedAsync([(20, 9, 9), (13, 1, 1), (7, 2, 4), (3, 3, 5), (0, 4, 6)]);
        await using var db = fixture.OpenOpsDatabases();

        var series = await OverviewQueries.SeriesAsync(db.Ember, db.Ops, fixture.Time, default);

        series.Active7PerDay.Should().Equal(new DayCount(Today.AddDays(-13), 1), new DayCount(Today.AddDays(-7), 4), new DayCount(Today.AddDays(-3), 5), new DayCount(Today, 6));
        series.Active7Before.Should().Be(4);
        series.UsersBefore.Should().Be(1, "u4's first habit is 100 days old");
    }

    [Fact]
    public async Task The_overview_series_has_no_earlier_value_without_a_snapshot_7_days_ago()
    {
        await SeedAsync([(8, 1, 1), (0, 4, 6)]);
        await using var db = fixture.OpenOpsDatabases();

        var series = await OverviewQueries.SeriesAsync(db.Ember, db.Ops, fixture.Time, default);

        series.Active7Before.Should().BeNull();
        series.Active7PerDay.Select(d => d.Count).Should().Equal(1, 6);
    }

    [Fact]
    public async Task The_overview_tile_trends_the_snapshots_and_compares_the_live_count_with_7_days_ago()
    {
        await SeedAsync([(7, 2, 4), (3, 3, 5), (0, 4, 6)]);

        var tiles = LookoutShape.Widget(await PageAsync("overview"), "tiles");

        var active7 = LookoutShape.Item(tiles, "Active 7 days");
        active7.GetProperty("trend").EnumerateArray().Select(t => t.GetDouble()).Should().Equal(4, 5, 6);
        active7.GetProperty("delta").GetProperty("value").GetDouble().Should().Be(-2, "2 users now against 4 seven days ago");
    }

    [Fact]
    public async Task The_activity_page_reads_the_snapshots()
    {
        await SeedAsync(Enumerable.Range(9, 7).Select(d => (Ago: 18 - d, Users: d == 15 ? 5 : 4, Active7: 6)).Append((0, 5, 6)).Append((16, 3, 3)),
            [(new DateOnly(2026, 11, 2), 4), (new DateOnly(2026, 11, 9), 6), (new DateOnly(2026, 11, 16), 7)]);

        var page = await PageAsync("activity");

        LookoutShape.AssertPage(page, "activity");
        var tiles = LookoutShape.Widget(page, "tiles");
        LookoutShape.Item(tiles, "Active today").GetProperty("value").GetInt32().Should().Be(5);
        LookoutShape.Item(tiles, "Average per day").GetProperty("value").GetDouble().Should().Be(4.1);
        LookoutShape.Item(tiles, "Stickiness").GetProperty("value").GetDouble().Should().BeApproximately(4.1 / 6, 1e-9);
        tiles.GetProperty("note").GetString().Should().Contain("history starts 2026-11-02");
    }
}
