using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Ember.Api.Tests.Contract;

/// <summary>
/// The request/response pairs of one area as contract shapes, keyed by a stable exchange name: method, route template, request
/// body shape, status, content type, the headers that are part of the contract and the body shape (problem bodies as sent).
/// </summary>
internal sealed partial class Exchanges
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NewLine = "\n",
    };

    [GeneratedRegex(@"/\d+(?=/|\?|$)")]
    private static partial Regex IdSegment();

    [GeneratedRegex(@"^(\.AspNetCore\.(?:Antiforgery|Correlation)\.).+")]
    private static partial Regex AntiforgeryCookie();

    [GeneratedRegex(@"\b(state|code)=[^&]+")]
    private static partial Regex RandomQuery();

    /// <summary>Where the test host's fake Discord stands in for discord.com/oauth2/authorize.</summary>
    private const string FakeAuthorize = "/test/discord/authorize";

    private readonly SortedDictionary<string, JsonObject> _byName = new(StringComparer.Ordinal);

    /// <summary>Records the answered <paramref name="call"/> under <paramref name="name"/> for the endpoint <paramref name="route"/> (its pattern, as in routes.txt) and returns the response.</summary>
    public async Task<HttpResponseMessage> Record(string name, string route, Task<HttpResponseMessage> call)
    {
        var response = await call;
        _byName.ContainsKey(name).Should().BeFalse("exchange names are unique ({0})", name);

        var exchange = new JsonObject { ["method"] = response.RequestMessage!.Method.Method, ["route"] = route };
        if (response.RequestMessage.Content is { } content && await content.ReadAsStringAsync() is { Length: > 0 } sent)
            exchange["request"] = content.Headers.ContentType?.MediaType == "application/json"
                ? Shapes.Of(JsonDocument.Parse(sent).RootElement)
                : JsonValue.Create(content.Headers.ContentType?.MediaType);
        exchange["status"] = (int)response.StatusCode;
        exchange["contentType"] = response.Content.Headers.ContentType?.MediaType;
        exchange["headers"] = Headers(response);
        if (await Body(response) is { } body) exchange["body"] = body;

        _byName[name] = exchange;
        return response;
    }

    public void Verify(string file)
    {
        var actual = new JsonObject();
        foreach (var (name, exchange) in _byName) actual[name] = exchange.DeepClone();

        if (Golden.Writing)
        {
            Golden.Write(file, actual.ToJsonString(Indented) + "\n");
            return;
        }

        var expected = JsonNode.Parse(Golden.Read(file) ?? throw new InvalidOperationException($"{file} is missing; run with EMBER_WRITE_FIXTURES=1."))!.AsObject();
        var differences = new List<string>();
        foreach (var name in expected.Select(p => p.Key).Union(_byName.Keys).Order(StringComparer.Ordinal))
            Diff(name, expected[name], actual[name], differences);

        differences.Should().BeEmpty("{0} differs from the app (golden vs app). Run with EMBER_WRITE_FIXTURES=1 to accept.", file);
    }

    private static void Diff(string path, JsonNode? expected, JsonNode? actual, List<string> differences)
    {
        switch (expected, actual)
        {
            case (null, null):
                return;
            case (JsonObject e, JsonObject a):
                foreach (var key in e.Select(p => p.Key).Union(a.Select(p => p.Key)))
                    Diff($"{path}.{key}", e[key], a[key], differences);
                return;
            default:
                if (expected?.ToJsonString() != actual?.ToJsonString())
                    differences.Add($"{path}: {expected?.ToJsonString() ?? "(missing)"} vs {actual?.ToJsonString() ?? "(missing)"}");
                return;
        }
    }

    private static JsonObject Headers(HttpResponseMessage response)
    {
        var headers = new JsonObject();
        if (response.Headers.Location is { } location) headers["Location"] = Location(location.OriginalString);
        if (response.Headers.RetryAfter is not null) headers["Retry-After"] = "present";
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            headers["Set-Cookie"] = new JsonArray(cookies.Select(c => AntiforgeryCookie().Replace(c.Split('=')[0], "$1<suffix>")).Distinct().Order(StringComparer.Ordinal)
                .Select(name => (JsonNode)name).ToArray());
        if (response.Content.Headers.ContentDisposition is { } disposition) headers["Content-Disposition"] = disposition.ToString();
        return headers;
    }

    /// <summary>A redirect target with database ids and the random OAuth <c>state</c> and <c>code</c> values replaced.</summary>
    private static string Location(string location)
    {
        if (location.StartsWith(FakeAuthorize, StringComparison.Ordinal)) location = "<discord authorize>" + location[FakeAuthorize.Length..];
        return RandomQuery().Replace(IdSegment().Replace(Shapes.Scrub(location), "/{id}"), m => $"{m.Groups[1].Value}=<{m.Groups[1].Value}>");
    }

    private static async Task<JsonNode?> Body(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (text.Length == 0) return null;

        var type = response.Content.Headers.ContentType?.MediaType;
        if (type == "application/problem+json") return Shapes.Problem(JsonDocument.Parse(text).RootElement);
        if (type == "application/json") return Shapes.Of(JsonDocument.Parse(text).RootElement);
        return JsonValue.Create(text.Split('\n')[0].TrimEnd('\r'));
    }

    /// <summary>The <c>(method, route)</c> of each exchange in a responses golden.</summary>
    public static IEnumerable<(string Method, string Route)> Routes(string file)
        => JsonNode.Parse(Golden.Read(file)!)!.AsObject().Select(p => (p.Value!["method"]!.GetValue<string>(), p.Value["route"]!.GetValue<string>()));
}
