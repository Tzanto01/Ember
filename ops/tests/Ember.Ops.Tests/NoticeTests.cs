using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ember.Ops.TestHost;
using FluentAssertions;

namespace Ember.Ops.Tests;

public sealed class NoticeRulesTests
{
    private static Notice Valid(string id = "a", string en = "Hello", string de = "Hallo", string? from = null, string? until = null) =>
        new(id, "info", en, de, from, until);

    [Fact]
    public void Accepts_a_valid_document()
    {
        NoticeRules.Validate([Valid("a"), Valid("b", from: "2026-11-18T10:00:00Z", until: "2026-11-18T11:00:00Z")]).Should().BeNull();
    }

    [Fact]
    public void Accepts_280_emoji_which_are_560_utf16_units()
    {
        NoticeRules.Validate([Valid(en: string.Concat(Enumerable.Repeat("\U0001F600", 280)))]).Should().BeNull();
    }

    [Fact]
    public void Rejects_281_code_points()
    {
        NoticeRules.Validate([Valid(en: new string('x', 281))]).Should().NotBeNull();
    }

    [Fact]
    public void Rejects_eleven_notices()
    {
        NoticeRules.Validate(Enumerable.Range(0, 11).Select(i => Valid($"n{i}")).ToList()).Should().NotBeNull();
    }

    [Fact]
    public void Rejects_a_duplicate_id() => NoticeRules.Validate([Valid("a"), Valid("a")]).Should().NotBeNull();

    [Fact]
    public void Rejects_an_unknown_kind() => NoticeRules.Validate([Valid() with { Kind = "warning" }]).Should().NotBeNull();

    [Fact]
    public void Rejects_whitespace_only_german_text() => NoticeRules.Validate([Valid(de: "  \t ")]).Should().NotBeNull();

    [Fact]
    public void Rejects_a_time_without_Z() => NoticeRules.Validate([Valid(until: "2026-11-18T11:00:00+00:00")]).Should().NotBeNull();

    [Theory]
    [InlineData("2026-11-18T10:00:00Z\n")]
    [InlineData("18 Nov 2026 10:00Z")]
    public void Rejects_a_time_the_worker_rejects(string until) => NoticeRules.Validate([Valid(until: until)]).Should().NotBeNull();

    [Fact]
    public void Rejects_equal_from_and_until() =>
        NoticeRules.Validate([Valid(from: "2026-11-18T10:00:00Z", until: "2026-11-18T10:00:00Z")]).Should().NotBeNull();

    [Fact]
    public void Rejects_a_document_over_16_KiB()
    {
        var big = Enumerable.Range(0, 10).Select(i => Valid($"n{i}", en: new string('x', 280), de: new string('y', 280))).ToList();
        NoticeRules.Validate(big).Should().BeNull("10 notices of 280 characters fit");
        NoticeRules.Validate(big.Select(n => n with { En = string.Concat(Enumerable.Repeat("\U0001F600", 280)), De = string.Concat(Enumerable.Repeat("\U0001F600", 280)) }).ToList())
            .Should().NotBeNull();
    }
}

[Collection(V2Collection.Name)]
public sealed class NoticeTests(V2DatabaseFixture database) : IDisposable
{
    private const string Token = "synthetic-test-token";
    private const string Actor = "Tester";
    private const string Widget = "/lookout/v1/widgets/notices/notices";

    private readonly List<IDisposable> _disposables = [];

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    private (HttpClient Client, FakeNoticesUpstream Fake) Start(string? upstreamToken = null)
    {
        var fake = new FakeNoticesUpstream(upstreamToken ?? Token);
        return (Client(fake), fake);
    }

