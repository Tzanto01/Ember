using System.Net;
using FluentAssertions;

namespace Ember.Api.Tests;

[Collection("Postgres")]
public class LimitValidationApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static async Task ExpectFieldMessage(HttpResponseMessage response, string field, string message)
    {
        var problem = await response.ExpectProblem(HttpStatusCode.BadRequest, "validation");
        problem.GetProperty("errors").GetProperty(field).EnumerateArray().Select(e => e.GetString()).Should().Equal(message);
    }

    [DockerFact]
    public async Task Habit_name_accepts_100_characters_and_rejects_101()
    {
        var user = await Host.SignedInAsync();

        await (await user.Post("/api/habits", new { name = new string('a', 100), frequency = "daily" })).Expect(HttpStatusCode.Created);

        await ExpectFieldMessage(await user.Post("/api/habits", new { name = new string('a', 101), frequency = "daily" }),
            "name", "Must be 1 to 100 characters.");
    }

    [DockerFact]
    public async Task Routine_name_accepts_50_characters_and_rejects_51()
    {
        var user = await Host.SignedInAsync();

        await (await user.Post("/api/routines", new { name = new string('r', 50) })).Expect(HttpStatusCode.Created);

        await ExpectFieldMessage(await user.Post("/api/routines", new { name = new string('r', 51) }),
            "name", "Must be 1 to 50 characters.");
    }

    [DockerFact]
    public async Task Times_per_day_accepts_10_and_rejects_11()
    {
        var user = await Host.SignedInAsync();

        var created = await user.Post("/api/habits", new { name = "Water", frequency = "daily", timesPerDay = 10 });
        (await created.ExpectJson(HttpStatusCode.Created)).GetProperty("timesPerDay").GetInt32().Should().Be(10);

        await ExpectFieldMessage(await user.Post("/api/habits", new { name = "Tea", frequency = "daily", timesPerDay = 11 }),
            "timesPerDay", "Must be between 1 and 10.");
    }

    [DockerFact]
    public async Task The_11th_routine_is_rejected_with_the_routine_limit_message()
    {
        var user = await Host.SignedInAsync();
        for (var i = 0; i < 10; i++)
            await (await user.Post("/api/routines", new { name = $"Routine {i}" })).Expect(HttpStatusCode.Created);

        var problem = await (await user.Post("/api/routines", new { name = "One too many" })).ExpectProblem(HttpStatusCode.Conflict, "conflict");

        problem.GetProperty("detail").GetString().Should()
            .Be("You already have 10 routines, the most Ember can hold. Delete one to add another.");
    }
}
