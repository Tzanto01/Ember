using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Ember.Api.Tests;

/// <summary>The account's Today order (<c>PUT /api/me/today-order</c>) and the habit-note offer setting on <c>/api/me</c>.</summary>
[Collection("Postgres")]
public class TodayOrderApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static async Task<int> HabitIdAsync(ApiClient user)
        => (await user.CreateHabitAsync()).GetProperty("id").GetInt32();

    private static async Task<JsonElement> MeAsync(ApiClient user) => await (await user.Get("/api/me")).ExpectJson();

    private static async Task<string?[]?> OrderAsync(HttpResponseMessage response)
    {
        var order = (await response.ExpectJson()).GetProperty("order");
        return order.ValueKind == JsonValueKind.Null ? null : order.EnumerateArray().Select(k => k.GetString()).ToArray();
    }

    [DockerFact]
    public async Task Saving_keeps_own_top_level_keys_once_in_order_and_drops_the_rest()
    {
        var user = await Host.SignedInAsync();
        var other = await Host.SignedInAsync();
        var solo = await HabitIdAsync(user);
        var member = await HabitIdAsync(user);
        var routine = (await (await user.Post("/api/routines", new { name = "Morning", habitIds = new[] { member } }))
            .ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var foreign = await HabitIdAsync(other);

        var saved = await OrderAsync(await user.Put("/api/me/today-order", new
        {
            order = new[]
            {
                $"r:{routine}", $"h:{member}", $"h:{solo}", $"h:{solo}", $"h:0{solo}", $"h:{foreign}", "h:999999", "x:1", "r:abc", "",
            },
        }));

        saved.Should().Equal($"r:{routine}", $"h:{solo}");
        (await MeAsync(user)).GetProperty("todayOrder").EnumerateArray().Select(k => k.GetString())
            .Should().Equal($"r:{routine}", $"h:{solo}");
    }

    [DockerFact]
    public async Task Null_resets_to_automatic()
    {
        var user = await Host.SignedInAsync();
        var habit = await HabitIdAsync(user);
        (await MeAsync(user)).GetProperty("todayOrder").ValueKind.Should().Be(JsonValueKind.Null);
        await (await user.Put("/api/me/today-order", new { order = new[] { $"h:{habit}" } })).Expect(HttpStatusCode.OK);

        (await OrderAsync(await user.Put("/api/me/today-order", new { order = (string[]?)null }))).Should().BeNull();

        (await MeAsync(user)).GetProperty("todayOrder").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [DockerFact]
    public async Task More_than_500_entries_is_a_400_and_keeps_the_saved_order()
    {
        var user = await Host.SignedInAsync();
        var habit = await HabitIdAsync(user);
        await (await user.Put("/api/me/today-order", new { order = new[] { $"h:{habit}" } })).Expect(HttpStatusCode.OK);

        var problem = await (await user.Put("/api/me/today-order", new { order = Enumerable.Repeat($"h:{habit}", 501).ToArray() }))
            .ExpectProblem(HttpStatusCode.BadRequest, "validation");
        problem.GetProperty("errors").Has("order").Should().BeTrue();
        (await MeAsync(user)).GetProperty("todayOrder").EnumerateArray().Select(k => k.GetString()).Should().Equal($"h:{habit}");
        (await OrderAsync(await user.Put("/api/me/today-order", new { order = Enumerable.Repeat($"h:{habit}", 500).ToArray() })))
            .Should().Equal($"h:{habit}");
    }

    [DockerFact]
    public async Task Offer_habit_note_is_off_by_default_and_patch_turns_it_on()
    {
        var user = await Host.SignedInAsync();
        (await MeAsync(user)).GetProperty("offerHabitNote").GetBoolean().Should().BeFalse();

        var patched = await (await user.Patch("/api/me", new { offerHabitNote = true })).ExpectJson();

        patched.GetProperty("offerHabitNote").GetBoolean().Should().BeTrue();
        (await MeAsync(user)).GetProperty("offerHabitNote").GetBoolean().Should().BeTrue();
    }
}
