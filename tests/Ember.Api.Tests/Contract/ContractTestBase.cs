namespace Ember.Api.Tests.Contract;

/// <summary>
/// One area of the API against a fresh database. A scenario calls the endpoints through <see cref="Rec"/> and ends with
/// <see cref="Verify"/>, which compares the recorded exchanges with <c>responses/{area}.json</c>.
/// </summary>
public abstract class ContractTestBase(PostgresFixture postgres, string area) : ApiTestBase(postgres)
{
    private readonly Exchanges _exchanges = new();

    /// <summary>Awaits <paramref name="call"/>, records it as exchange <paramref name="name"/> of the endpoint <paramref name="route"/> and returns the response.</summary>
    protected Task<HttpResponseMessage> Rec(string name, string route, Task<HttpResponseMessage> call) => _exchanges.Record(name, route, call);

    protected void Verify() => _exchanges.Verify($"responses/{area}.json");
}