    private HttpClient Client(HttpMessageHandler upstream, string? noticesToken = Token + "\n")
    {
        var factory = new OpsApiFactory(database, notices: upstream, noticesToken: noticesToken, hubAddress: OpsApiFactory.HubIp, lookoutKey: OpsApiFactory.LookoutKey);
        var client = factory.ClientFrom(OpsApiFactory.HubIp);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {OpsApiFactory.LookoutKey}");
        _disposables.Add(client);
        _disposables.Add(factory);
        return client;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Lookout-Actor", Actor);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, object body) => SendAsync(client, HttpMethod.Post, Widget, body);

    private static Task<HttpResponseMessage> EndAsync(HttpClient client, string id) => SendAsync(client, HttpMethod.Post, $"{Widget}/{id}/end");

    private static object Body(string id, string? from = null, string? until = null) =>
        new { id, kind = "info", text = new { en = $"en {id}", de = $"de {id}" }, showFrom = from, showUntil = until };

    private Task<long> LogCountAsync(string action, string id) =>
        database.ScalarAsync<long>("emberops", "SELECT count(*) FROM NoticeLog WHERE action = $1 AND notice->>'id' = $2", action, id);

    private static async Task<JsonElement> ViewAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> ListAsync(HttpClient client) =>
        LookoutShape.Widget(await client.GetFromJsonAsync<JsonElement>("/lookout/v1/pages/notices"), "notices");

    private static int Count(JsonElement view) => view.GetProperty("notices").GetArrayLength();

    private static string StateOf(JsonElement view, string id) =>
        view.GetProperty("notices").EnumerateArray().Single(n => n.GetProperty("id").GetString() == id).GetProperty("state").GetString()!;

    private static string UniqueId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    [Fact]
    public async Task A_second_publish_within_90_seconds_keeps_the_first_even_when_the_edge_still_serves_the_old_document()
    {
        var (client, fake) = Start();
        var a = UniqueId("a");
        var b = UniqueId("b");
        (await PublishAsync(client, Body(a))).StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Notices = [];

        database.Time.Advance(TimeSpan.FromSeconds(60));
        (await PublishAsync(client, Body(b))).StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Notices.Select(n => n.Id).Should().BeEquivalentTo([a, b]);

        var c = UniqueId("c");
        fake.Notices = [];
        database.Time.Advance(TimeSpan.FromSeconds(20));
        (await PublishAsync(client, Body(c))).StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Notices.Select(n => n.Id).Should().BeEquivalentTo([a, b, c]);
    }

    [Fact]
    public async Task A_delete_by_another_writer_is_listed_once_the_edge_has_served_our_write()
    {
        var (client, fake) = Start();
        await PublishAsync(client, Body(UniqueId("gone")));
        Count(await ListAsync(client)).Should().Be(1);

        fake.Notices = [];

        Count(await ListAsync(client)).Should().Be(0);
    }

    [Fact]
    public async Task An_odd_id_is_refused_for_a_new_notice_but_an_existing_one_does_not_block_writes()
    {
        var (client, fake) = Start();
        var odd = "maintenance 2026";
        fake.Notices = [new Notice(odd, "info", "External", "Extern")];

        (await PublishAsync(client, Body("bad id"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.Publishes.Should().Be(0);

        var id = UniqueId("ok");
        (await PublishAsync(client, Body(id))).StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Notices.Select(n => n.Id).Should().BeEquivalentTo([odd, id]);
    }

    [Fact]
    public async Task Another_writers_change_within_90_seconds_is_listed_and_kept_by_the_next_publish()
    {
        var (client, fake) = Start();
        var mine = UniqueId("mine");
        var theirs = UniqueId("theirs");
        await PublishAsync(client, Body(mine));
        fake.Notices = [new Notice(theirs, "info", "External", "Extern")];

        var list = await ListAsync(client);
        list.GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("id").GetString()).Should().BeEquivalentTo([theirs]);

        var next = UniqueId("next");
        (await PublishAsync(client, Body(next))).StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Notices.Select(n => n.Id).Should().BeEquivalentTo([theirs, next]);
    }

    [Fact]
    public async Task After_the_stale_read_window_the_list_follows_the_upstream_document()
    {
        var (client, fake) = Start();
        await PublishAsync(client, Body(UniqueId("stale")));
        fake.Notices = [];

        Count(await ListAsync(client)).Should().Be(1);
        database.Time.Advance(NoticeStore.StaleReadWindow + TimeSpan.FromSeconds(1));
        Count(await ListAsync(client)).Should().Be(0);
    }

    [Fact]
    public async Task Ending_a_live_notice_sets_show_until_to_now_in_seconds_format()
    {
        var (client, fake) = Start();
        var id = UniqueId("live");
        await PublishAsync(client, Body(id));
        database.Time.Advance(TimeSpan.FromSeconds(5.5));
        var expected = NoticeRules.FormatTime(database.Time.GetUtcNow());

        var response = await EndAsync(client, id);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        StateOf(await ViewAsync(response), id).Should().Be("ended");
        fake.Notices.Single(n => n.Id == id).ShowUntil.Should().Be(expected).And.MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$");
        (await LogCountAsync("end", id)).Should().Be(1);
    }

    [Fact]
    public async Task Ending_a_scheduled_notice_drops_its_show_from()
    {
        var (client, fake) = Start();
        var id = UniqueId("sched");
        var from = NoticeRules.FormatTime(database.Time.GetUtcNow().AddHours(1));
        var until = NoticeRules.FormatTime(database.Time.GetUtcNow().AddHours(2));
        await PublishAsync(client, Body(id, from, until));

        var response = await EndAsync(client, id);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = fake.Notices.Single(n => n.Id == id);
        stored.ShowFrom.Should().BeNull();
        stored.ShowUntil.Should().Be(NoticeRules.FormatTime(database.Time.GetUtcNow()));
    }

    [Fact]
    public async Task Ending_an_already_ended_notice_is_a_conflict_and_publishes_nothing()
    {
        var (client, fake) = Start();
        var id = UniqueId("ended");
        await PublishAsync(client, Body(id));
        await EndAsync(client, id);
        var publishes = fake.Publishes;

        var response = await EndAsync(client, id);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        fake.Publishes.Should().Be(publishes);
    }

    [Fact]
    public async Task Ending_an_unknown_notice_is_404_and_publishes_nothing()
    {
        var (client, fake) = Start();

        var response = await EndAsync(client, "nope");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        fake.Publishes.Should().Be(0);
    }

    [Fact]
    public async Task Deleting_removes_the_notice_upstream_and_logs_it()
    {
        var (client, fake) = Start();
        var id = UniqueId("del");
        await PublishAsync(client, Body(id));

        var response = await SendAsync(client, HttpMethod.Delete, $"{Widget}/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Notices.Should().NotContain(n => n.Id == id);
        (await LogCountAsync("delete", id)).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_an_unknown_notice_is_404_and_publishes_nothing()
    {
        var (client, fake) = Start();

        (await SendAsync(client, HttpMethod.Delete, $"{Widget}/nope")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        fake.Publishes.Should().Be(0);
    }

    [Fact]
    public async Task An_upstream_400_detail_passes_through_and_nothing_is_logged()
    {
        var client = Client(new StubUpstream(HttpStatusCode.BadRequest, """{"detail":"synthetic rule"}"""));
        var id = UniqueId("rej");

        var response = await PublishAsync(client, Body(id));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ViewAsync(response)).GetProperty("detail").GetString().Should().Be("synthetic rule");
        (await LogCountAsync("publish", id)).Should().Be(0);
    }

    [Fact]
    public async Task A_refused_publish_token_is_a_502_and_nothing_is_logged()
    {
        var (client, _) = Start(upstreamToken: "some-other-token");
        var id = UniqueId("tok");

        var response = await PublishAsync(client, Body(id));

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await LogCountAsync("publish", id)).Should().Be(0);
    }

    [Fact]
    public async Task Without_a_token_file_the_list_is_read_only_and_publishing_is_503()
    {
        var fake = new FakeNoticesUpstream(Token);
        var client = Client(fake, noticesToken: null);

        var list = await ListAsync(client);
        var response = await PublishAsync(client, Body(UniqueId("none")));

        list.GetProperty("publishing").GetBoolean().Should().BeFalse();
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fake.Publishes.Should().Be(0);
    }

    private sealed class StubUpstream(HttpStatusCode putStatus, string putBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Method == HttpMethod.Put
                ? new HttpResponseMessage(putStatus) { Content = new StringContent(putBody, Encoding.UTF8, "application/problem+json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"notices":[]}""", Encoding.UTF8, "application/json") });
    }
}
