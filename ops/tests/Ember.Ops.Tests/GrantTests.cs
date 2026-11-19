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
            """SELECT "Id" FROM "AspNetUsers" """,
            """SELECT "UserId", "LoginProvider" FROM "AspNetUserLogins" """,
            """SELECT "UserId", "CreatedAt", "NextReminderUtc" FROM "Habits" """,
            """SELECT "NextReminderUtc" FROM "Routines" """,
            """SELECT "UserId", "CreatedUtc", "LastSeenUtc" FROM "AuthSessions" """,
            """SELECT "UserId", "Enabled" FROM "PushSubscriptions" """,
            """SELECT count(*) FROM "ReminderPhones" """,
            """SELECT "Id", "Title", "Text", "Platform", "Version", "CreatedAt", "Status", "ResolvedAt", "UserId" FROM "Feedback" """,
            """SELECT "Id", "FeedbackId", "ContentType", "Size", "Data" FROM "FeedbackImages" """,
            """SELECT "MigrationId" FROM "__EFMigrationsHistory" """,
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
    [InlineData("""SELECT * FROM "Moods" """)]
    [InlineData("""SELECT "ProviderKey" FROM "AspNetUserLogins" """)]
    [InlineData("""SELECT "Label" FROM "ReminderPhones" """)]
    [InlineData("""SELECT "Language" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "Name" FROM "Routines" """)]
    [InlineData("""SELECT "Email" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "PasswordHash" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "Timezone" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "Endpoint" FROM "PushSubscriptions" """)]
    [InlineData("""SELECT "Id" FROM "AuthSessions" """)]
    [InlineData("""SELECT "ExpiresUtc" FROM "AuthSessions" """)]
    [InlineData("""SELECT * FROM "AuthSessions" """)]
    [InlineData("""SELECT "HabitId" FROM "HabitLogs" """)]
    [InlineData("""SELECT "UserId" FROM "DayNotes" """)]
    [InlineData("""SELECT "UserId" FROM "DayMarks" """)]
    [InlineData("""SELECT "UserId" FROM "JournalKeys" """)]
    [InlineData("""SELECT "HabitId" FROM "HabitSlips" """)]
    [InlineData("""SELECT "Id" FROM "FiresideLinks" """)]
    [InlineData("""SELECT "SentAtUtc" FROM "Warmth" """)]
    [InlineData("""SELECT "FiresideEnabled" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "Kind" FROM "Habits" """)]
    [InlineData("""SELECT "ShowStreaks" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "GraceDaysPerWeek" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "Theme" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "DiscordReminders" FROM "AspNetUsers" """)]
    [InlineData("""SELECT "ReminderTimes" FROM "Habits" """)]
    [InlineData("""SELECT "PausedUntil" FROM "Habits" """)]
    [InlineData("""SELECT "RoutineId" FROM "Habits" """)]
    [InlineData("""SELECT "ReminderTime" FROM "Routines" """)]
    [InlineData("""SELECT "UserId" FROM "Routines" """)]
    [InlineData("""SELECT "Enabled" FROM "ReminderPhones" """)]
    [InlineData("""SELECT "Date" FROM "ReminderMessages" """)]
    [InlineData("""SELECT "Date" FROM "PushReminders" """)]
    [InlineData("""SELECT pg_sequence_last_value(pg_get_serial_sequence('"ReminderMessages"', 'Id'))""")]
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
    public async Task Dashboard_role_runs_without_jit()
    {
        await using var connection = fixture.DashboardConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SHOW jit", connection);

        (await command.ExecuteScalarAsync()).Should().Be("off", "JIT compiling costs more than the small dashboard queries on the shared host");
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

    [Fact]
    public async Task Rerunning_the_grant_script_revokes_health_columns_an_older_script_had_granted()
    {
        await using (var superuser = await fixture.OpenAsync("emberdb"))
        await using (var grant = new NpgsqlCommand("""
            GRANT SELECT ("HabitId", "Date", "Completed") ON "HabitLogs" TO ember_dashboard;
            GRANT SELECT ("UserId", "Date") ON "DayNotes" TO ember_dashboard;
            GRANT SELECT ("UserId", "Date", "Kind") ON "DayMarks" TO ember_dashboard;
            GRANT SELECT ("UserId") ON "JournalKeys" TO ember_dashboard;
            GRANT SELECT ("HabitId", "Date", "Count") ON "HabitSlips" TO ember_dashboard;
            GRANT SELECT ("Id") ON "FiresideLinks" TO ember_dashboard;
            GRANT SELECT ON "Warmth" TO ember_dashboard;
            GRANT SELECT ("FiresideEnabled") ON "AspNetUsers" TO ember_dashboard;
            GRANT SELECT ("Kind") ON "Habits" TO ember_dashboard;
            """, superuser))
            await grant.ExecuteNonQueryAsync();
        var script = Path.Combine(DatabaseFixture.OpsRoot(), "sql", "ember_dashboard.sql");

        await fixture.Database.RunScriptAsync(script, "emberdb", "DASH_PW", File.ReadAllText(fixture.Database.EmberPasswordFile).Trim());

        var left = await fixture.ScalarAsync<long>("emberdb", """
            SELECT count(*) FROM information_schema.column_privileges
            WHERE grantee = 'ember_dashboard' AND table_schema = 'public'
              AND (table_name IN ('HabitLogs', 'DayNotes', 'DayMarks', 'JournalKeys', 'HabitSlips', 'FiresideLinks', 'Warmth')
                   OR (table_name = 'AspNetUsers' AND column_name = 'FiresideEnabled') OR (table_name = 'Habits' AND column_name = 'Kind'))
            """);
        left.Should().Be(0);
        var sessions = await fixture.ScalarAsync<string>("emberdb", """
            SELECT string_agg(column_name, ',' ORDER BY column_name) FROM information_schema.column_privileges
            WHERE grantee = 'ember_dashboard' AND table_name = 'AuthSessions' AND privilege_type = 'SELECT'
            """);
        sessions.Should().Be("CreatedUtc,LastSeenUtc,UserId");
    }

    [Fact]
    public async Task Rerunning_the_grant_script_revokes_the_habit_reminder_and_setting_columns_the_revision_4_script_had_granted()
    {
        await using (var superuser = await fixture.OpenAsync("emberdb"))
        await using (var grant = new NpgsqlCommand("""
            GRANT SELECT ("ShowStreaks", "GraceDaysPerWeek", "SecondReminderMinutes", "DmFailureCount", "DmRemindersPausedAt", "DiscordReminders", "Theme") ON "AspNetUsers" TO ember_dashboard;
            GRANT SELECT ("Id", "RoutineId", "PausedFrom", "PausedUntil", "SecondReminderUtc", "FrequencyType", "TimesPerDay", "ReminderTimes") ON "Habits" TO ember_dashboard;
            GRANT SELECT ("Id", "UserId", "SecondReminderUtc", "ReminderTime", "CreatedAt") ON "Routines" TO ember_dashboard;
            GRANT SELECT ("Id", "Date") ON "ReminderMessages" TO ember_dashboard;
            GRANT SELECT ("Id", "Date") ON "PushReminders" TO ember_dashboard;
            GRANT SELECT ("Id") ON "PushSubscriptions" TO ember_dashboard;
            GRANT SELECT ("UserId", "Enabled") ON "ReminderPhones" TO ember_dashboard;
            """, superuser))
            await grant.ExecuteNonQueryAsync();
        await using (var superuser = await fixture.OpenAsync("emberdb"))
        await using (var sequence = new NpgsqlCommand("""SELECT format('GRANT SELECT ON SEQUENCE %s TO ember_dashboard', pg_get_serial_sequence('"ReminderMessages"', 'Id'))""", superuser))
        await using (var run = new NpgsqlCommand((string)(await sequence.ExecuteScalarAsync())!, superuser))
            await run.ExecuteNonQueryAsync();

        await fixture.Database.RunScriptAsync(Path.Combine(DatabaseFixture.OpsRoot(), "sql", "ember_dashboard.sql"), "emberdb", "DASH_PW",
            File.ReadAllText(fixture.Database.EmberPasswordFile).Trim());

        var granted = await fixture.ScalarAsync<string>("emberdb", """
            SELECT string_agg(table_name || '.' || column_name, ',' ORDER BY table_name, column_name) FROM information_schema.column_privileges
            WHERE grantee = 'ember_dashboard' AND table_schema = 'public' AND privilege_type = 'SELECT'
              AND table_name IN ('AspNetUsers', 'Habits', 'Routines', 'ReminderMessages', 'PushReminders', 'PushSubscriptions', 'ReminderPhones')
            """);
        granted.Should().Be("AspNetUsers.Id,Habits.CreatedAt,Habits.NextReminderUtc,Habits.UserId,PushSubscriptions.Enabled,PushSubscriptions.UserId,ReminderPhones.Id,Routines.NextReminderUtc");
        (await fixture.ScalarAsync<long>("emberdb", """
            SELECT count(*) FROM pg_class c, aclexplode(c.relacl) a
            WHERE c.relkind = 'S' AND a.grantee = 'ember_dashboard'::regrole
            """)).Should().Be(0);
    }
}
