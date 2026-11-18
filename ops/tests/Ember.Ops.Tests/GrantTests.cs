using FluentAssertions;
using Npgsql;

namespace Ember.Ops.Tests;

[Collection(V2Collection.Name)]
public sealed class GrantTests(V2DatabaseFixture fixture)
{
    [Fact]
    public async Task Dashboard_role_can_read_the_aggregate_columns()
    {
        string[] reads =
        [
            """SELECT "Id", "ShowStreaks", "GraceDaysPerWeek", "SecondReminderMinutes", "DmFailureCount", "DmRemindersPausedAt", "DiscordReminders", "FiresideEnabled" FROM "AspNetUsers" """,
            """SELECT "Id", "UserId", "CreatedAt", "RoutineId", "PausedFrom", "PausedUntil", "NextReminderUtc", "SecondReminderUtc", "Kind" FROM "Habits" """,
            """SELECT "HabitId", "Date", "Completed" FROM "HabitLogs" """,
            """SELECT "Date" FROM "DayNotes" """,
            """SELECT "Date", "Kind" FROM "DayMarks" """,
            """SELECT "HabitId", "Date", "Count" FROM "HabitSlips" """,
            """SELECT "Id" FROM "FiresideLinks" """,
            """SELECT "SentAtUtc" FROM "Warmth" """,
            """SELECT "Id", "Date" FROM "ReminderMessages" """,
            """SELECT "Id", "Date" FROM "PushReminders" """,
            """SELECT "Id", "Title", "Text", "Platform", "Version", "CreatedAt", "Status", "ResolvedAt", "UserId" FROM "Feedback" """,
            """SELECT "Id", "FeedbackId", "ContentType", "Size", "Data" FROM "FeedbackImages" """,
            """SELECT "MigrationId" FROM "__EFMigrationsHistory" """,
            """SELECT pg_sequence_last_value(pg_get_serial_sequence('"ReminderMessages"', 'Id'))""",
        ];

        await using var connection = fixture.DashboardConnection();
        await connection.OpenAsync();
        foreach (var sql in reads)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var read = async () => { await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) { } };
            await read.Should().NotThrowAsync(because: sql);
        }
    }

    [Theory]
    [InlineData("""SELECT "Name" FROM "Habits" """)]
    [InlineData("""SELECT * FROM "Habits" """)]
    [InlineData("""SELECT "Text" FROM "DayNotes" """)]
    [InlineData("""SELECT * FROM "Moods" """)]
    [InlineData("""SELECT "Email" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "PasswordHash" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "Timezone" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "OwnerId" FROM "FiresideLinks" """)]
    [InlineData("""SELECT "FromUserId" FROM "Warmth" """)]
    [InlineData("""SELECT "Endpoint" FROM "PushSubscriptions" """)]
    [InlineData("""UPDATE "Feedback" SET "Title" = 'changed' """)]
    [InlineData("""UPDATE "Habits" SET "NextReminderUtc" = NULL""")]
    [InlineData("""INSERT INTO "Feedback" ("Title") VALUES ('x')""")]
    [InlineData("""DELETE FROM "Feedback" """)]
    public async Task Dashboard_role_is_refused_everything_else(string sql)
    {
        await using var connection = fixture.DashboardConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);

        var run = async () => await command.ExecuteNonQueryAsync();

        (await run.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task Dashboard_role_can_move_a_feedback_card()
    {
        await using var connection = fixture.DashboardConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""UPDATE "Feedback" SET "Status" = 'implemented', "ResolvedAt" = NULL WHERE "Id" = $1""", connection, transaction)
        {
            Parameters = { new() { Value = fixture.Data.FeedbackOpen } },
        };

        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Dashboard_role_cannot_connect_to_the_emberops_database()
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.Database.OpsConnection)
        {
            Username = "ember_dashboard",
            Password = File.ReadAllText(fixture.Database.EmberPasswordFile).Trim(),
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);

        var open = async () => await connection.OpenAsync();

        (await open.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    [Fact]
    public async Task Emberops_role_owns_its_database()
    {
        var owner = await fixture.ScalarAsync<string>("postgres", "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'emberops'");

        owner.Should().Be("emberops");
    }

    [Fact]
    public async Task Rerunning_the_grant_script_succeeds_and_keeps_the_grants()
    {
        var password = File.ReadAllText(fixture.Database.EmberPasswordFile).Trim();
        var script = Path.Combine(DatabaseFixture.OpsRoot(), "sql", "ember_dashboard.sql");

        var rerun = async () => await fixture.Database.RunScriptAsync(script, "emberdb", "DASH_PW", password);

        await rerun.Should().NotThrowAsync();
        await using var connection = fixture.DashboardConnection();
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand("""SELECT count(*) FROM "AspNetUsers" """, connection);
        (await count.ExecuteScalarAsync()).Should().Be(6L);
    }
}
