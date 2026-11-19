using Ember.Ops.Lookout;
using FluentAssertions;

namespace Ember.Ops.Tests;

public sealed class LookoutPagesTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 18);

    private static ActivityInsights Activity(int today, int writers, int privateJournals, int weekUsers) =>
        new(Now, OpsCalendar.Days(Today.AddDays(-89), Today, new Dictionary<DateOnly, int> { [Today] = today }),
            [new("2026-11-09", 5, 5), new("2026-11-16", 2, 2)], [new("1 day", 4, 4), new("2–4 days", 1, 1)],
            [new(new(2026, 11, 2), 4, 3, 0)],
            [new("2026-11-09", 9, 4), new("2026-11-16", 6, weekUsers)], 10, writers, privateJournals,
            [new("2026-11-09", 3, 3), new("2026-11-16", 2, 1)]);

    private static StatsWidget Stats(Widget widget) => (StatsWidget)widget;

    private static Widget Widget(Page page, string id) => page.Widgets.Single(w => w.Id == id);

    [Fact]
    public void A_suppressed_stat_is_the_string_below_3_with_no_hint_trend_delta_max_or_goal()
    {
        var page = LookoutPages.Activity(Activity(today: 2, writers: 2, privateJournals: 1, weekUsers: 2));

        var tiles = Stats(Widget(page, "tiles")).Items;
        tiles[0].Should().Be(new StatItem("Active today", LookoutPages.Suppressed));
        var journal = Stats(((PanelWidget)Widget(page, "journal-activity")).Children[0]).Items;
        journal.Should().OnlyContain(i => i.Value as string == LookoutPages.Suppressed && i.Hint == null && i.Trend == null && i.Delta == null && i.Max == null);
    }

    [Fact]
    public void Three_or_more_people_show_the_number_and_zero_is_not_suppressed()
    {
        var page = LookoutPages.Activity(Activity(today: 3, writers: 3, privateJournals: 0, weekUsers: 3));

        var tiles = Stats(Widget(page, "tiles")).Items;
        tiles[0].Value.Should().Be(3);
        tiles[0].Max.Should().Be(5);
        Stats(((PanelWidget)Widget(page, "journal-activity")).Children[0]).Items.Select(i => i.Value).Should().Equal(3, 10, 0);
    }

    [Fact]
    public void Bars_with_one_or_two_people_are_left_out_and_the_note_says_so()
    {
        var page = LookoutPages.Activity(Activity(today: 1, writers: 3, privateJournals: 3, weekUsers: 2));

        var perWeek = (BarsWidget)Widget(page, "active-per-week");
        perWeek.Bars.Select(b => b.Label).Should().Equal("2026-11-09");
        perWeek.Note.Should().Contain("1 or 2 people are left out");
        ((BarsWidget)Widget(page, "active-days")).Bars.Select(b => b.Label).Should().Equal("1 day");
        ((BarsWidget)Widget(page, "active-per-day")).Bars.Should().HaveCount(89).And.OnlyContain(b => b.Values[0] == 0);
        ((BarsWidget)Widget(page, "low-days-week")).Bars.Select(b => b.Label).Should().Equal("2026-11-09");
        var journalWeeks = (BarsWidget)((PanelWidget)Widget(page, "journal-activity")).Children[1];
        journalWeeks.Bars.Select(b => b.Label).Should().Equal("2026-11-09");
    }

    [Fact]
    public void A_cohort_with_any_series_of_one_or_two_people_is_left_out()
    {
        var a = Activity(today: 0, writers: 0, privateJournals: 0, weekUsers: 0) with { Cohorts = [new(new(2026, 11, 2), 4, 3, 0), new(new(2026, 10, 26), 5, 2, 0)] };

        var cohorts = (BarsWidget)((PanelWidget)Widget(LookoutPages.Activity(a), "retention")).Children[1];

        cohorts.Bars.Select(b => (b.Label, b.Values.ToArray())).Should().BeEquivalentTo([("2026-11-02", new double[] { 4, 3, 0 })]);
    }

    [Fact]
    public void An_empty_activity_page_has_nulls_and_zeros_but_no_suppression()
    {
        var page = LookoutPages.Activity(new(Now, OpsCalendar.Days(Today.AddDays(-89), Today, new Dictionary<DateOnly, int>()), [], [], [], [], 0, 0, 0, []));

        Stats(Widget(page, "tiles")).Items.Select(i => i.Value).Should().Equal(0, null, null);
    }

    [Fact]
    public void The_summary_carries_the_v1_1_fields()
    {
        var series = new OverviewSeries(Now, OpsCalendar.Weeks(Today, 12).Select((w, i) => new WeekCount(w, i == 11 ? 2 : 0)).ToList(), [], [], [], null, null,
            UsersBefore: 4, OpsCalendar.Days(Today.AddDays(-13), Today, new Dictionary<DateOnly, int> { [Today] = 3 }), Active7Before: 1, Active30Before: 0);
        var feedback = new FeedbackList([
            new(1, "t", "x", "web", null, Now.AddDays(-2), "open", null, false, [], 0, 1),
            new(2, "t", "x", "web", null, Now.AddDays(-20), "open", null, false, [], 0, 2),
        ]);

        var tiles = LookoutPages.Summary(new(Now, 6, 3, 4, new(6, 2, 1, 3), new(2, 1), new(6, 2, []), null, null),
            new(Now, [], new(0, 0, 0), new(0, [], 0), new(0, 0), new(0, 0), new(0, 0)), feedback, EmptySystem(), Now, series).Tiles;

        tiles[0].Trend.Should().HaveCount(12).And.EndWith(6);
        tiles[0].Delta.Should().Be(new Delta(2, "last week", "up"));
        tiles[1].Should().BeEquivalentTo(new { Max = 6d, Delta = new Delta(2, "the 7 days before", "up") });
        tiles[1].Trend.Should().HaveCount(14);
        tiles[2].Goal.Should().Be(new Goal(Below: 1));
        tiles[3].Should().BeEquivalentTo(new { Value = (object)2, Goal = new Goal(Below: 5), Delta = new Delta(1, "last week", "down") });
    }

    [Fact]
    public void Disks_carry_their_size_as_max_and_ten_percent_as_goal()
    {
        var gb = 1024L * 1024 * 1024;
        var system = EmptySystem() with { Disks = [new("/", 30 * gb, 100 * gb)] };

        var disk = Stats(Widget(LookoutPages.System(system), "disks")).Items.Single();

        (disk.Max, disk.Goal).Should().Be((100d * gb, new Goal(Above: 10d * gb)));
    }

    [Fact]
    public void Feedback_trends_count_by_week_platform_status_and_version()
    {
        FeedbackItem Item(int id, string platform, string? version, int daysAgo, string status, double? resolvedHours = null) =>
            new(id, "t", "x", platform, version, Now.AddDays(-daysAgo), status, resolvedHours is { } h ? Now.AddDays(-daysAgo).AddHours(h) : null, false, [], 0, id);
        var list = new FeedbackList([
            Item(1, "web", "1.2.0", 1, "open"),
            Item(2, "android", "1.2.0", 3, "planned"),
            Item(3, "discord", null, 9, "released", resolvedHours: 10),
            Item(4, "web", "1.1.0", 10, "declined", resolvedHours: 30),
            Item(5, "web", "1.1.0", 40, "released", resolvedHours: 50),
        ]);

        var page = LookoutPages.FeedbackTrends(list, Now);

        Stats(Widget(page, "tiles")).Items.Should().BeEquivalentTo(new object[]
        {
            new { Label = "Open", Value = (object)1, Goal = new Goal(Below: 5) },
            new { Label = "New, 7 days", Value = (object)2, Delta = new Delta(0, "the 7 days before", "down") },
            new { Label = "Resolved, 30 days", Value = (object)2 },
            new { Label = "Median time to resolve", Value = (object)30d, Format = "hours" },
        }, o => o.WithStrictOrdering());
        var perWeek = (BarsWidget)Widget(page, "per-week");
        perWeek.Series.Select(s => s.Label).Should().Equal("Web", "Android", "Discord", "Other");
        perWeek.Bars.Should().HaveCount(12);
        perWeek.Bars[^1].Values.Should().Equal(1, 0, 0, 0);
        perWeek.Bars[^2].Values.Should().Equal(0, 1, 1, 0);
        ((BarsWidget)Widget(page, "by-status")).Bars.Select(b => (b.Label, b.Values.Sum())).Should()
            .Equal(("Open", 1), ("Planned", 1), ("Implemented", 0), ("Released", 2), ("Declined", 1));
        ((BarsWidget)Widget(page, "versions")).Bars.Select(b => (b.Label, b.Values[0])).Should().Equal(("1.2.0", 2), ("1.1.0", 2), ("Unknown", 1));
        ((BarsWidget)Widget(page, "flow")).Bars[^2].Values.Should().Equal(2, 2);
    }

    private static SystemReport EmptySystem() =>
        new(Now, Now, [], null, [], null, null, [], null, null, null, new Dictionary<string, string>());
}
