using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace Ember.Api.Tests;

internal static class ErrorsAssertions
{
    /// <summary>Asserts a 400 <c>validation</c> problem whose <c>errors</c> object equals <paramref name="expected"/>, property order included.</summary>
    public static async Task ExpectErrors(this HttpResponseMessage response, string expected)
    {
        var problem = await response.ExpectProblem(HttpStatusCode.BadRequest, "validation");
        JsonNode.Parse(problem.GetProperty("errors").GetRawText())!.ToJsonString()
            .Should().Be(JsonNode.Parse(expected)!.ToJsonString());
    }
}
