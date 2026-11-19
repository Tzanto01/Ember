using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ember.Ops.Tests;

[Collection(ProductionCollection.Name)]
public sealed class LookoutProductionSchemaTests(ProductionDatabaseFixture database) : IDisposable
{
    private readonly OpsApiFactory _factory = new(database, hubAddress: OpsApiFactory.HubIp, lookoutKey: OpsApiFactory.LookoutKey);

    public void Dispose() => _factory.Dispose();

    private HttpClient Hub()
    {
        var client = _factory.ClientFrom(OpsApiFactory.HubIp);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {OpsApiFactory.LookoutKey}");
        return client;
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("reminders")]
    [InlineData("feedback")]
    [InlineData("notices")]
    [InlineData("system")]
    public async Task Every_page_has_the_contract_shape_on_the_production_schema(string pageId)
    {
        using var client = Hub();

        LookoutShape.AssertPage(await client.GetFromJsonAsync<JsonElement>($"/lookout/v1/pages/{pageId}"), pageId);
    }

    [Fact]
    public async Task The_v2_only_overview_panels_are_unavailable_and_the_let_go_count_is_a_dash()
    {
        using var client = Hub();

        var overview = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/pages/overview");

        foreach (var id in new[] { "fireside", "let-go" })
        {
            var panel = LookoutShape.Widget(overview, id);
            panel.GetProperty("unavailable").GetString().Should().Be("Not on production yet");
            panel.GetProperty("children").GetArrayLength().Should().Be(0);
        }
        var tiles = LookoutShape.Widget(overview, "tiles");
        LookoutShape.Item(tiles, "Users").GetProperty("value").GetInt32().Should().Be(6);
        var habits = LookoutShape.Item(tiles, "Habits build / let go");
        habits.GetProperty("value").GetString().Should().Be("8 / –");
        habits.GetProperty("hint").GetString().Should().Be("Let go: not on production yet");
    }

    [Fact]
    public async Task The_summary_answers_on_the_production_schema()
    {
        using var client = Hub();

        var summary = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/summary");

        summary.GetProperty("tiles").EnumerateArray().Select(t => t.GetProperty("label").GetString()).Should()
            .Equal("Users", "Active 7 days", "Overdue reminders", "Open feedback");
        summary.GetProperty("tiles")[0].GetProperty("value").GetInt32().Should().Be(6);
    }
}
