using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Ember.Api.Tests;

/// <summary>The timer length of a habit (contract "Habit timer length v1.28").</summary>
public class HabitTimerApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string BoundsMessage = "Must be between 1 and 240.";

    private static async Task ExpectTimerError(HttpResponseMessage response, string message)
    {
        var problem = await response.ExpectProblem(HttpStatusCode.BadRequest, "validation");
        var error = problem.GetProperty("errors").EnumerateObject().Single();
        error.Name.Should().Be("timerMinutes");
        error.Value.EnumerateArray().Select(e => e.GetString()).Should().Equal(message);
    }

    private static async Task<int> CreateAsync(ApiClient user, object body)
        => (await (await user.Post("/api/habits", body)).ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();

    private static async Task<int?> TimerAsync(HttpResponseMessage response)
    {
        var timer = (await response.ExpectJson()).GetProperty("timerMinutes");
        return timer.ValueKind == JsonValueKind.Null ? null : timer.GetInt32();
    }

    [DockerFact]
    public async Task Create_takes_1_to_240_whole_minutes_or_none()
    {
        var user = await Host.SignedInAsync();
        object Walk(object? timerMinutes) => new { name = "Walk", frequency = "daily", timerMinutes };

        await ExpectTimerError(await user.Post("/api/habits", Walk(0)), BoundsMessage);
        await ExpectTimerError(await user.Post("/api/habits", Walk(241)), BoundsMessage);
        await (await user.Post("/api/habits", Walk(30.5))).Expect(HttpStatusCode.BadRequest);
        (await (await user.Get("/api/habits")).ExpectJson()).GetArrayLength().Should().Be(0);

        foreach (var minutes in new int?[] { 1, 240, null })
        {
            var id = await CreateAsync(user, Walk(minutes));
            (await TimerAsync(await user.Get($"/api/habits/{id}"))).Should().Be(minutes);
        }
    }

    [DockerFact]
    public async Task Patch_sets_the_length_within_bounds_and_keeps_it_unless_clearTimer_removes_it()
    {
        var user = await Host.SignedInAsync();
        var id = await CreateAsync(user, new { name = "Walk", frequency = "daily" });
        var url = $"/api/habits/{id}";

        (await TimerAsync(await user.Patch(url, new { timerMinutes = 1 }))).Should().Be(1);
        (await TimerAsync(await user.Patch(url, new { timerMinutes = 240 }))).Should().Be(240);
        await ExpectTimerError(await user.Patch(url, new { name = "Run", timerMinutes = 0 }), BoundsMessage);
        await ExpectTimerError(await user.Patch(url, new { timerMinutes = 241 }), BoundsMessage);
        await (await user.Patch(url, new { timerMinutes = 30.5 })).Expect(HttpStatusCode.BadRequest);
        await ExpectTimerError(await user.Patch(url, new { timerMinutes = 30, clearTimer = true }), "Can't be combined with clearTimer.");

        (await TimerAsync(await user.Patch(url, new { reminderTime = "09:00", frequency = "weekly" }))).Should().Be(240);
        (await TimerAsync(await user.Patch(url, new { timerMinutes = (int?)null, clearTimer = false }))).Should().Be(240);
        (await TimerAsync(await user.Patch(url, new { clearTimer = true }))).Should().BeNull();
        var habit = await (await user.Get(url)).ExpectJson();
        (habit.GetProperty("name").GetString(), habit.GetProperty("reminderTime").GetString()).Should().Be(("Walk", "09:00"));
    }

    [DockerFact]
    public async Task A_let_go_habit_never_gets_a_length()
    {
        var user = await Host.SignedInAsync();
        const string message = "Never set for let-go habits.";

        await ExpectTimerError(await user.Post("/api/habits", new { name = "Coffee", kind = "letGo", frequency = "daily", timerMinutes = 30 }), message);
        var id = await CreateAsync(user, new { name = "Coffee", kind = "letGo", frequency = "daily" });
        await ExpectTimerError(await user.Patch($"/api/habits/{id}", new { timerMinutes = 30 }), message);

        (await TimerAsync(await user.Get($"/api/habits/{id}"))).Should().BeNull();
    }
}
