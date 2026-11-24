using FluentAssertions;
using Npgsql;

namespace Ember.Ops.Tests;

[Collection(EmptyCollection.Name)]
public sealed class DropFeedbackBoardTests(EmptyDatabaseFixture fixture)
{
    private static readonly string Script = Path.Combine(DatabaseFixture.OpsRoot(), "sql", "drop_feedback_board.sql");

    [Fact]
    public async Task Drops_only_the_two_board_tables_can_run_twice_and_the_app_starts_without_them()
    {
        await using var db = fixture.OpenOpsDatabases();
        await OpsMigrator.ApplyAsync(db.Ops);
        await ExecuteAsync("emberops", """
            INSERT INTO comments (feedback_id, author, body) VALUES (1, 'tester', 'Synthetic note');
            INSERT INTO card_positions (feedback_id, position) VALUES (1, 1.5);
            INSERT INTO NoticeLog (actor, action, notice) VALUES ('tester', 'publish', '{"id": "synthetic-kept"}');
            """);
        var before = await TablesAsync();

        await fixture.Database.RunScriptAsync(Script, "emberops");
        await fixture.Database.RunScriptAsync(Script, "emberops");

        (await TablesAsync()).Should().BeEquivalentTo(before.Except(["comments", "card_positions"]));
        (await fixture.ScalarAsync<long>("emberops", "SELECT count(*) FROM NoticeLog WHERE notice->>'id' = 'synthetic-kept'")).Should().Be(1);
        (await OpsMigrator.ApplyAsync(db.Ops)).Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_another_database_and_keeps_its_comments_table()
    {
        await ExecuteAsync("postgres", "CREATE TABLE comments (id int)");

        var run = () => fixture.Database.RunScriptAsync(Script, "postgres");

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*connected to postgres, not emberops*");
        (await fixture.ScalarAsync<bool>("postgres", "SELECT to_regclass('public.comments') IS NOT NULL")).Should().BeTrue();
    }

    private async Task<string[]> TablesAsync() =>
        (await fixture.ScalarAsync<string>("emberops", "SELECT string_agg(tablename, ',') FROM pg_tables WHERE schemaname = 'public'")).Split(',');

    private async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = await fixture.OpenAsync(database);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
