using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ember.Ops.Tests;

[Collection(ProductionCollection.Name)]
public sealed class ProductionSchemaTests(ProductionDatabaseFixture database) : IDisposable
{
    private readonly OpsApiFactory _factory = new(database);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Every_section_works_on_the_production_schema_and_the_v2_parts_are_null()
    {
        using var client = _factory.ClientFrom();

        foreach (var path in new[] { "/api/reminders", "/api/feedback", "/api/system" })
            (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.OK, path);
        var overview = await client.GetFromJsonAsync<JsonElement>("/api/overview");

        overview.GetProperty("fireside").ValueKind.Should().Be(JsonValueKind.Null);
        overview.GetProperty("letGoSlipsPerWeek").ValueKind.Should().Be(JsonValueKind.Null);
        overview.GetProperty("habits").GetProperty("letGo").ValueKind.Should().Be(JsonValueKind.Null);
        overview.GetProperty("habits").GetProperty("build").GetInt32().Should().Be(8, "without Kind every habit is a build habit");
        overview.GetProperty("users").GetProperty("total").GetInt32().Should().Be(6);
    }
}
