using Ember.Ops.Lookout;
using FluentAssertions;

namespace Ember.Ops.Tests;

public sealed class LookoutPagesTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 18);

    private static readonly DateOnly Monday = new(2026, 11, 16);

    /// <summary>History from <paramref name="since"/>; every week from its Monday to this one is listed, with 0 where <paramref name="weeks"/> has none.</summary>
    private static ActivityInsights Activity(DateOnly since, Dictionary<DateOnly, int> days, Dictionary<DateOnly, int> weeks)
    {
        var first = since > Today.AddDays(-89) ? since : Today.AddDays(-89);
        var mondays = Enumerable.Range(0, 12).Select(i => Monday.AddDays(-7 * (11 - i))).Where(w => w >= OpsCalendar.Monday(since));
        return new(Now, since, OpsCalendar.Days(first, Today, days),
            mondays.Select(w => new UserBucket(w.ToString("yyyy-MM-dd"), weeks.GetValueOrDefault(w), weeks.GetValueOrDefault(w))).ToList());
    }

    /// <summary>History from 2026-11-02; last full week 2026-11-09 to 11-15 averages 29 / 7 users a day and had 6 different users.</summary>
    private static ActivityInsights FullHistory(int today = 5, int lastWeekUsers = 6) => Activity(new(2026, 11, 2),
        new() { [new(2026, 11, 9)] = 4, [new(2026, 11, 10)] = 4, [new(2026, 11, 11)] = 4, [new(2026, 11, 12)] = 4, [new(2026, 11, 13)] = 4, [new(2026, 11, 14)] = 4, [new(2026, 11, 15)] = 5, [Today] = today },
        new() { [new(2026, 11, 2)] = 4, [new(2026, 11, 9)] = lastWeekUsers, [Monday] = 7 });

    private static StatsWidget Stats(Widget widget) => (StatsWidget)widget;

    private static Widget Widget(Page page, string id) => page.Widgets.Single(w => w.Id == id);

    [Fact]
    public void Activity_shows_today_the_last_full_weeks_average_and_stickiness_with_the_history_start()
    {
        var page = LookoutPages.Activity(FullHistory());

        var tiles = Stats(Widget(page, "tiles"));
        tiles.Items.Select(i => (i.Label, i.Value)).Should().Equal(("Active today", 5), ("Average per day", 4.1), ("Stickiness", 29d / 7 / 6));
        tiles.Items[0].Max.Should().BeNull("this week's distinct users could be 1 or 2");
        tiles.Items[2].Format.Should().Be("percent");
        tiles.Note.Should().Contain("history starts 2026-11-02");
        ((BarsWidget)Widget(page, "active-per-day")).Note.Should().Contain("history starts 2026-11-02");
        ((BarsWidget)Widget(page, "active-per-week")).Bars.Select(b => (b.Label, b.Values[0])).Should().Equal(("2026-11-02", 4), ("2026-11-09", 6), ("2026-11-16", 7));
        ((BarsWidget)Widget(page, "active-per-day")).Bars.Should().HaveCount(17, "from the first snapshot day to today");
    }

    [Fact]
    public void Without_snapshots_every_widget_is_unavailable_and_empty()
    {
        var page = LookoutPages.Activity(new(Now, null, [], []));

        page.Widgets.Select(w => (w.Id, w.Unavailable)).Should().Equal(("tiles", "No snapshots yet"), ("active-per-day", "No snapshots yet"), ("active-per-week", "No snapshots yet"));
        Stats(Widget(page, "tiles")).Items.Should().BeEmpty();
        ((BarsWidget)Widget(page, "active-per-day")).Bars.Should().BeEmpty();
        ((BarsWidget)Widget(page, "active-per-week")).Bars.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void A_figure_of_one_or_two_people_is_the_string_below_3_with_no_hint_trend_delta_max_or_goal(int people)
    {
        var page = LookoutPages.Activity(FullHistory(today: people, lastWeekUsers: people));

        Stats(Widget(page, "tiles")).Items.Should().Equal(
            new StatItem("Active today", LookoutPages.Suppressed), new StatItem("Average per day", LookoutPages.Suppressed), new StatItem("Stickiness", LookoutPages.Suppressed));
    }

    [Fact]
    public void Three_people_show_the_number_and_zero_is_not_suppressed()
    {
        Stats(Widget(LookoutPages.Activity(FullHistory(today: 3)), "tiles")).Items[0].Value.Should().Be(3);
        Stats(Widget(LookoutPages.Activity(FullHistory(today: 0)), "tiles")).Items[0].Value.Should().Be(0);
    }

    [Fact]
    public void Bars_with_one_or_two_people_are_left_out_while_empty_days_stay_and_the_note_says_so()
    {
        var a = Activity(new(2026, 11, 14),
            new() { [new(2026, 11, 14)] = 2, [new(2026, 11, 15)] = 3, [new(2026, 11, 17)] = 1, [Today] = 3 },
            new() { [new(2026, 11, 9)] = 2, [Monday] = 5 });

        var page = LookoutPages.Activity(a);

        var perDay = (BarsWidget)Widget(page, "active-per-day");
        perDay.Bars.Select(b => b.Label).Should().Equal("2026-11-15", "2026-11-16", "2026-11-18");
        perDay.Note.Should().Contain("1 or 2 people are left out");
        var perWeek = (BarsWidget)Widget(page, "active-per-week");
        perWeek.Bars.Select(b => b.Label).Should().Equal("2026-11-16");
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public void Average_and_stickiness_need_history_back_to_the_Monday_of_the_last_full_week(int sinceDay, bool covered)
    {
        var days = Enumerable.Range(9, 7).ToDictionary(d => new DateOnly(2026, 11, d), _ => 4);
        var page = LookoutPages.Activity(Activity(new(2026, 11, sinceDay), days, new() { [new(2026, 11, 9)] = 8, [Monday] = 6 }));

        var items = Stats(Widget(page, "tiles")).Items;

        items[1].Value.Should().Be(covered ? 4d : null);
        items[2].Value.Should().Be(covered ? 0.5 : null);
        items[1].Hint.Should().Be(covered ? "last full week" : "needs a full week of history");
    }

    [Fact]
    public void A_day_without_a_snapshot_is_left_out_and_leaves_the_week_average_and_today_unknown()
    {
        var days = Enumerable.Range(9, 7).Where(d => d != 12).Select(d => new DayCount(new(2026, 11, d), 4)).ToList();
        var page = LookoutPages.Activity(new(Now, new(2026, 11, 2), days, [new("2026-11-09", 6, 6), new("2026-11-16", 2, 2)]));

        Stats(Widget(page, "tiles")).Items.Should().HaveCount(3).And.OnlyContain(i => i.Value == null);
        ((BarsWidget)Widget(page, "active-per-day")).Bars.Should().HaveCount(6);
    }

    [Fact]
    public void An_empty_last_week_has_an_average_of_zero_and_no_stickiness_to_divide_by()
    {
        var page = LookoutPages.Activity(Activity(new(2026, 11, 2), new(), new()));

        Stats(Widget(page, "tiles")).Items.Select(i => i.Value).Should().Equal(0, 0d, null);
    }

    private static OverviewSeries Series(int? before, params (int Ago, int Count)[] days) =>
        new(Now, OpsCalendar.Weeks(Today, 12).Select((w, i) => new WeekCount(w, i == 11 ? 2 : 0)).ToList(), UsersBefore: 4,
            days.OrderByDescending(d => d.Ago).Select(d => new DayCount(Today.AddDays(-d.Ago), d.Count)).ToList(), before);

    private static readonly OverviewCounters Counters = new(Now, 6, 3, 4, new(6, 2, 1, 3), new(2, 1), new(6, 2, []));

    [Fact]
    public void The_summary_carries_the_v1_1_fields()
    {
        var feedback = new FeedbackList([
            new(1, "t", "x", "web", null, Now.AddDays(-2), "open", null, false, [], 0, 1),
            new(2, "t", "x", "web", null, Now.AddDays(-20), "open", null, false, [], 0, 2),
        ]);

        var tiles = LookoutPages.Summary(Counters,
            new(Now, [], new(0, 0, 0), new(0, [], 0), new(0, 0), new(0, 0), new(0, 0)), feedback, EmptySystem(), Now, Series(1, (7, 1), (0, 3))).Tiles;

        tiles[0].Trend.Should().HaveCount(12).And.EndWith(6);
        tiles[0].Delta.Should().Be(new Delta(2, "last week", "up"));
        tiles[1].Should().BeEquivalentTo(new { Max = 6d, Delta = new Delta(2, "the 7 days before", "up"), Trend = new[] { 1d, 3d } });
        tiles[2].Goal.Should().Be(new Goal(Below: 1));
        tiles[3].Should().BeEquivalentTo(new { Value = (object)2, Goal = new Goal(Below: 5), Delta = new Delta(1, "last week", "down") });
    }

    [Fact]
    public void The_active_7_tile_has_no_trend_below_two_points_and_no_delta_without_a_row_for_7_days_ago()
    {
        var tile = LookoutPages.Summary(Counters, null, null, EmptySystem(), Now, Series(null, (0, 3))).Tiles[1];

        tile.Value.Should().Be(3);
        tile.Trend.Should().BeNull();
        tile.Delta.Should().BeNull();
    }

    [Fact]
    public void The_overview_lists_users_active_7_and_14_days_without_check_in_journal_or_fireside_widgets()
    {
        var page = LookoutPages.Overview(Overview.From(Counters, Series(null)));

        page.Widgets.Select(w => w.Id).Should().Equal("tiles", "new-users", "grace");
        var tiles = Stats(Widget(page, "tiles")).Items;
        tiles.Select(i => i.Label).Should().Equal("Users", "Active 7 days", "Active 14 days", "Habits build / let go", "Routines", "Streak users");
        tiles[2].Should().Be(new StatItem("Active 14 days", 4, Hint: "67% of users", Max: 6));
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
        Stats(Widget(LookoutPages.FeedbackTrends(new([list.Items[2], list.Items[3]]), Now), "tiles")).Items[3].Value.Should().Be(20d, "the median of two is their mean");
    }

    private static SystemReport EmptySystem() =>
        new(Now, Now, [], null, [], null, null, [], null, null, null, new Dictionary<string, string>());
}
