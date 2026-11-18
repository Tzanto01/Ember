using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Ember.Ops.Tests;

[Collection(V2Collection.Name)]
public sealed class ReminderHistoryTests(V2DatabaseFixture fixture)
{
    [Fact]
    public async Task Counts_reminders_sent_between_snapshots_even_when_the_rows_were_deleted_afterwards()
    {
        await using var db = fixture.OpenOpsDatabases();
        await OpsMigrator.ApplyAsync(db.Ops);
        await Superuser("TRUNCATE reminder_counters", "emberops");
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 9, 0, 0, TimeSpan.Zero));
        var firstAt = time.GetUtcNow().UtcDateTime;
        var user = fixture.Data.UserIds[0];
        var habit = fixture.Data.HabitIds[0];

        (await ReminderQueries.SnapshotAsync(db.Ember, db.Ops, time, default)).Should().BeTrue("the first snapshot is always stored");
        await Superuser($"""
            INSERT INTO "ReminderMessages" ("UserId", "HabitId", "Date", "ChannelId", "MessageId")
                SELECT '{user}', {habit}, DATE '2020-01-01', 1, 1 FROM generate_series(1, 3);
            INSERT INTO "PushReminders" ("UserId", "HabitId", "Date") VALUES ('{user}', {habit}, DATE '2020-01-01'), ('{user}', {habit}, DATE '2020-01-02');
            """, "emberdb");
        time.Advance(TimeSpan.FromMinutes(10));
        (await ReminderQueries.SnapshotAsync(db.Ember, db.Ops, time, default)).Should().BeTrue();
        await Superuser("""DELETE FROM "ReminderMessages" WHERE "Date" = DATE '2020-01-01'; DELETE FROM "PushReminders" WHERE "Date" < DATE '2021-01-01'""", "emberdb");
        time.Advance(TimeSpan.FromMinutes(5));
        (await ReminderQueries.SnapshotAsync(db.Ember, db.Ops, time, default)).Should().BeFalse("nothing was sent since the last snapshot and deleting rows does not move the sequence");

        var history = await ReminderQueries.HistoryAsync(db.Ops, time, default);

        history.Since.Should().Be(firstAt);
        history.PerDay.Should().HaveCount(90);
        history.PerDay.Where(d => d.Discord > 0 || d.Push > 0).Should().BeEquivalentTo([new ChannelDay(DatabaseFixture.Today, 3, 2)]);
    }

    private async Task Superuser(string sql, string database)
    {
        await using var connection = await fixture.OpenAsync(database);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
