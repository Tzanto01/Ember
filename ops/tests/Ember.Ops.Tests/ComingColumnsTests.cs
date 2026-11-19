using System.Net.Http.Json;
using System.Text.Json;
using Ember.Ops.Lookout;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Ember.Ops.Tests;

/// <summary>
/// The paths that use <c>AspNetUsers."CreatedUtc"</c>, <c>AuthSessions."Platform"</c> and <c>"AppVersion"</c> once Ember has them;
/// every number is counted by hand from <see cref="ComingColumnsDatabaseFixture"/>. Today is Wednesday 2026-11-18.
/// </summary>
[Collection(ComingColumnsCollection.Name)]
public sealed class ComingColumnsTests(ComingColumnsDatabaseFixture fixture) : IDisposable
{
    private readonly OpsApiFactory _factory = new(fixture, hubAddress: OpsApiFactory.HubIp, lookoutKey: OpsApiFactory.LookoutKey);

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> PageAsync(string id)
    {
        using var client = _factory.ClientFrom(OpsApiFactory.HubIp);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {OpsApiFactory.LookoutKey}");
        return await client.GetFromJsonAsync<JsonElement>($"/lookout/v1/pages/{id}");
    }

    [Fact]
    public async Task The_grant_script_grants_the_new_columns_and_the_role_sees_them()
    {
        await using var connection = fixture.DashboardConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT u."CreatedUtc", s."Platform", s."AppVersion" FROM "AspNetUsers" u, "AuthSessions" s""", connection);
        var read = async () => { await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) { } };

        await read.Should().NotThrowAsync();
        await using var db = fixture.OpenOpsDatabases();
        (await EmberFeatures.DetectAsync(db.Ember, default)).Should().Be(new EmberFeatures(UserCreated: true, SessionPlatform: true, SessionAppVersion: true));
    }

    [Fact]
    public async Task New_users_are_counted_by_account_creation_and_an_unknown_one_counts_as_before_the_12_weeks()
    {
        var page = await PageAsync("overview");

        var tiles = LookoutShape.Widget(page, "tiles");
        LookoutShape.Item(tiles, "Users").GetProperty("trend").EnumerateArray().Select(t => t.GetDouble()).Should().Equal(1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 5, 6);
        LookoutShape.Item(tiles, "New users this week").GetProperty("value").GetInt32().Should().Be(1, "u6 was created yesterday");
        LookoutShape.Item(tiles, "Active 7 days").GetProperty("value").GetInt32().Should().Be(6);
        LookoutShape.Widget(page, "new-users").GetProperty("note").GetString().Should().Be("by account creation");
    }

    [Fact]
    public async Task Devices_count_platforms_and_versions_of_the_last_7_days_and_leave_out_one_or_two_people()
    {
        var page = await PageAsync("devices");

        LookoutShape.AssertPage(page, "devices");
        Bars(LookoutShape.Widget(page, "platforms")).Should().Equal(("Android", 3d));
        Bars(LookoutShape.Widget(page, "versions")).Should().Equal(("1.4.0", 3d));
    }

    [Fact]
    public async Task Platforms_map_web_android_and_discord_and_count_anything_else_as_other()
    {
        await using var db = fixture.OpenOpsDatabases();

        var devices = await InsightQueries.DevicesAsync(db.Ember, await EmberFeatures.DetectAsync(db.Ember, default), fixture.Time, default);

        devices.Platforms!.Select(p => (p.Label, p.Users)).Should().Equal(
            ("Android", 3), ("Discord bot", 1), ("Not recorded", 1), ("Other", 1), ("Web", 1));
    }

    [Fact]
    public async Task Retention_cohorts_follow_account_creation()
    {
        await using var db = fixture.OpenOpsDatabases();
        await OpsMigrator.ApplyAsync(db.Ops);
        await InsightQueries.SnapshotActivityAsync(db.Ember, db.Ops, fixture.Time, default);
        var nextWeek = new FakeTimeProvider(fixture.Time.GetUtcNow().AddDays(7));

        var activity = await InsightQueries.ActivityAsync(db.Ops, nextWeek, default);
        var series = await OverviewQueries.SeriesAsync(db.Ember, db.Ops, await EmberFeatures.DetectAsync(db.Ember, default), nextWeek, default);

        activity.Retained.Should().Equal(
            new RetainedCount(new(2026, 10, 19), new(2026, 11, 16), 0), new RetainedCount(new(2026, 11, 2), new(2026, 11, 16), 1),
            new RetainedCount(new(2026, 11, 9), new(2026, 11, 16), 3));
        var retention = (StatsWidget)LookoutPages.Engagement(activity, series).Widgets.Single(w => w.Id == "retention");
        retention.Items.Should().Equal(
            new StatItem("After 1 week", 1d, "percent", Hint: "3 of 3 new users", Max: 1),
            new StatItem("After 2 weeks", LookoutPages.Suppressed),
            new StatItem("After 4 weeks", null, "percent", Hint: "no finished week yet", Max: 1));
        retention.Note.Should().Contain("by account creation");
    }

    private static List<(string, double)> Bars(JsonElement widget) =>
        widget.GetProperty("bars").EnumerateArray().Select(b => (b.GetProperty("label").GetString()!, b.GetProperty("values")[0].GetDouble())).ToList();
}
