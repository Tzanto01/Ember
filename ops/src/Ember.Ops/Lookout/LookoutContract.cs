using System.Text.Json.Serialization;

namespace Ember.Ops.Lookout;

/// <summary><c>GET /lookout/v1/manifest</c>, Lookout provider contract v1.</summary>
public sealed record Manifest(
    int Contract, string Id, string Name, string Icon,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimeZone,
    IReadOnlyList<PageRef> Pages);

public sealed record PageRef(
    string Id, string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Icon = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RefreshSeconds = null);

/// <summary><c>GET /lookout/v1/summary</c>: one card on the hub's cross-app overview.</summary>
public sealed record Summary(DateTime GeneratedAt, string State, IReadOnlyList<StatItem> Tiles, IReadOnlyList<Alert> Alerts);

public sealed record Alert(string Text, string Tone, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Page = null);

/// <summary><c>GET /lookout/v1/pages/{pageId}</c>.</summary>
public sealed record Page(string Id, string Title, DateTime GeneratedAt, IReadOnlyList<Widget> Widgets);

/// <summary>A widget; the serializer writes its <c>type</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StatsWidget), "stats")]
[JsonDerivedType(typeof(BarsWidget), "bars")]
[JsonDerivedType(typeof(StatusWidget), "status")]
[JsonDerivedType(typeof(CiWidget), "ci")]
[JsonDerivedType(typeof(ListWidget), "list")]
[JsonDerivedType(typeof(PanelWidget), "panel")]
[JsonDerivedType(typeof(NoticesWidget), "notices")]
public abstract record Widget
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Note { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Size { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Unavailable { get; init; }
}

public sealed record StatsWidget : Widget
{
    public required string Variant { get; init; }
    public required IReadOnlyList<StatItem> Items { get; init; }
}

/// <param name="Value">A number, a string or null.</param>
public sealed record StatItem(
    string Label, object? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Format = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tone = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Hint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HintTone = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<double>? Trend = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Delta? Delta = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Max = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Goal? Goal = null);

/// <summary>v1.1: change against a named earlier period, in the unit of the value.</summary>
public sealed record Delta(double Value, string Vs, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Better = null);

/// <summary>v1.1: target for a value, met below <see cref="Below"/> and/or above <see cref="Above"/>.</summary>
public sealed record Goal(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Below = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Above = null);

public sealed record BarsWidget : Widget
{
    public required string Axis { get; init; }
    public required IReadOnlyList<SeriesRef> Series { get; init; }
    public required IReadOnlyList<Bar> Bars { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Better { get; init; }
}

public sealed record SeriesRef(string Label);

public sealed record Bar(string Label, IReadOnlyList<double> Values);

public sealed record StatusWidget : Widget
{
    public required DateTime? CheckedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? StaleAfterSeconds { get; init; }
    public required IReadOnlyList<StatusItem> Items { get; init; }
}

public sealed record StatusItem(string Name, string State, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail = null);

public sealed record CiWidget : Widget
{
    public required IReadOnlyList<CiRun> Runs { get; init; }
}

public sealed record ListWidget : Widget
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Empty { get; init; }
    public required IReadOnlyList<ListItem> Items { get; init; }
}

public sealed record ListItem(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Lines = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Badge>? Badges = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Meta = null);

public sealed record Badge(string Text, string Tone);

/// <summary>Several widgets in one card; children are never panels, boards or notices.</summary>
public sealed record PanelWidget : Widget
{
    public required IReadOnlyList<Widget> Children { get; init; }
}

/// <summary>The notices publisher; every notice write answers with it.</summary>
public sealed record NoticesWidget : Widget
{
    public required bool Publishing { get; init; }
    public required IReadOnlyList<string> Kinds { get; init; }
    public required IReadOnlyList<Language> Languages { get; init; }
    public required int MaxLength { get; init; }
    public required int MaxNotices { get; init; }
    public required IReadOnlyList<NoticeItem> Notices { get; init; }
}

public sealed record Language(string Code, string Label);

/// <summary>A notice with its state: <c>scheduled</c>, <c>live</c> or <c>ended</c>.</summary>
public sealed record NoticeItem(
    string Id, string Kind, IReadOnlyDictionary<string, string> Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ShowFrom,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ShowUntil,
    string State);

/// <summary><c>POST widgets/{noticesId}/notices</c>.</summary>
public sealed record NoticeInput(string? Id, string? Kind, IReadOnlyDictionary<string, string>? Text, string? ShowFrom, string? ShowUntil);
