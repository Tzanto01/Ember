using FluentAssertions;

namespace Ember.Api.Tests.Contract;

[Collection("Postgres")]
public class RouteTableTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    /// <summary>Endpoints no exchange has to cover. Each needs a reason.</summary>
    private static readonly string[] NotRecorded = [];

    /// <summary>
    /// Paths that exchanges record although the app has no endpoint for them: the Discord OAuth handler answers its callback
    /// in the authentication middleware.
    /// </summary>
    private static readonly string[] HandledByMiddleware = ["GET /api/auth/discord/callback"];

    [DockerFact]
    public void Route_table_matches_the_golden()
    {
        var table = string.Join("\n", RouteTable.Read(Host.Services).Select(l => l.Line)) + "\n";

        Golden.Check("routes.txt", table);
    }

    [DockerFact]
    public void Every_route_has_a_recorded_exchange_and_every_exchange_a_route()
    {
        if (Golden.Writing) return;

        var routes = RouteTable.Read(Host.Services).Select(l => l.Key).ToList();
        var exchanges = Directory.GetFiles(Golden.Path("responses"), "*.json")
            .SelectMany(file => Exchanges.Routes(Path.Combine("responses", Path.GetFileName(file))))
            .Select(e => $"{e.Method} {e.Route}")
            .ToHashSet();

        bool Covered(string route) => route.StartsWith("ANY ", StringComparison.Ordinal)
            ? exchanges.Any(e => e.EndsWith(route[3..], StringComparison.Ordinal))
            : exchanges.Contains(route);

        routes.Where(r => !Covered(r) && !NotRecorded.Contains(r)).Should().BeEmpty(
            "every endpoint needs an exchange in tests/Ember.Api.Tests/Contract/responses (a new endpoint: add its scenario)");
        exchanges.Where(e => !routes.Any(r => r == e || (r.StartsWith("ANY ", StringComparison.Ordinal) && e.EndsWith(r[3..], StringComparison.Ordinal)))
                && !HandledByMiddleware.Contains(e))
            .Should().BeEmpty("an exchange names a route the app doesn't have (its route template is wrong, or the endpoint is gone)");
    }
}
