using FluentAssertions;
using Npgsql;

namespace Ember.Ops.Tests;

[Collection(V2Collection.Name)]
public sealed class FeedbackPurgeTests(V2DatabaseFixture fixture)
{
    private const int NeverExisted = 987654;

    [Fact]
    public async Task Removes_comments_and_card_positions_of_feedback_Ember_no_longer_has_and_keeps_the_rest()
    {
        await using var db = fixture.OpenOpsDatabases();
        await OpsMigrator.ApplyAsync(db.Ops);
        int deleted;
        await using (var connection = await fixture.OpenAsync("emberdb"))
            deleted = await new SyntheticRows(connection).AddReturningIdAsync("Feedback", new()
            {
                ["Platform"] = "web",
                ["Title"] = "Synthetic purge",
                ["Text"] = "Synthetic",
                ["CreatedAt"] = DateTime.UtcNow,
                ["Status"] = "open",
            });
        var kept = fixture.Data.FeedbackOpen;
        foreach (var id in new[] { deleted, deleted, kept, NeverExisted })
            await FeedbackStore.AddCommentAsync(db.Ops, id, "tester", "Synthetic comment", default);
        await FeedbackStore.MoveAsync(db, deleted, "open", 1.5, default);
        await FeedbackStore.MoveAsync(db, kept, "open", 2.5, default);
        await SuperuserAsync("emberdb", $"""DELETE FROM "Feedback" WHERE "Id" = {deleted}""");

        var removed = await FeedbackStore.PurgeOrphansAsync(db, default);

        removed.Should().Be(4, "two comments and a position of the deleted report, and the comment of a report that never existed");
        (await CountAsync($"SELECT count(*) FROM comments WHERE feedback_id IN ({deleted}, {NeverExisted})")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM card_positions WHERE feedback_id = {deleted}")).Should().Be(0);
        (await CountAsync($"SELECT count(*) FROM comments WHERE feedback_id = {kept}")).Should().BeGreaterThan(0);
        (await CountAsync($"SELECT count(*) FROM card_positions WHERE feedback_id = {kept}")).Should().Be(1);
        (await FeedbackStore.PurgeOrphansAsync(db, default)).Should().Be(0);
    }

    private Task<long> CountAsync(string sql) => fixture.ScalarAsync<long>("emberops", sql);

    private async Task SuperuserAsync(string database, string sql)
    {
        await using var connection = await fixture.OpenAsync(database);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
