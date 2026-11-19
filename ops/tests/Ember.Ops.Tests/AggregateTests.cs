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
        var features = await EmberFeatures.DetectAsync(db.Ember, default);
        return Overview.From(
            await OverviewQueries.CountersAsync(db.Ember, features, fixture.Time, default),
            await OverviewQueries.SeriesAsync(db.Ember, db.Ops, fixture.Time, default));
    }

    [Fact]
    public async Task Counts_users_and_distinct_sign_in_users_in_the_7_and_14_day_windows_from_Berlin_midnight()
    {
        var overview = await OverviewAsync();

        overview.Users.Total.Should().Be(6);
        overview.Users.Active7.Should().Be(2, "u1 has two sessions and counts once, u2 was created at the 7-day edge; u3 was last seen a minute before it");
        overview.Users.Active14.Should().Be(4, "u3 and u4 sit inside the 14-day edge, u5 a minute before it, u6 has no session");
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
