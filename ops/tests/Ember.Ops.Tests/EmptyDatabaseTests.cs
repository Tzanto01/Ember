using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ember.Ops.Tests;

[Collection(EmptyCollection.Name)]
public sealed class EmptyDatabaseTests(EmptyDatabaseFixture database) : IDisposable
{
    private readonly OpsApiFactory _factory = new(database, hubAddress: OpsApiFactory.HubIp, lookoutKey: OpsApiFactory.LookoutKey);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Every_page_and_the_summary_answer_with_the_contract_shape_on_an_empty_database()
    {
        using var client = _factory.ClientFrom(OpsApiFactory.HubIp);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {OpsApiFactory.LookoutKey}");

        foreach (var id in LookoutShape.PageIds)
        {
            using var response = await client.GetAsync($"/lookout/v1/pages/{id}");
            response.StatusCode.Should().Be(HttpStatusCode.OK, id);
            LookoutShape.AssertPage(await response.Content.ReadFromJsonAsync<JsonElement>(), id);
        }
        var summary = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/summary");
        summary.GetProperty("tiles")[0].GetProperty("value").GetInt32().Should().Be(0);

        var engagement = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/pages/engagement");
        engagement.GetProperty("widgets").EnumerateArray().Where(w => w.GetProperty("id").GetString() != "days-active")
            .Select(w => w.GetProperty("unavailable").GetString()).Should().AllBe("No snapshots yet");
        var adoption = LookoutShape.Widget(await client.GetFromJsonAsync<JsonElement>("/lookout/v1/pages/devices"), "adoption");
        LookoutShape.Item(adoption, "Web push on").GetProperty("hint").GetString().Should().Be("0% of users");
    }

    [Fact]
    public async Task The_purge_keeps_every_comment_while_Ember_has_no_feedback_at_all()
    {
        await using var db = database.OpenOpsDatabases();
        await OpsMigrator.ApplyAsync(db.Ops);
        await FeedbackStore.AddCommentAsync(db.Ops, 1, "tester", "Synthetic comment", default);

        (await FeedbackStore.PurgeOrphansAsync(db, default)).Should().Be(0);
        (await database.ScalarAsync<long>("emberops", "SELECT count(*) FROM comments WHERE feedback_id = 1")).Should().Be(1);
    }
}
