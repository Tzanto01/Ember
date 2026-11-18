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

    private static NoticePoller Poller(FakeTimeProvider time)
        => new(new NoHttp(), new ConfigurationBuilder().Build(), time, NullLogger<NoticePoller>.Instance);

    private static Notice Window(string id, DateTimeOffset? from, DateTimeOffset? until) => new(id, from, until, id);

    [Fact]
    public void Active_notice_is_the_first_whose_window_contains_now_ignoring_unknown_fields()
    {
        var notices = NoticePoller.Parse(TwoNotices)!;

        NoticePoller.Select(notices, Now)!.Id.Should().Be("now");
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
    [InlineData("""{ "notices": [ { "id": "a", "en": "x", "showFrom": "tomorrow" } ] }""")]
    public void Invalid_file_is_rejected(string json)
    {
        NoticePoller.Parse(json).Should().BeNull();
    }

    [Fact]
    public void Failed_poll_keeps_the_last_notice_for_30_minutes_then_clears_it()
    {
        var time = new FakeTimeProvider(Now);
        var poller = Poller(time);
        poller.Apply("""{ "notices": [ { "id": "a", "en": "Resting" } ] }""", time.GetUtcNow());

        time.Advance(TimeSpan.FromMinutes(30));
        poller.Apply(null, time.GetUtcNow()).Should().BeTrue();
        poller.Apply("not json", time.GetUtcNow()).Should().BeFalse();
        poller.Active!.Id.Should().Be("a");

        time.Advance(TimeSpan.FromMinutes(1));
        poller.Apply(null, time.GetUtcNow());
        poller.Active.Should().BeNull();
    }

    [Fact]
    public void Successful_poll_ends_the_failure_streak_so_the_next_failure_warns_again()
    {
        var time = new FakeTimeProvider(Now);
        var poller = Poller(time);

        poller.Apply(null, Now).Should().BeTrue();
        poller.Apply("""{ "notices": [] }""", Now).Should().BeFalse();
        poller.Apply(null, Now).Should().BeTrue();
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

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP in unit tests.");
    }
}
