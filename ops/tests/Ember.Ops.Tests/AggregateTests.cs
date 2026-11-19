using FluentAssertions;

namespace Ember.Ops.Tests;

/// <summary>Every number is counted by hand from <see cref="Dataset"/>; today is 2026-11-18.</summary>
[Collection(V2Collection.Name)]
public sealed class AggregateTests(V2DatabaseFixture fixture)
{
    private static readonly DateOnly Today = DatabaseFixture.Today;

    private async Task<Overview> OverviewAsync()
    {
        await using var db = fixture.OpenOpsDatabases();
        await OpsMigrator.ApplyAsync(db.Ops);
        return Overview.From(
            await OverviewQueries.CountersAsync(db.Ember, fixture.Time, default),
            await OverviewQueries.SeriesAsync(db.Ember, db.Ops, await EmberFeatures.DetectAsync(db.Ember, default), fixture.Time, default));
    }

    [Fact]
    public async Task Counts_users_and_distinct_sign_in_users_in_the_7_and_14_day_windows_from_Berlin_midnight()
    {
        var overview = await OverviewAsync();

        overview.Users.Total.Should().Be(6);
        overview.Users.Active7.Should().Be(2, "u1 has two sessions and counts once, u2 was created at the 7-day edge; u3 was last seen a minute before it");
        overview.Users.Active14.Should().Be(4, "u3 and u4 sit inside the 14-day edge, u5 a minute before it, u6 has no session");
    }

    [Fact]
    public async Task Without_account_creation_counts_new_users_per_week_of_their_first_habit_over_12_zero_filled_weeks()
    {
        var overview = await OverviewAsync();

        overview.Series.SignupNote.Should().Be("by first habit, a proxy until Ember records account creation");
        overview.Users.NewPerWeek.Should().HaveCount(12);
        overview.Users.NewPerWeek[0].Week.Should().Be(new DateOnly(2026, 8, 31));
        overview.Users.NewPerWeek[^1].Week.Should().Be(new DateOnly(2026, 11, 16));
        overview.Users.NewPerWeek.Where(w => w.Count > 0).Should().BeEquivalentTo(
            [new WeekCount(new(2026, 10, 26), 1), new WeekCount(new(2026, 11, 2), 2), new WeekCount(new(2026, 11, 16), 1)]);
    }

    [Fact]
    public async Task Counts_habits_and_routines_whose_next_reminder_is_more_than_15_minutes_overdue()
    {
        await using var db = fixture.OpenOpsDatabases();

        (await ReminderQueries.OverdueAsync(db.Ember, default)).Should().Be(2, "h2 and the second routine are an hour or more late; the rest are due later or have none");
    }

    [Fact]
    public async Task Counts_web_push_users_android_installs_and_discord_links_and_reads_no_platforms_or_versions_without_the_columns()
    {
        await using var db = fixture.OpenOpsDatabases();

        var devices = await InsightQueries.DevicesAsync(db.Ember, new EmberFeatures(UserCreated: false, SessionPlatform: false, SessionAppVersion: false), fixture.Time, default);

        (devices.Users, devices.WebPush, devices.AndroidInstalls, devices.DiscordLinked).Should().Be((6, 2, 2, 2));
        devices.Platforms.Should().BeNull();
        devices.Versions.Should().BeNull();
    }
}
