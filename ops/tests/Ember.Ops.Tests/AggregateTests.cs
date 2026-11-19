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
        var features = await EmberFeatures.DetectAsync(db.Ember, default);
        return Overview.From(
            await OverviewQueries.CountersAsync(db.Ember, features, fixture.Time, default),
            await OverviewQueries.SeriesAsync(db.Ember, features, fixture.Time, default));
    }

    [Fact]
    public async Task Counts_users_and_activity_where_active_means_a_completed_check_in_in_the_window()
    {
        var overview = await OverviewAsync();

        overview.Users.Total.Should().Be(6);
        overview.Users.Active7.Should().Be(2, "u3 completed 7 days ago and u4's only recent log is not completed");
        overview.Users.Active30.Should().Be(3, "u3 completed 29 days ago, u4 completed 30 days ago which is outside");
        overview.Streaks.Users.Should().Be(6);
        overview.Streaks.ShowStreaks.Should().Be(3);
        overview.Streaks.Grace.Should().BeEquivalentTo([new GraceCount(0, 3), new GraceCount(1, 2), new GraceCount(2, 1)], o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Counts_habits_by_kind_pause_and_reminder_and_routines()
    {
        var overview = await OverviewAsync();

        overview.Habits.Should().Be(new HabitCounts(Build: 6, LetGo: 2, Paused: 1, WithReminder: 3));
        overview.Routines.Should().Be(new RoutineCounts(Total: 3, Users: 2));
    }

    [Fact]
    public async Task Zero_fills_check_ins_per_day_over_90_days_and_counts_only_completed_logs()
    {
        var overview = await OverviewAsync();

        overview.CheckInsPerDay.Should().HaveCount(90);
        overview.CheckInsPerDay[0].Date.Should().Be(Today.AddDays(-89));
        overview.CheckInsPerDay[^1].Date.Should().Be(Today);
        overview.CheckInsPerDay.Where(d => d.Count > 0).Select(d => (Today.DayNumber - d.Date.DayNumber, d.Count))
            .Should().BeEquivalentTo([(30, 1), (29, 1), (7, 1), (6, 1), (3, 1), (0, 2)], o => o.WithoutStrictOrdering());
    }

    [Fact]
    public async Task Counts_new_users_per_week_of_their_first_habit_over_12_zero_filled_weeks()
    {
        var overview = await OverviewAsync();

        overview.Users.NewPerWeek.Should().HaveCount(12);
        overview.Users.NewPerWeek[0].Week.Should().Be(new DateOnly(2026, 8, 31));
        overview.Users.NewPerWeek[^1].Week.Should().Be(new DateOnly(2026, 11, 16));
        overview.Users.NewPerWeek.Where(w => w.Count > 0).Should().BeEquivalentTo(
            [new WeekCount(new(2026, 10, 26), 1), new WeekCount(new(2026, 11, 2), 2), new WeekCount(new(2026, 11, 16), 1)]);
    }

    [Fact]
    public async Task Counts_journal_days_and_low_days_per_day()
    {
        var overview = await OverviewAsync();

        overview.JournalPerDay.Should().HaveCount(90);
        overview.JournalPerDay.Where(d => d.Count > 0).Should().BeEquivalentTo([new DayCount(Today, 3), new DayCount(Today.AddDays(-1), 1)]);
        overview.LowDaysPerDay.Where(d => d.Count > 0).Should().BeEquivalentTo([new DayCount(Today, 2), new DayCount(Today.AddDays(-2), 1)]);
    }

    [Fact]
    public async Task Counts_fireside_friendships_as_half_the_link_rows_and_warmth_in_the_last_30_days()
    {
        var overview = await OverviewAsync();

        overview.Fireside.Should().NotBeNull();
        overview.Fireside!.UsersOn.Should().Be(3);
        overview.Fireside.Friendships.Should().Be(3);
        overview.Fireside.WarmthPerDay.Should().HaveCount(30);
        overview.Fireside.WarmthPerDay.Where(d => d.Count > 0).Should().BeEquivalentTo([new DayCount(Today, 2), new DayCount(Today.AddDays(-2), 1)]);
    }

    [Fact]
    public async Task Sums_let_go_slips_and_slip_days_per_week()
    {
        var overview = await OverviewAsync();

        overview.LetGoSlipsPerWeek.Should().HaveCount(12);
        overview.LetGoSlipsPerWeek!.Where(w => w.Slips > 0).Should().BeEquivalentTo(
            [new SlipWeek(new(2026, 11, 2), 3, 1), new SlipWeek(new(2026, 11, 9), 1, 1), new SlipWeek(new(2026, 11, 16), 3, 2)]);
    }

    [Fact]
    public async Task Counts_open_reminders_by_channel_for_the_last_14_days()
    {
        await using var db = fixture.OpenOpsDatabases();

        var reminders = await ReminderQueries.CountersAsync(db.Ember, fixture.Time, default);

        reminders.OpenPerDay.Should().HaveCount(14);
        reminders.OpenPerDay.Where(d => d.Discord > 0 || d.Push > 0).Should().BeEquivalentTo(
            [new ChannelDay(Today, 2, 1), new ChannelDay(Today.AddDays(-1), 1, 0), new ChannelDay(Today.AddDays(-5), 0, 2)]);
    }

    [Fact]
    public async Task Counts_discord_second_reminder_push_phone_and_overdue_state()
    {
        await using var db = fixture.OpenOpsDatabases();

        var reminders = await ReminderQueries.CountersAsync(db.Ember, fixture.Time, default);

        reminders.Discord.Should().Be(new DiscordCounts(RemindersOn: 2, Failing: 2, Paused: 1));
        reminders.SecondReminders.UsersOn.Should().Be(3);
        reminders.SecondReminders.ByMinutes.Should().BeEquivalentTo([new MinutesCount(15, 2), new MinutesCount(30, 1)], o => o.WithStrictOrdering());
        reminders.SecondReminders.PendingNow.Should().Be(2, "one habit and one routine have a second reminder scheduled");
        reminders.Push.Should().Be(new PushCounts(Subscriptions: 3, Enabled: 2));
        reminders.Phones.Should().Be(new PhoneCounts(Total: 2, Enabled: 1));
        reminders.Overdue.Should().Be(new OverdueCounts(Habits: 1, Routines: 1));
    }
}
