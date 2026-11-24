using System.Text.Json;
using FluentAssertions;

namespace Ember.Ops.Tests;

/// <summary>Asserts the widget shapes of provider-v1.ts on a decoded page.</summary>
public static class LookoutShape
{
    public static readonly string[] PageIds = ["overview", "engagement", "devices", "releases", "health", "notices", "system"];

    public static readonly Dictionary<string, string[]> WidgetIds = new()
    {
        ["overview"] = ["tiles", "new-users", "active-per-day"],
        ["engagement"] = ["tiles", "active-per-day", "active-per-week", "days-active", "retention"],
        ["devices"] = ["platforms", "versions", "adoption"],
        ["releases"] = ["deployed", "apk", "ci"],
        ["health"] = ["api", "requests", "latency", "errors", "slowest"],
        ["notices"] = ["notices"],
        ["system"] = ["services", "database", "disks", "nightly-backup", "backup", "outbox"],
    };

    private static readonly Dictionary<string, string> Types = new()
    {
        ["tiles"] = "stats", ["new-users"] = "bars", ["active-per-day"] = "bars", ["active-per-week"] = "bars", ["days-active"] = "bars", ["retention"] = "stats",
        ["api"] = "stats", ["requests"] = "bars", ["latency"] = "bars", ["errors"] = "bars", ["slowest"] = "stats",
        ["platforms"] = "bars", ["versions"] = "bars", ["adoption"] = "stats",
        ["notices"] = "notices", ["services"] = "status", ["database"] = "stats", ["disks"] = "stats",
        ["nightly-backup"] = "stats", ["backup"] = "stats", ["outbox"] = "stats", ["deployed"] = "stats", ["apk"] = "stats", ["ci"] = "ci",
    };

    public static void AssertPage(JsonElement page, string pageId)
    {
        page.GetProperty("id").GetString().Should().Be(pageId);
        page.GetProperty("title").GetString().Should().NotBeNullOrEmpty();
        page.GetProperty("generatedAt").GetString().Should().EndWith("Z");
        var widgets = page.GetProperty("widgets").EnumerateArray().ToList();
        widgets.Select(w => w.GetProperty("id").GetString()).Should().Equal(WidgetIds[pageId]);
        foreach (var widget in widgets)
        {
            widget.GetProperty("type").GetString().Should().Be(Types[widget.GetProperty("id").GetString()!]);
            AssertWidget(widget, nested: false);
        }
    }

    public static void AssertWidget(JsonElement w, bool nested)
    {
        w.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        w.GetProperty("title").GetString().Should().NotBeNull();
        var type = w.GetProperty("type").GetString();
        switch (type)
        {
            case "stats":
                w.GetProperty("variant").GetString().Should().BeOneOf("tiles", "list");
                foreach (var item in w.GetProperty("items").EnumerateArray())
                {
                    item.GetProperty("label").GetString().Should().NotBeNullOrEmpty();
                    item.TryGetProperty("value", out var value).Should().BeTrue("value is required even when null");
                    AssertV11(item, value);
                }
                break;
            case "bars":
                w.GetProperty("axis").GetString().Should().BeOneOf("day", "week", "category");
                var series = w.GetProperty("series").GetArrayLength();
                series.Should().BeGreaterThan(0);
                series.Should().BeLessThanOrEqualTo(4);
                w.GetProperty("bars").GetArrayLength().Should().BeLessThanOrEqualTo(120);
                foreach (var bar in w.GetProperty("bars").EnumerateArray())
                {
                    bar.GetProperty("label").GetString().Should().NotBeNullOrEmpty();
                    bar.GetProperty("values").GetArrayLength().Should().Be(series);
                }
                if (w.TryGetProperty("better", out var better)) better.GetString().Should().BeOneOf("up", "down");
                break;
            case "status":
                w.TryGetProperty("checkedAt", out _).Should().BeTrue("checkedAt is required even when null");
                foreach (var item in w.GetProperty("items").EnumerateArray())
                    item.GetProperty("state").GetString().Should().BeOneOf("ok", "bad");
                break;
            case "ci":
                w.GetProperty("runs").ValueKind.Should().Be(JsonValueKind.Array);
                break;
            case "list":
                w.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
                break;
            case "panel":
                nested.Should().BeFalse("panels do not nest");
                foreach (var child in w.GetProperty("children").EnumerateArray())
                {
                    child.GetProperty("type").GetString().Should().BeOneOf("stats", "bars", "status", "ci", "list");
                    AssertWidget(child, nested: true);
                }
                break;
            case "notices":
                nested.Should().BeFalse();
                w.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()).Should().Equal("info", "maintenance");
                w.GetProperty("maxLength").GetInt32().Should().Be(NoticeRules.MaxTextLength);
                w.GetProperty("maxNotices").GetInt32().Should().Be(NoticeRules.MaxNotices);
                w.GetProperty("publishing").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
                w.GetProperty("notices").ValueKind.Should().Be(JsonValueKind.Array);
                break;
            default:
                throw new Xunit.Sdk.XunitException($"unknown widget type {type}");
        }
    }

    /// <summary>The optional v1.1 fields have their shapes, and a suppressed value carries none that would reveal the count.</summary>
    private static void AssertV11(JsonElement item, JsonElement value)
    {
        if (item.TryGetProperty("trend", out var trend))
        {
            trend.GetArrayLength().Should().BeInRange(1, 60);
            trend.EnumerateArray().Should().OnlyContain(t => t.ValueKind == JsonValueKind.Number);
        }
        if (item.TryGetProperty("delta", out var delta))
        {
            delta.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Number);
            delta.GetProperty("vs").GetString().Should().NotBeNullOrEmpty();
            if (delta.TryGetProperty("better", out var better)) better.GetString().Should().BeOneOf("up", "down");
        }
        if (item.TryGetProperty("max", out var max)) max.ValueKind.Should().Be(JsonValueKind.Number);
        if (item.TryGetProperty("goal", out var goal))
            goal.EnumerateObject().Select(p => p.Name).Should().NotBeEmpty().And.BeSubsetOf(["below", "above"]);
        if (value.ValueKind == JsonValueKind.String && value.GetString() == "<3")
            item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["label", "value"]);
    }

    public static JsonElement Widget(JsonElement page, string id) => page.GetProperty("widgets").EnumerateArray().Single(w => w.GetProperty("id").GetString() == id);

    public static JsonElement Item(JsonElement stats, string label) => stats.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("label").GetString() == label);
}
