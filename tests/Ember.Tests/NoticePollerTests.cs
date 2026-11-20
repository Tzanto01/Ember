using System.Net;
using System.Text.Json.Nodes;
using Ember.Bot.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

public class NoticePollerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private const string TwoNotices = """
        {
          "notices": [
            { "id": "past", "kind": "info", "showUntil": "2026-11-18T11:00:00Z", "en": "Over", "de": "Vorbei" },
            { "id": "now", "kind": "maintenance", "showFrom": "2026-11-18T11:00:00Z", "showUntil": "2026-11-18T13:00:00Z", "en": "Ember is resting", "de": "Ember ruht", "extra": { "x": 1 } }
          ],
          "version": 2
        }
        """;

    private static NoticePoller Poller(FakeTimeProvider time, params HttpResponseMessage[] responses)
        => new(new QueuedHttp(responses), new ConfigurationBuilder().Build(), time, NullLogger<NoticePoller>.Instance);

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static Notice Window(string id, DateTimeOffset? from, DateTimeOffset? until) => new(id, from, until, id);

    [Fact]
    public void Active_notice_is_the_first_whose_window_contains_now_ignoring_unknown_fields()
    {
        var notices = NoticePoller.Parse(TwoNotices)!;

        NoticePoller.Select(notices, Now)!.Id.Should().Be("now");
    }

    [Fact]
    public void The_shared_notices_document_ops_publishes_parses_into_its_notices()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(RulesFixtureTests.RepoRoot(), "docs", "contracts", "fixtures", "notices.json")))!;
        var published = new JsonObject { ["notices"] = fixture["notices"]!.DeepClone() }.ToJsonString();

        var notices = NoticePoller.Parse(published);

        notices.Should().Equal(
            new Notice("maintenance-2027-03-20", new DateTimeOffset(2027, 3, 20, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 3, 20, 23, 30, 0, TimeSpan.Zero),
                "Ember is resting for an update tonight from 22:00 UTC. Your check-ins will wait for you."),
            new Notice("journal.moods_v2", null, null, "New: pick your own moods in the journal 🌱"),
            new Notice("spring-tips", null, new DateTimeOffset(2027, 4, 1, 0, 0, 0, TimeSpan.Zero), "Small steps count. Try a low day when a day is hard."),
            new Notice("fireside-launch", new DateTimeOffset(2027, 4, 2, 8, 0, 0, TimeSpan.Zero), null, "Fireside is here: share your streaks with a few friends."));
    }

    [Fact]
    public void Window_starts_inclusive_and_ends_exclusive()
    {
        var notice = Window("n", Now, Now.AddHours(1));

        NoticePoller.Select([notice], Now).Should().Be(notice);
        NoticePoller.Select([notice], Now.AddHours(1)).Should().BeNull();
        NoticePoller.Select([notice], Now.AddTicks(-1)).Should().BeNull();
    }

    [Fact]
    public void Notice_without_a_window_is_always_active_and_the_earlier_entry_wins()
    {
        NoticePoller.Select([Window("open", null, null), Window("later", null, null)], Now)!.Id.Should().Be("open");
        NoticePoller.Select([Window("from", Now.AddMinutes(-1), null)], Now)!.Id.Should().Be("from");
        NoticePoller.Select([Window("future", Now.AddMinutes(1), null)], Now).Should().BeNull();
    }

    [Fact]
    public void Entries_without_id_or_English_text_are_skipped()
    {
        var notices = NoticePoller.Parse("""{ "notices": [ { "en": "No id" }, { "id": "a", "de": "Nur Deutsch" }, { "id": "b", "en": "  Kept  " } ] }""")!;

        notices.Should().ContainSingle().Which.Should().Be(new Notice("b", null, null, "Kept"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "notices": [] }""")]
    public void Missing_or_empty_list_means_no_notice(string json)
    {
        NoticePoller.Parse(json).Should().BeEmpty();
    }

    [Theory]
    [InlineData("<html>Bad gateway</html>")]
    [InlineData("null")]
    [InlineData("""{ "notices": "soon" }""")]
    public void Invalid_file_is_rejected(string json)
    {
        NoticePoller.Parse(json).Should().BeNull();
    }

    [Fact]
    public void One_malformed_entry_does_not_hide_a_good_one()
    {
        var notices = NoticePoller.Parse("""
            { "notices": [
              { "id": "bad-date", "en": "x", "showFrom": "tomorrow" },
              { "id": "number-date", "en": "x", "showUntil": 1763467200 },
              { "id": 7, "en": "numeric id" },
              "not an object",
              { "id": "good", "en": "Ember is resting", "showFrom": null, "showUntil": "2026-11-18T13:00:00Z" }
            ] }
            """)!;

        NoticePoller.Select(notices, Now).Should().Be(new Notice("good", null, new DateTimeOffset(2026, 11, 18, 13, 0, 0, TimeSpan.Zero), "Ember is resting"));
    }

    [Fact]
    public async Task A_404_clears_an_active_notice()
    {
        var poller = Poller(new FakeTimeProvider(Now), Ok("""{ "notices": [ { "id": "a", "en": "Resting" } ] }"""), new HttpResponseMessage(HttpStatusCode.NotFound));

        await poller.PollAsync("https://ember.example/notices.json", CancellationToken.None);
        poller.Active!.Id.Should().Be("a");

        await poller.PollAsync("https://ember.example/notices.json", CancellationToken.None);
        poller.Active.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    [InlineData(HttpStatusCode.Gone, null)]
    [InlineData(HttpStatusCode.OK, "<html>Bad gateway</html>")]
    public async Task Any_failed_poll_clears_an_active_notice(HttpStatusCode status, string? body)
    {
        var failure = new HttpResponseMessage(status) { Content = new StringContent(body ?? "") };
        var poller = Poller(new FakeTimeProvider(Now), Ok("""{ "notices": [ { "id": "a", "en": "Resting" } ] }"""), failure);

        await poller.PollAsync("https://ember.example/notices.json", CancellationToken.None);
        await poller.PollAsync("https://ember.example/notices.json", CancellationToken.None);

        poller.Active.Should().BeNull();
    }

    [Fact]
    public async Task Network_error_clears_an_active_notice_and_still_raises_Changed()
    {
        var poller = Poller(new FakeTimeProvider(Now), Ok("""{ "notices": [ { "id": "a", "en": "Resting" } ] }"""));
        var changes = 0;
        poller.Changed += () => { changes++; return Task.CompletedTask; };

        await poller.PollAsync("https://ember.example/notices.json", CancellationToken.None);
        await poller.PollAsync("https://ember.example/notices.json", CancellationToken.None);

        poller.Active.Should().BeNull();
        changes.Should().Be(2);
    }

    [Fact]
    public void Warning_is_due_once_per_failure_streak()
    {
        var poller = Poller(new FakeTimeProvider(Now));

        poller.Apply(null).Should().BeTrue();
        poller.Apply(null).Should().BeFalse();
        poller.Apply([]).Should().BeFalse();
        poller.Apply(null).Should().BeTrue();
    }

    [Fact]
    public void Status_text_is_the_English_text_cut_to_128_characters()
    {
        NoticePoller.StatusText(new Notice("a", null, null, "Ember is resting")).Should().Be("Ember is resting");
        NoticePoller.StatusText(new Notice("a", null, null, new string('x', 128))).Should().HaveLength(128);
        NoticePoller.StatusText(new Notice("a", null, null, new string('x', 200))).Should().Be(new string('x', 128));
    }

    [Fact]
    public void Status_text_does_not_split_an_emoji_at_the_cut()
    {
        var text = new string('x', 127) + "🔥 more";

        NoticePoller.StatusText(new Notice("a", null, null, text)).Should().Be(new string('x', 127));
    }

    [Fact]
    public void Notices_url_prefers_the_explicit_setting_and_falls_back_to_the_web_origin()
    {
        static IConfiguration Config(params (string Key, string Value)[] values)
            => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

        NoticePoller.NoticesUrl(Config(("Ember:WebOrigin", "https://ember.example/"))).Should().Be("https://ember.example/notices.json");
        NoticePoller.NoticesUrl(Config(("Ember:WebOrigin", "https://ember.example"), ("Ember:NoticesUrl", "https://notices.example/n.json")))
            .Should().Be("https://notices.example/n.json");
        NoticePoller.NoticesUrl(Config()).Should().BeNull();
    }

    private sealed class QueuedHttp(HttpResponseMessage[] responses) : IHttpClientFactory
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public HttpClient CreateClient(string name) => new(new Handler(_responses));

        private sealed class Handler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => responses.TryDequeue(out var response) ? Task.FromResult(response) : throw new HttpRequestException("Connection refused");
        }
    }
}
