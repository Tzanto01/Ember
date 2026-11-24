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
    [InlineData("engagement")]
    [InlineData("devices")]
    [InlineData("releases")]
    [InlineData("health")]
    [InlineData("notices")]
    [InlineData("system")]
    public async Task Every_page_has_the_contract_shape_on_the_production_schema(string pageId)
    {
        using var client = Hub();

        LookoutShape.AssertPage(await client.GetFromJsonAsync<JsonElement>($"/lookout/v1/pages/{pageId}"), pageId);
    }

    [Fact]
    public async Task Without_the_outbox_table_the_system_page_marks_it_unavailable_and_the_summary_raises_nothing_for_it()
    {
        using var client = Hub();

        var system = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/pages/system");
        var summary = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/summary");

        var outbox = LookoutShape.Widget(system, "outbox");
        outbox.GetProperty("unavailable").GetString().Should().Be("Unavailable: Ember has no Lookout outbox yet");
        outbox.GetProperty("items").GetArrayLength().Should().Be(0);
        summary.GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("text").GetString()).Should().NotContain(t => t!.Contains("outbox"));
    }

    [Fact]
    public async Task The_overview_counts_users_and_sign_in_activity_on_the_production_schema()
    {
        using var client = Hub();

        var overview = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/pages/overview");

        var tiles = LookoutShape.Widget(overview, "tiles");
        LookoutShape.Item(tiles, "Users").GetProperty("value").GetInt32().Should().Be(6);
        LookoutShape.Item(tiles, "Active 7 days").GetProperty("value").GetInt32().Should().Be(2);
        LookoutShape.Item(tiles, "Active 14 days").GetProperty("value").GetInt32().Should().Be(4);
        LookoutShape.Widget(overview, "new-users").GetProperty("note").GetString().Should().StartWith("by first habit, a proxy");
    }

    [Fact]
    public async Task Without_platform_and_app_version_columns_the_devices_page_marks_them_unavailable()
    {
        using var client = Hub();

        var devices = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/pages/devices");

        LookoutShape.Widget(devices, "platforms").GetProperty("unavailable").GetString().Should().Be("Ember doesn't record the sign-in platform yet");
        LookoutShape.Widget(devices, "versions").GetProperty("unavailable").GetString().Should().Be("Ember doesn't record the app version on sign-in yet");
        LookoutShape.Item(LookoutShape.Widget(devices, "adoption"), "Discord linked").GetProperty("value").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task The_summary_answers_on_the_production_schema()
    {
        using var client = Hub();

        var summary = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/summary");

        summary.GetProperty("tiles").EnumerateArray().Select(t => t.GetProperty("label").GetString()).Should()
            .Equal("Users", "Active 7 days", "API p95");
        summary.GetProperty("tiles")[0].GetProperty("value").GetInt32().Should().Be(6);
    }
}
