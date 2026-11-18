using System.Text.Json;
using FluentAssertions;

namespace Ember.Ops.Tests;

/// <summary>Asserts the widget shapes of provider-v1.ts on a decoded page.</summary>
public static class LookoutShape
{
    public static readonly string[] PageIds = ["overview", "reminders", "feedback", "notices", "system"];

    public static readonly Dictionary<string, string[]> WidgetIds = new()
    {
        ["overview"] = ["tiles", "new-users", "check-ins", "journal", "low-days", "grace", "fireside", "let-go"],
        ["reminders"] = ["tiles", "open", "sent", "discord", "second", "push", "overdue"],
        ["feedback"] = ["feedback"],
        ["notices"] = ["notices"],
        ["system"] = ["services", "database", "disks", "backup", "deployed", "apk", "live-notice", "ci"],
    };

    private static readonly Dictionary<string, string> Types = new()
    {
        ["tiles"] = "stats", ["new-users"] = "bars", ["check-ins"] = "bars", ["journal"] = "bars", ["low-days"] = "bars", ["grace"] = "panel",
        ["fireside"] = "panel", ["let-go"] = "panel", ["open"] = "bars", ["sent"] = "bars", ["discord"] = "stats", ["second"] = "panel", ["push"] = "stats",
        ["overdue"] = "stats", ["feedback"] = "board", ["notices"] = "notices", ["services"] = "status", ["database"] = "stats", ["disks"] = "stats",
        ["backup"] = "stats", ["deployed"] = "stats", ["apk"] = "stats", ["live-notice"] = "list", ["ci"] = "ci",
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
                    item.TryGetProperty("value", out _).Should().BeTrue("value is required even when null");
                }
                break;
            case "bars":
                w.GetProperty("axis").GetString().Should().BeOneOf("day", "week", "category");
                var series = w.GetProperty("series").GetArrayLength();
                series.Should().BeGreaterThan(0);
                foreach (var bar in w.GetProperty("bars").EnumerateArray())
                {
                    bar.GetProperty("label").GetString().Should().NotBeNullOrEmpty();
                    bar.GetProperty("values").GetArrayLength().Should().Be(series);
                }
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
            case "board":
                nested.Should().BeFalse();
                w.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("id").GetString()).Should().Equal("open", "planned", "implemented", "released", "declined");
                w.GetProperty("comments").GetBoolean().Should().BeTrue();
                foreach (var card in w.GetProperty("cards").EnumerateArray())
                {
                    card.GetProperty("id").ValueKind.Should().Be(JsonValueKind.String);
                    foreach (var name in new[] { "column", "title", "text", "createdAt", "fields", "facets", "imageIds", "commentCount" })
                        card.TryGetProperty(name, out _).Should().BeTrue(name);
                    card.GetProperty("position").ValueKind.Should().Be(JsonValueKind.Number);
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

    public static JsonElement Widget(JsonElement page, string id) => page.GetProperty("widgets").EnumerateArray().Single(w => w.GetProperty("id").GetString() == id);

    public static JsonElement Item(JsonElement stats, string label) => stats.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("label").GetString() == label);
}
