using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Ember.Ops.Tests;

[Collection(V2Collection.Name)]
public sealed class BoardTests(V2DatabaseFixture database) : IDisposable
{
    private readonly OpsApiFactory _factory = new(database);

    public void Dispose() => _factory.Dispose();

    private Task<long> CommentCountAsync(int feedbackId) =>
        database.ScalarAsync<long>("emberops", "SELECT count(*) FROM comments WHERE feedback_id = $1", feedbackId);

    [Fact]
    public async Task Rejects_a_comment_without_a_csrf_token_and_writes_nothing()
    {
        using var client = _factory.ClientFrom();
        var id = database.Data.FeedbackOpen;
        var before = await CommentCountAsync(id);

        var response = await client.PostAsJsonAsync($"/api/feedback/{id}/comments", new { body = "no token" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CommentCountAsync(id)).Should().Be(before);
    }

    [Fact]
    public async Task Stores_a_comment_with_a_valid_token_under_the_identity_as_author()
    {
        using var client = _factory.ClientFrom();
        await OpsApiFactory.StartSessionAsync(client);
        var id = database.Data.FeedbackOpen;

        var response = await client.PostAsJsonAsync($"/api/feedback/{id}/comments", new { body = "  looks good  " });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = await database.ScalarAsync<string>("emberops", "SELECT author || '|' || body FROM comments WHERE feedback_id = $1 ORDER BY id DESC LIMIT 1", id);
        stored.Should().Be($"{OpsApiFactory.Login}|looks good");
    }

    [Fact]
    public async Task Rejects_a_write_marked_cross_site_even_with_a_valid_token()
    {
        using var client = _factory.ClientFrom();
        await OpsApiFactory.StartSessionAsync(client);
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        var id = database.Data.FeedbackOpen;
        var before = await CommentCountAsync(id);

        var response = await client.PostAsJsonAsync($"/api/feedback/{id}/comments", new { body = "cross site" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CommentCountAsync(id)).Should().Be(before);
    }

    [Fact]
    public async Task Moving_a_card_to_released_sets_resolved_at_and_moving_it_back_clears_it()
    {
        using var client = _factory.ClientFrom();
        await OpsApiFactory.StartSessionAsync(client);
        var id = database.Data.FeedbackPlanned;
        var resolvedAt = () => database.ScalarAsync<DateTime?>("emberdb", """SELECT "ResolvedAt" FROM "Feedback" WHERE "Id" = $1""", id);

        (await client.PutAsJsonAsync($"/api/feedback/{id}/status", new { status = "released", position = (double?)null })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var released = await resolvedAt();
        (await client.PutAsJsonAsync($"/api/feedback/{id}/status", new { status = "planned", position = 1.5 })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var planned = await resolvedAt();

        released.Should().NotBeNull();
        planned.Should().BeNull();
        (await database.ScalarAsync<string>("emberdb", """SELECT "Status" FROM "Feedback" WHERE "Id" = $1""", id)).Should().Be("planned");
    }

    [Fact]
    public async Task A_card_moved_between_two_never_moved_cards_sorts_between_them()
    {
        var ids = new List<int>();
        await using (var connection = await database.OpenAsync("emberdb"))
        {
            var rows = new SyntheticRows(connection);
            foreach (var days in new[] { 30, 20, 10 })
                ids.Add(await rows.AddReturningIdAsync("Feedback", new()
                {
                    ["Platform"] = "web",
                    ["Title"] = $"Order {days}",
                    ["Text"] = "Synthetic",
                    ["CreatedAt"] = DateTime.UtcNow.AddDays(-days - 400),
                    ["Status"] = "declined",
                }));
        }
        using var client = _factory.ClientFrom();
        await OpsApiFactory.StartSessionAsync(client);
        async Task<List<FeedbackItem>> ColumnAsync() =>
            (await client.GetFromJsonAsync<FeedbackList>("/api/feedback"))!.Items.Where(i => ids.Contains(i.Id)).ToList();
        var (oldest, middle, newest) = (ids[0], ids[1], ids[2]);
        var before = await ColumnAsync();
        before.Select(i => i.Id).Should().Equal(newest, middle, oldest);

        var between = (before[1].Position + before[2].Position) / 2;
        (await client.PutAsJsonAsync($"/api/feedback/{newest}/status", new { status = "declined", position = between })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ColumnAsync()).Select(i => i.Id).Should().Equal(middle, newest, oldest);
    }

    [Fact]
    public async Task Rejects_a_status_move_without_a_csrf_token_and_changes_nothing()
    {
        using var client = _factory.ClientFrom();
        var id = database.Data.FeedbackOpen;
        var before = await database.ScalarAsync<string>("emberdb", """SELECT "Status" FROM "Feedback" WHERE "Id" = $1""", id);

        var response = await client.PutAsJsonAsync($"/api/feedback/{id}/status", new { status = "declined", position = (double?)null });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await database.ScalarAsync<string>("emberdb", """SELECT "Status" FROM "Feedback" WHERE "Id" = $1""", id)).Should().Be(before);
    }

    [Fact]
    public async Task No_payload_contains_a_user_id()
    {
        using var client = _factory.ClientFrom();
        var bodies = new List<string>();
        foreach (var path in new[] { "/api/overview", "/api/reminders", "/api/feedback", "/api/system" })
        {
            var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        var all = string.Join('\n', bodies).ToLowerInvariant();

        all.Should().Contain("\"total\":6").And.Contain("synthetic report one", "the payloads carry the seeded data");
        foreach (var id in database.Data.UserIds)
            all.Should().NotContain(id.ToString().ToLowerInvariant());
    }
}
