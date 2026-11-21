using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ember.Api.Tests.Contract;

/// <summary>
/// A JSON value as its contract shape: objects as sorted key to shape, arrays as one merged element shape, primitives as
/// <c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>, <c>null</c> or a wire format (<c>date</c>, <c>time</c>,
/// <c>datetime</c>, <c>guid</c>). Short token-like strings are kept as <c>enum(value)</c> since the values are part of the contract, except under <see cref="FreeTextKeys"/>.
/// A key missing from some array elements gets a <c>?</c> suffix; <c>a|b</c> joins the shapes of one position that differ.
/// </summary>
internal static partial class Shapes
{
    /// <summary>Keys whose strings are free text, user-chosen or random, or change with releases: shown as <c>string</c>. Every other short token-like string is kept as its value.</summary>
    private static readonly HashSet<string> FreeTextKeys =
    [
        "name", "label", "text", "title", "description", "headline", "footer", "renamedFrom", "habitNames", "email", "password",
        "key", "code", "sharedKey", "authenticatorUri", "accessToken", "refreshToken", "recoveryCodes", "recoveryCode",
        "twoFactorCode", "twoFactorRecoveryCode", "token", "verifier", "challenge", "signupKey", "userId", "installId", "endpoint",
        "iv", "ct", "salt", "wrapped", "encrypted", "emoji", "policyVersion", "currentPolicyVersion", "consentVersion",
        "timezone", "version", "confirm", "newPassword", "oldPassword", "resetCode", "p256dh", "auth", "refresh", "id", "ref",
        "installation",
    ];

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.:-]{0,23}$")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"^custom:\d+$")]
    private static partial Regex CustomMoodPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^\d{2}:\d{2}$")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2}$")]
    private static partial Regex TimeWithSecondsPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$")]
    private static partial Regex DateTimePattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2})?$")]
    private static partial Regex UnzonedDateTimePattern();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^(browser|phone)-\d+$")]
    private static partial Regex DestinationIdPattern();

    [GeneratedRegex(@"t-[0-9a-f]{32}@example\.test")]
    private static partial Regex EmailPattern();

    /// <summary>Random test data in a text (account ids, generated email addresses) replaced by placeholders.</summary>
    public static string Scrub(string text) => EmailPattern().Replace(GuidPattern().Replace(text, "<guid>"), "<email>");

    public static JsonNode Of(JsonElement element, string? key = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    obj[property.Name] = Of(property.Value, property.Name);
                return obj;
            case JsonValueKind.Array:
                var items = element.EnumerateArray().Select(item => Of(item, key)).ToList();
                return items.Count == 0 ? new JsonArray() : new JsonArray(items.Aggregate(Merge));
            case JsonValueKind.String:
                return JsonValue.Create(StringKind(element.GetString()!, key));
            case JsonValueKind.Number:
                return JsonValue.Create(element.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') >= 0 ? "number" : "integer");
            case JsonValueKind.True:
            case JsonValueKind.False:
                return JsonValue.Create("boolean");
            default:
                return JsonValue.Create("null");
        }
    }

    /// <summary>A problem response body as sent, without the per-request <c>traceId</c>.</summary>
    public static JsonNode Problem(JsonElement element)
    {
        var obj = new JsonObject();
        foreach (var property in element.EnumerateObject().Where(p => p.Name != "traceId").OrderBy(p => p.Name, StringComparer.Ordinal))
            obj[property.Name] = property.Name == "errors" && property.Value.ValueKind == JsonValueKind.Object
                ? SortedByKey(JsonNode.Parse(Scrub(property.Value.GetRawText()))!.AsObject())
                : JsonNode.Parse(Scrub(property.Value.GetRawText()));
        return obj;
    }

    private static JsonObject SortedByKey(JsonObject obj)
    {
        var sorted = new JsonObject();
        foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal).ToList()) sorted[key] = value!.DeepClone();
        return sorted;
    }

    private static string StringKind(string value, string? key)
    {
        if (Guid.TryParse(value, out _) && GuidPattern().IsMatch(value)) return "guid";
        if (DestinationIdPattern().Match(value) is { Success: true } destination) return $"{destination.Groups[1].Value}-{{n}}";
        if (DatePattern().IsMatch(value)) return "date";
        if (TimePattern().IsMatch(value)) return "time";
        if (TimeWithSecondsPattern().IsMatch(value)) return "time-with-seconds";
        if (DateTimePattern().IsMatch(value)) return "datetime";
        if (UnzonedDateTimePattern().IsMatch(value)) return "datetime-without-Z";
        if (CustomMoodPattern().IsMatch(value)) return "enum(custom:{n})";
        return key is not null && !FreeTextKeys.Contains(key) && TokenPattern().IsMatch(value) ? $"enum({value})" : "string";
    }

    private static JsonNode Merge(JsonNode a, JsonNode b)
    {
        var (left, leftNull) = Unwrap(a);
        var (right, rightNull) = Unwrap(b);
        var nullable = leftNull || rightNull;
        if (left is null && right is null) return JsonValue.Create("null");
        if (left is null || right is null) return Wrap((left ?? right)!.DeepClone(), true);

        JsonNode merged = (left, right) switch
        {
            (JsonValue x, JsonValue y) => JsonValue.Create(Union(x.GetValue<string>(), y.GetValue<string>())),
            (JsonObject x, JsonObject y) => MergeObjects(x, y),
            (JsonArray x, JsonArray y) => x.Count == 0 ? y.DeepClone() : y.Count == 0 ? x.DeepClone() : new JsonArray(Merge(x[0]!, y[0]!)),
            _ => new JsonObject { ["$oneOf"] = new JsonArray(left.DeepClone(), right.DeepClone()) },
        };
        return Wrap(merged, nullable);
    }

    /// <summary>The shape without its <c>$nullable</c> wrapper and whether it was nullable; a bare <c>null</c> has no shape.</summary>
    private static (JsonNode? Shape, bool Nullable) Unwrap(JsonNode node) => node switch
    {
        JsonValue value when value.GetValue<string>() == "null" => (null, true),
        JsonObject { Count: 1 } wrapper when wrapper.ContainsKey("$nullable") => (wrapper["$nullable"], true),
        _ => (node, false),
    };

    private static JsonNode Wrap(JsonNode shape, bool nullable)
        => !nullable ? shape
            : shape is JsonValue value ? JsonValue.Create(Union(value.GetValue<string>(), "null"))
            : new JsonObject { ["$nullable"] = shape };

    private static JsonObject MergeObjects(JsonObject x, JsonObject y)
    {
        var fields = new SortedDictionary<string, (JsonNode Shape, bool Optional)>(StringComparer.Ordinal);
        foreach (var (side, other) in new[] { (x, y), (y, x) })
            foreach (var (name, shape) in side)
            {
                var key = name.TrimEnd('?');
                var optional = name.EndsWith('?') || !other.Any(p => p.Key.TrimEnd('?') == key);
                fields[key] = fields.TryGetValue(key, out var seen)
                    ? (Merge(seen.Shape, shape!), seen.Optional || optional)
                    : (shape!.DeepClone(), optional);
            }

        var merged = new JsonObject();
        foreach (var (key, field) in fields) merged[field.Optional ? key + "?" : key] = field.Shape;
        return merged;
    }

    private static string Union(string a, string b)
    {
        var tokens = a.Split('|').Concat(b.Split('|')).Distinct().ToList();
        var literals = tokens.Where(t => t.StartsWith("enum(")).ToList();
        if (literals.Count > 1)
        {
            var values = literals.SelectMany(t => t[5..^1].Split(',')).Distinct().Order(StringComparer.Ordinal);
            tokens = [.. tokens.Where(t => !t.StartsWith("enum(")), $"enum({string.Join(",", values)})"];
        }
        return string.Join("|", tokens.Order(StringComparer.Ordinal));
    }
}
