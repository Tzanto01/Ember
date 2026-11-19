using Ember.Ops.Lookout;
using FluentAssertions;

namespace Ember.Ops.Tests;

public sealed class LookoutPagesTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 18);

    private static readonly DateOnly Monday = new(2026, 11, 16);

    /// <summary>History from <paramref name="since"/>; every week from its Monday to this one is listed, with 0 where <paramref name="weeks"/> has none.</summary>
    private static ActivityInsights Activity(DateOnly since, Dictionary<DateOnly, int> days, Dictionary<DateOnly, int> weeks, IReadOnlyList<RetainedCount>? retained = null)
    {
        var first = since > Today.AddDays(-89) ? since : Today.AddDays(-89);
        var mondays = Enumerable.Range(0, 12).Select(i => Monday.AddDays(-7 * (11 - i))).Where(w => w >= OpsCalendar.Monday(since));
        return new(Now, since, OpsCalendar.Days(first, Today, days),
            mondays.Select(w => new UserBucket(w.ToString("yyyy-MM-dd"), weeks.GetValueOrDefault(w), weeks.GetValueOrDefault(w))).ToList(), retained ?? []);
    }

    /// <summary>History from 2026-11-02; last full week 2026-11-09 to 11-15 averages 29 / 7 users a day and had 6 different users.</summary>
    private static ActivityInsights FullHistory(int today = 5, int lastWeekUsers = 6, IReadOnlyList<RetainedCount>? retained = null) => Activity(new(2026, 11, 2),
        new() { [new(2026, 11, 9)] = 4, [new(2026, 11, 10)] = 4, [new(2026, 11, 11)] = 4, [new(2026, 11, 12)] = 4, [new(2026, 11, 13)] = 4, [new(2026, 11, 14)] = 4, [new(2026, 11, 15)] = 5, [Today] = today },
        new() { [new(2026, 11, 2)] = 4, [new(2026, 11, 9)] = lastWeekUsers, [Monday] = 7 }, retained);

    private static Page Engagement(ActivityInsights a, OverviewSeries? series = null) => LookoutPages.Engagement(a, series ?? Series(null));

    private static StatsWidget Stats(Widget widget) => (StatsWidget)widget;

    private static Widget Widget(Page page, string id) => page.Widgets.Single(w => w.Id == id);

    [Fact]
    public void Engagement_shows_today_the_last_full_weeks_average_and_stickiness_with_the_history_start()
    {
        var page = Engagement(FullHistory());

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
        var page = Engagement(new(Now, null, [], [], []));

        page.Widgets.Where(w => w.Id != "days-active").Select(w => (w.Id, w.Unavailable)).Should().Equal(
            ("tiles", "No snapshots yet"), ("active-per-day", "No snapshots yet"), ("active-per-week", "No snapshots yet"), ("retention", "No snapshots yet"));
        Stats(Widget(page, "tiles")).Items.Should().BeEmpty();
        ((BarsWidget)Widget(page, "active-per-day")).Bars.Should().BeEmpty();
        ((BarsWidget)Widget(page, "active-per-week")).Bars.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public void A_figure_of_one_or_two_people_is_the_string_below_3_with_no_hint_trend_delta_max_or_goal(int people)
    {
        var page = Engagement(FullHistory(today: people, lastWeekUsers: people));

        Stats(Widget(page, "tiles")).Items.Should().Equal(
            new StatItem("Active today", LookoutPages.Suppressed), new StatItem("Average per day", LookoutPages.Suppressed), new StatItem("Stickiness", LookoutPages.Suppressed));
    }

    [Fact]
    public void Three_people_show_the_number_and_zero_is_not_suppressed()
    {
        Stats(Widget(Engagement(FullHistory(today: 3)), "tiles")).Items[0].Value.Should().Be(3);
        Stats(Widget(Engagement(FullHistory(today: 0)), "tiles")).Items[0].Value.Should().Be(0);
    }

    [Fact]
    public void Bars_with_one_or_two_people_are_left_out_while_empty_days_stay_and_the_note_says_so()
    {
        var a = Activity(new(2026, 11, 14),
            new() { [new(2026, 11, 14)] = 2, [new(2026, 11, 15)] = 3, [new(2026, 11, 17)] = 1, [Today] = 3 },
            new() { [new(2026, 11, 9)] = 2, [Monday] = 5 });

        var page = Engagement(a);

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
        var page = Engagement(Activity(new(2026, 11, sinceDay), days, new() { [new(2026, 11, 9)] = 8, [Monday] = 6 }));

        var items = Stats(Widget(page, "tiles")).Items;

        items[1].Value.Should().Be(covered ? 4d : null);
        items[2].Value.Should().Be(covered ? 0.5 : null);
        items[1].Hint.Should().Be(covered ? "last full week" : "needs a full week of history");
    }

    [Fact]
    public void A_day_without_a_snapshot_is_left_out_and_leaves_the_week_average_and_today_unknown()
    {
        var days = Enumerable.Range(9, 7).Where(d => d != 12).Select(d => new DayCount(new(2026, 11, d), 4)).ToList();
        var page = Engagement(new(Now, new(2026, 11, 2), days, [new("2026-11-09", 6, 6), new("2026-11-16", 2, 2)], []));

        Stats(Widget(page, "tiles")).Items.Should().HaveCount(3).And.OnlyContain(i => i.Value == null);
        ((BarsWidget)Widget(page, "active-per-day")).Bars.Should().HaveCount(6);
    }

    [Fact]
    public void An_empty_last_week_has_an_average_of_zero_and_no_stickiness_to_divide_by()
    {
        var page = Engagement(Activity(new(2026, 11, 2), new(), new()));

        Stats(Widget(page, "tiles")).Items.Select(i => i.Value).Should().Equal(0, 0d, null);
    }

    private static OverviewSeries Series(int? before, params (int Ago, int Count)[] days) =>
        new(Now, OpsCalendar.Weeks(Today, 12).Select((w, i) => new WeekCount(w, i == 11 ? 2 : 0)).ToList(), UsersBefore: 4,
            days.OrderByDescending(d => d.Ago).Select(d => new DayCount(Today.AddDays(-d.Ago), d.Count)).ToList(), before, "by account creation");

    private static readonly OverviewCounters Counters = new(Now, 6, 3, 4);

    [Fact]
    public void The_summary_carries_the_v1_1_fields()
    {
        var feedback = new FeedbackList([
            new(1, "t", "x", "web", null, Now.AddDays(-2), "open", null, false, [], 0, 1),
            new(2, "t", "x", "web", null, Now.AddDays(-20), "open", null, false, [], 0, 2),
        ]);

        var api = new ApiHealth(Now, null, [], new(Now.AddHours(-23), 23 * 60, new(ApiTimings.AllRoutes, 40, 0, 80, 412.4)), []);
        var tiles = LookoutPages.Summary(Counters, feedback, EmptySystem(), Now, Series(1, (7, 1), (0, 3)), api).Tiles;

        tiles[0].Trend.Should().HaveCount(12).And.EndWith(6);
        tiles[0].Delta.Should().Be(new Delta(2, "last week", "up"));
        tiles[1].Should().BeEquivalentTo(new { Max = 6d, Delta = new Delta(2, "the 7 days before", "up"), Trend = new[] { 1d, 3d } });
        tiles[2].Should().Be(new StatItem("API p95", 412d, Hint: "ms, last 24 hours", Goal: new(Below: 300)));
        LookoutPages.Summary(Counters, feedback, EmptySystem(), Now, api: new(Now, ApiTimings.NotReporting, [], null, [])).Tiles[2]
            .Should().Be(new StatItem("API p95", null, Hint: ApiTimings.NotReporting, Goal: new(Below: 300)));
        tiles[3].Should().BeEquivalentTo(new { Value = (object)2, Goal = new Goal(Below: 5), Delta = (Delta?)null });
    }

    [Fact]
    public void The_active_7_tile_has_no_trend_below_two_points_and_no_delta_without_a_row_for_7_days_ago()
    {
        var tile = LookoutPages.Summary(Counters, null, EmptySystem(), Now, Series(null, (0, 3))).Tiles[1];

        tile.Value.Should().Be(3);
        tile.Trend.Should().BeNull();
        tile.Delta.Should().BeNull();
    }

    [Fact]
    public void The_overview_lists_users_new_users_and_activity_and_no_in_app_figures()
    {
        var page = LookoutPages.Overview(Overview.From(Counters, Series(null)), FullHistory());

        page.Widgets.Select(w => w.Id).Should().Equal("tiles", "new-users", "active-per-day");
        var tiles = Stats(Widget(page, "tiles")).Items;
        tiles.Select(i => i.Label).Should().Equal("Users", "New users this week", "Active today", "Active 7 days", "Active 14 days", "Stickiness");
        tiles[1].Should().Be(new StatItem("New users this week", 2, Hint: "by account creation"));
        tiles[2].Should().Be(new StatItem("Active today", 5, Max: 6));
        tiles[4].Should().Be(new StatItem("Active 14 days", 4, Hint: "67% of users", Max: 6));
        ((BarsWidget)Widget(page, "new-users")).Note.Should().Be("by account creation");
        Stats(Widget(LookoutPages.Overview(Overview.From(Counters, Series(null)), FullHistory(today: 2)), "tiles")).Items[2]
            .Should().Be(new StatItem("Active today", LookoutPages.Suppressed));
    }

    /// <summary>New users per week: 4 in the week of 2026-10-26, 2 in 11-02, 5 in 11-09; this week is 2026-11-16.</summary>
    private static OverviewSeries Cohorts() =>
        new(Now, OpsCalendar.Weeks(Today, 12).Select(w => new WeekCount(w, w.Day switch { 26 => 4, 2 => 2, 9 => 5, _ => 0 })).ToList(), 0, [], null, "by account creation");

    [Fact]
    public void Retention_pools_the_finished_weeks_leaves_out_this_one_and_suppresses_one_or_two_returning_users()
    {
        RetainedCount R(int cohortMonth, int cohortDay, int weeksLater, int users)
        {
            var cohort = new DateOnly(2026, cohortMonth, cohortDay);
            return new(cohort, cohort.AddDays(7 * weeksLater), users);
        }
        var retained = new[] { R(10, 26, 1, 3), R(11, 2, 1, 2), R(11, 9, 1, 4), R(10, 26, 2, 1), R(11, 2, 2, 1) };

        var widget = Stats(Widget(Engagement(FullHistory(retained: retained), Cohorts()), "retention"));

        widget.Items.Should().Equal(
            new StatItem("After 1 week", 5d / 6, "percent", Hint: "5 of 6 new users", Max: 1),
            new StatItem("After 2 weeks", LookoutPages.Suppressed),
            new StatItem("After 4 weeks", null, "percent", Hint: "no finished week yet", Max: 1));
        widget.Note.Should().Contain("by account creation").And.Contain("history starts 2026-11-02");
    }

    [Fact]
    public void Retention_without_a_finished_week_has_no_value_and_without_snapshots_is_unavailable()
    {
        var thisWeek = Stats(Widget(Engagement(FullHistory(retained: [new(new(2026, 11, 9), Monday, 4)]), Cohorts()), "retention"));
        var none = Stats(Widget(Engagement(FullHistory(), Cohorts()), "retention"));

        thisWeek.Items.Should().OnlyContain(i => i.Value == null && i.Hint == "no finished week yet");
        none.Unavailable.Should().Be("No snapshots yet");
    }

    [Fact]
    public void Days_active_per_user_is_unavailable_with_the_reason()
    {
        Widget(Engagement(FullHistory()), "days-active").Unavailable.Should().Contain("without storing user ids");
    }

    [Fact]
    public void Devices_leave_out_buckets_of_one_or_two_people_and_mark_missing_columns_unavailable()
    {
        var page = LookoutPages.Devices(new(Now, 10, 4, 3, 5,
            [new("Web", 6, 6), new("Android", 3, 3), new("Discord bot", 2, 2), new("Not recorded", 1, 1)],
            [new("1.4.0", 5, 5), new("1.3.2", 2, 2), new("Not recorded", 0, 0)]));

        ((BarsWidget)Widget(page, "platforms")).Bars.Select(b => (b.Label, b.Values[0])).Should().Equal(("Web", 6), ("Android", 3));
        ((BarsWidget)Widget(page, "platforms")).Note.Should().Contain("1 or 2 people are left out");
        ((BarsWidget)Widget(page, "versions")).Bars.Select(b => b.Label).Should().Equal("1.4.0", "Not recorded");
        Stats(Widget(page, "adoption")).Items.Should().Equal(
            new StatItem("Web push on", 4, Hint: "40% of users", Max: 10),
            new StatItem("Android app installs", 3, Hint: "phones registered with the app"),
            new StatItem("Discord linked", 5, Hint: "50% of users", Max: 10));

        var missing = LookoutPages.Devices(new(Now, 10, 4, 3, 5, null, null));
        missing.Widgets.Take(2).Select(w => w.Unavailable).Should().Equal("Ember doesn't record the sign-in platform yet", "Ember doesn't record the app version on sign-in yet");
    }

    [Fact]
    public void Releases_carry_the_deployed_shas_the_apk_and_ci_and_the_system_page_no_longer_does()
    {
        var system = EmptySystem() with { Deployed = new("abc1234", "def5678"), Apk = new("1.4.0", 14) };

        var releases = LookoutPages.Releases(system);

        releases.Widgets.Select(w => w.Id).Should().Equal("deployed", "apk", "ci");
        Stats(Widget(releases, "deployed")).Items.Select(i => i.Value).Should().Equal("abc1234", "def5678", null);
        LookoutPages.System(system).Widgets.Select(w => w.Id).Should().Equal("services", "database", "disks", "backup", "outbox");
    }

    [Fact]
    public void Disks_carry_their_size_as_max_and_ten_percent_as_goal()
    {
        var gb = 1024L * 1024 * 1024;
        var system = EmptySystem() with { Disks = [new("/", 30 * gb, 100 * gb)] };

        var disk = Stats(Widget(LookoutPages.System(system), "disks")).Items.Single();

        (disk.Max, disk.Goal).Should().Be((100d * gb, new Goal(Above: 10d * gb)));
    }

    private static SystemReport EmptySystem() =>
        new(Now, Now, [], null, [], null, null, [], null, null, null, new Dictionary<string, string>());
}
