using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ember.Ops.Tests;

[Collection(V2Collection.Name)]
public sealed class AccessTests(V2DatabaseFixture database) : IDisposable
{
    private const string CapabilityHeader = $$"""{"{{OpsApiFactory.Capability}}":[{"author":"nico"}]}""";

    private readonly OpsApiFactory _factory = new(database);

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/api/overview")]
    [InlineData("/api/session")]
    [InlineData("/")]
    [InlineData("/index.html")]
    public async Task Refuses_a_request_without_identity(string path)
    {
        using var client = _factory.ClientFrom(login: null);

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Lets_an_allow_listed_login_from_a_trusted_address_use_the_api_and_the_spa()
    {
        using var client = _factory.ClientFrom(login: "OPERATOR@example.test");

        (await client.GetAsync("/api/overview")).StatusCode.Should().Be(HttpStatusCode.OK);
        var spa = await client.GetAsync("/");
        spa.StatusCode.Should().Be(HttpStatusCode.OK);
        (await spa.Content.ReadAsStringAsync()).Should().Contain("ember-ops-spa");
        (await client.GetFromJsonAsync<JsonElement>("/api/session")).GetProperty("author").GetString().Should().Be(OpsApiFactory.Login);
    }

    [Fact]
    public async Task Refuses_forged_identity_headers_from_an_untrusted_address()
    {
        using var client = _factory.ClientFrom(OpsApiFactory.UntrustedIp, OpsApiFactory.Login, CapabilityHeader);

        (await client.GetAsync("/api/overview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Refuses_a_login_that_is_not_on_the_allow_list()
    {
        using var client = _factory.ClientFrom(login: "stranger@example.test");

        (await client.GetAsync("/api/overview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(CapabilityHeader, "nico")]
    [InlineData($$$"""{"{{{OpsApiFactory.Capability}}}":[{}]}""", "tailnet")]
    public async Task Lets_the_configured_capability_in_and_takes_the_author_from_it(string capabilities, string author)
    {
        using var client = _factory.ClientFrom(login: null, capabilities: capabilities);

        var response = await client.GetAsync("/api/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("author").GetString().Should().Be(author);
    }

    [Theory]
    [InlineData("""{"other.example/cap/x":[{"author":"nico"}]}""")]
    [InlineData($$$"""{"{{{OpsApiFactory.Capability}}}":{"author":"nico"}}""")]
    [InlineData($$"""[{"{{OpsApiFactory.Capability}}":[]}]""")]
    [InlineData("""{"example.test/cap/ember-ops":[{"author":"nico"}""")]
    [InlineData("not json")]
    public async Task Refuses_a_capability_header_that_is_malformed_or_names_another_capability(string capabilities)
    {
        using var client = _factory.ClientFrom(login: null, capabilities: capabilities);

        (await client.GetAsync("/api/overview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Refuses_everything_when_no_login_and_no_capability_is_configured()
    {
        using var factory = new OpsApiFactory(database, logins: [], capability: null);
        using var client = factory.ClientFrom(login: OpsApiFactory.Login, capabilities: CapabilityHeader);

        (await client.GetAsync("/api/overview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Refuses_everything_when_no_trusted_address_is_configured()
    {
        using var factory = new OpsApiFactory(database, trusted: []);
        using var client = factory.ClientFrom(login: OpsApiFactory.Login, capabilities: CapabilityHeader);

        (await client.GetAsync("/api/overview")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Answers_the_health_check_without_identity_and_nothing_else_of_value()
    {
        using var client = _factory.ClientFrom(OpsApiFactory.UntrustedIp, login: null);

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
    }

    [Fact]
    public async Task Refuses_a_host_name_outside_allowed_hosts_but_answers_the_container_health_check()
    {
        using var factory = new OpsApiFactory(database, allowedHosts: "ops.example.test;localhost;127.0.0.1");
        using var client = factory.ClientFrom();

        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/overview") { Headers = { Host = "attacker.example" } };
        (await client.SendAsync(foreign)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var health = new HttpRequestMessage(HttpMethod.Get, "/healthz") { Headers = { Host = "127.0.0.1:8080" } };
        (await client.SendAsync(health)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/overview")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Issues_the_csrf_cookie_secure_only_even_when_the_proxy_forwards_plain_http()
    {
        using var client = _factory.ClientFrom(scheme: "http");

        var response = await client.GetAsync("/api/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie").Should().ContainSingle(c => c.StartsWith("ember-ops-csrf=")).Which.Should().Contain("secure");
    }
}
