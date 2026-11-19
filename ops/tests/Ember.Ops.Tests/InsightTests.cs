using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ember.Ops.Tests;

/// <summary>Every number is counted by hand from <see cref="Dataset"/>; today is Wednesday 2026-11-18, its week starts 2026-11-16.</summary>
[Collection(V2Collection.Name)]
public sealed class InsightTests(V2DatabaseFixture fixture) : IDisposable
{
    private static readonly DateOnly Today = DatabaseFixture.Today;
    private readonly OpsApiFactory _factory = new(fixture, hubAddress: OpsApiFactory.HubIp, lookoutKey: OpsApiFactory.LookoutKey);

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> PageAsync(string id)
    {
        using var client = _factory.ClientFrom(OpsApiFactory.HubIp);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {OpsApiFactory.LookoutKey}");
        return await client.GetFromJsonAsync<JsonElement>($"/lookout/v1/pages/{id}");
    }

    [Fact]
    public async Task The_overview_tiles_carry_the_user_trend_and_the_sign_in_windows()
    {
        var tiles = LookoutShape.Widget(await PageAsync("overview"), "tiles");

        var users = LookoutShape.Item(tiles, "Users");
        users.GetProperty("trend").EnumerateArray().Select(t => t.GetDouble()).Should().Equal(1, 1, 1, 1, 1, 1, 1, 1, 2, 4, 4, 5);
        users.GetProperty("delta").GetProperty("value").GetDouble().Should().Be(1);
        users.GetProperty("delta").GetProperty("better").GetString().Should().Be("up");
        var active7 = LookoutShape.Item(tiles, "Active 7 days");
        (active7.GetProperty("value").GetInt32(), active7.GetProperty("max").GetDouble()).Should().Be((2, 6));
        active7.TryGetProperty("trend", out _).Should().BeFalse("there is no snapshot yet");
        active7.TryGetProperty("delta", out _).Should().BeFalse();
        var active14 = LookoutShape.Item(tiles, "Active 14 days");
        active14.TryGetProperty("delta", out _).Should().BeFalse();
        (active14.GetProperty("value").GetInt32(), active14.GetProperty("max").GetDouble()).Should().Be((4, 6));
        LookoutShape.Item(tiles, "New users this week").GetProperty("value").GetInt32().Should().Be(1, "u1's first habit is from yesterday");
        LookoutShape.Item(tiles, "Active today").GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null, "there is no snapshot yet");
        LookoutShape.Item(tiles, "Stickiness").GetProperty("hint").GetString().Should().Be("No snapshots yet");
    }

    [Fact]
    public async Task The_devices_page_counts_adoption_against_all_users()
    {
        var adoption = LookoutShape.Widget(await PageAsync("devices"), "adoption");

        LookoutShape.Item(adoption, "Web push on").GetProperty("hint").GetString().Should().Be("33% of users", "u1 and u2 have an enabled subscription, u3's is off");
        LookoutShape.Item(adoption, "Android app installs").GetProperty("value").GetInt32().Should().Be(2);
        LookoutShape.Item(adoption, "Discord linked").GetProperty("max").GetDouble().Should().Be(6);
    }
}
