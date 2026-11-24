using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ember.Ops.TestHost;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Ops.Tests;

[Collection(V2Collection.Name)]
public sealed class LookoutTests(V2DatabaseFixture database) : IDisposable
{
    private const string ActorHeader = "Lookout-Actor";
    private const string NoticesToken = "synthetic-test-token";

    private readonly List<IDisposable> _disposables = [];

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    private OpsApiFactory Factory(string? hub = OpsApiFactory.HubIp, string? key = OpsApiFactory.LookoutKey, HttpMessageHandler? notices = null)
    {
        var factory = new OpsApiFactory(database, notices: notices, noticesToken: NoticesToken, hubAddress: hub, lookoutKey: key);
        _disposables.Add(factory);
        return factory;
    }

    private HttpClient Hub(OpsApiFactory? factory = null, string ip = OpsApiFactory.HubIp, string? bearer = OpsApiFactory.LookoutKey)
    {
        var client = (factory ?? Factory()).ClientFrom(ip);
        _disposables.Add(client);
        if (bearer is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        return client;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? actor = "tester", HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (actor is not null) request.Headers.TryAddWithoutValidation(ActorHeader, actor);
        return request;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Refuses_a_wrong_peer_even_with_a_valid_key()
    {
        using var response = await Hub(ip: OpsApiFactory.UntrustedIp).GetAsync("/lookout/v1/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_hub_address_refuses_every_peer()
    {
        using var response = await Hub(Factory(hub: ""), ip: "127.0.0.1").GetAsync("/lookout/v1/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("none", OpsApiFactory.LookoutKey, null)]
    [InlineData("wrong", OpsApiFactory.LookoutKey, "synthetic-lookout-key-for-tests-0123456780")]
    [InlineData("scheme", OpsApiFactory.LookoutKey, "Basic")]
    [InlineData("short key", "short-key-of-31-bytes-012345678", "short-key-of-31-bytes-012345678")]
    [InlineData("no key file", null, OpsApiFactory.LookoutKey)]
    public async Task Answers_401_without_a_body_unless_the_bearer_matches_a_key_of_at_least_32_bytes(string name, string? key, string? bearer)
    {
        using var client = Hub(Factory(key: key), bearer: name == "scheme" ? null : bearer);
        if (name == "scheme") client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Basic {OpsApiFactory.LookoutKey}");

        using var response = await client.GetAsync("/lookout/v1/manifest");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, name);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Outside_lookout_only_healthz_answers()
    {
        var factory = Factory();
        using var hub = Hub(factory);
        using var stranger = factory.ClientFrom(OpsApiFactory.UntrustedIp);

        foreach (var path in new[] { "/api/overview", "/api/session", "/" })
            (await hub.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        using var health = await stranger.GetAsync("/healthz");

        health.StatusCode.Should().Be(HttpStatusCode.OK);
        (await health.Content.ReadAsStringAsync()).Should().Be("ok");
    }

    [Theory]
    [InlineData("/lookout")]
    [InlineData("/lookout/")]
    [InlineData("/lookout/index.html")]
    [InlineData("/lookout/v1/nope")]
    [InlineData("/lookout/v2/manifest")]
    public async Task An_unmatched_lookout_path_is_a_404_problem(string path)
    {
        using var response = await Hub().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task The_manifest_lists_the_pages_in_order()
    {
        using var client = Hub();

        var manifest = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/manifest");

        manifest.GetProperty("contract").GetInt32().Should().Be(1);
        (manifest.GetProperty("id").GetString(), manifest.GetProperty("name").GetString(), manifest.GetProperty("icon").GetString(), manifest.GetProperty("timeZone").GetString())
            .Should().Be(("ember", "Ember", "flame", "Europe/Berlin"));
        manifest.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("id").GetString()).Should().Equal(LookoutShape.PageIds);
        manifest.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("refreshSeconds").GetInt32()).Should().Equal(30, 60, 60, 30, 60, 30, 30);
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("engagement")]
    [InlineData("devices")]
    [InlineData("releases")]
    [InlineData("health")]
    [InlineData("notices")]
    [InlineData("system")]
    public async Task Every_page_has_the_contract_shape(string pageId)
    {
        using var client = Hub();

        var page = await client.GetFromJsonAsync<JsonElement>($"/lookout/v1/pages/{pageId}");

        LookoutShape.AssertPage(page, pageId);
    }

    [Fact]
    public async Task The_overview_page_carries_the_hand_counted_values()
    {
        using var client = Hub();

        var overview = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/pages/overview");
        var tiles = LookoutShape.Widget(overview, "tiles");

        LookoutShape.Item(tiles, "Users").GetProperty("value").GetInt32().Should().Be(6);
        LookoutShape.Item(tiles, "New users this week").GetProperty("value").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task The_summary_has_the_three_tiles_a_state_and_at_most_five_alerts()
    {
        using var client = Hub();

        var summary = await client.GetFromJsonAsync<JsonElement>("/lookout/v1/summary");

        summary.GetProperty("generatedAt").GetString().Should().EndWith("Z");
        summary.GetProperty("state").GetString().Should().BeOneOf("ok", "warn", "bad");
        summary.GetProperty("tiles").EnumerateArray().Select(t => t.GetProperty("label").GetString()).Should()
            .Equal("Users", "Active 7 days", "API p95");
        summary.GetProperty("tiles")[0].GetProperty("value").GetInt32().Should().Be(6);
        summary.GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("text").GetString()).Should().Contain("Reminders not advancing");
        summary.GetProperty("alerts").GetArrayLength().Should().BeInRange(1, 5);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("feedback")]
    public async Task An_unknown_page_is_a_404_problem(string pageId)
    {
        using var response = await Hub().GetAsync($"/lookout/v1/pages/{pageId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task No_payload_contains_a_user_id()
    {
        using var client = Hub();
        var bodies = new List<string>();
        foreach (var path in LookoutShape.PageIds.Select(id => $"/lookout/v1/pages/{id}").Append("/lookout/v1/summary"))
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        var all = string.Join('\n', bodies).ToLowerInvariant();

        all.Should().Contain("\"label\":\"users\",\"value\":6", "the payloads carry the seeded data");
        foreach (var id in database.Data.UserIds)
            all.Should().NotContain(id.ToString().ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%20%20")]
    [InlineData("a%07b")]
    public async Task A_write_with_a_missing_or_invalid_actor_is_a_400_problem(string? actor)
    {
        using var client = Hub();

        using var response = await client.SendAsync(Request(HttpMethod.Post, "/lookout/v1/widgets/notices/notices", actor,
            JsonContent.Create(new { id = "x", kind = "info", text = new { en = "a", de = "b" } })));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Lookout-Actor");
    }

    [Fact]
    public async Task An_actor_of_101_characters_is_refused_and_100_is_accepted()
    {
        using var client = Hub(Factory(notices: new FakeNoticesUpstream(NoticesToken)));
        const string path = "/lookout/v1/widgets/notices/notices";
        static JsonContent Notice() => JsonContent.Create(new { id = $"lookout-{Guid.NewGuid():N}", kind = "info", text = new { en = "Hello", de = "Hallo" } });

        using var long101 = await client.SendAsync(Request(HttpMethod.Post, path, new string('a', 101), Notice()));
        using var exact100 = await client.SendAsync(Request(HttpMethod.Post, path, new string('a', 100), Notice()));

        long101.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        exact100.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Notice_writes_run_through_the_store_and_log_the_actor()
    {
        var fake = new FakeNoticesUpstream(NoticesToken);
        using var client = Hub(Factory(notices: fake));
        var id = $"lookout-{Guid.NewGuid():N}";

        using var published = await client.SendAsync(Request(HttpMethod.Post, "/lookout/v1/widgets/notices/notices", "Zo%C3%AB",
            JsonContent.Create(new { id, kind = "info", text = new { en = "Hello", de = "Hallo" } })));
        var widget = await published.Content.ReadFromJsonAsync<JsonElement>();
        using var ended = await client.SendAsync(Request(HttpMethod.Post, $"/lookout/v1/widgets/notices/notices/{id}/end", "Zo%C3%AB"));

        published.StatusCode.Should().Be(HttpStatusCode.OK);
        LookoutShape.AssertWidget(widget, nested: false);
        widget.GetProperty("notices").EnumerateArray().Single(n => n.GetProperty("id").GetString() == id).GetProperty("state").GetString().Should().Be("live");
        fake.Notices.Select(n => n.Id).Should().Contain(id);
        (await database.ScalarAsync<string>("emberops", "SELECT actor FROM NoticeLog WHERE action = 'publish' AND notice->>'id' = $1", id)).Should().Be("Zoë");
        ended.StatusCode.Should().Be(HttpStatusCode.OK);
        (await database.ScalarAsync<string>("emberops", "SELECT actor FROM NoticeLog WHERE action = 'end' AND notice->>'id' = $1", id)).Should().Be("Zoë");
    }

    [Fact]
    public async Task Ending_or_deleting_a_notice_without_an_actor_is_a_400_and_logs_nothing()
    {
        var fake = new FakeNoticesUpstream(NoticesToken);
        using var client = Hub(Factory(notices: fake));
        var id = $"lookout-{Guid.NewGuid():N}";

        using var end = await client.SendAsync(Request(HttpMethod.Post, $"/lookout/v1/widgets/notices/notices/{id}/end", actor: null));
        using var delete = await client.SendAsync(Request(HttpMethod.Delete, $"/lookout/v1/widgets/notices/notices/{id}", actor: null));

        end.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        delete.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.Publishes.Should().Be(0);
    }

    [Fact]
    public async Task A_malformed_json_body_is_a_400_problem_not_an_empty_400()
    {
        using var client = Hub(Factory(notices: new FakeNoticesUpstream(NoticesToken)));

        using var response = await client.SendAsync(Request(HttpMethod.Post, "/lookout/v1/widgets/notices/notices", "tester", Json("{not json")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Invalid JSON body");
    }

    [Theory]
    [InlineData("GET", "/lookout/v1/nope")]
    [InlineData("POST", "/lookout/v1/widgets/notices/notices")]
    public async Task The_peer_check_comes_before_the_key_check_and_both_before_routing(string method, string path)
    {
        using var wrongPeer = await Hub(ip: OpsApiFactory.UntrustedIp).SendAsync(Request(new HttpMethod(method), path, content: Json("{}")));
        using var wrongKey = await Hub(bearer: "synthetic-lookout-key-for-tests-0123456780").SendAsync(Request(new HttpMethod(method), path, content: Json("{}")));

        wrongPeer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        wrongKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
