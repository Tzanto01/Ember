namespace Ember.Api.Tests.Contract;

[Collection("Postgres")]
public class RouteTableTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [DockerFact]
    public void Route_table_matches_the_golden()
    {
        var table = string.Join("\n", RouteTable.Read(Host.Services).Select(l => l.Line)) + "\n";

        Golden.Check("routes.txt", table);
    }
}
