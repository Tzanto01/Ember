using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Api.Tests;

/// <summary>
/// Auth is opt-in per endpoint (no fallback policy), so every route the app maps is pinned here with its auth requirement:
/// <c>anonymous</c> (no auth metadata), <c>allow-anonymous</c> (explicit <see cref="IAllowAnonymous"/>), <c>authenticated</c>
/// (<see cref="IAuthorizeData"/> without policy, roles or schemes), or the policy, roles and schemes it names.
/// It sees only routed endpoints. Requests answered before routing or outside it are not covered: the Discord OAuth callback (answered
/// by the auth handler), <see cref="AuthEndpoints.HideIdentityManage"/>, static files and <c>release-notes.json</c>, and the metrics
/// listener on its own port (covered by <see cref="ApiMetricsTests"/>). A new <c>app.Map(path, branch)</c> or answering middleware
/// needs its own test.
/// </summary>
public class EndpointAuthInventoryTests(PostgresFixture postgres) : ApiTestBase(postgres), IDisposable
{
    /// <summary>
    /// The reviewed security contract: one line per method and route. Change it only together with the endpoint, after reading the
    /// handler: an anonymous entry must authenticate the caller some other way or be meant for anyone.
    /// </summary>
    private const string Inventory = """
        * /api/{**rest} -> anonymous
        * /health -> anonymous
        DELETE /api/account/merge -> authenticated
        DELETE /api/day-notes/{date} -> authenticated
        DELETE /api/fireside/ask -> authenticated
        DELETE /api/fireside/code -> authenticated
        DELETE /api/fireside/friends/{id:int} -> authenticated
        DELETE /api/habits/{id:int} -> authenticated
        DELETE /api/habits/{id:int}/days/{date} -> authenticated
        DELETE /api/habits/{id:int}/notes/{date} -> authenticated
        DELETE /api/habits/{id:int}/slips/{date} -> authenticated
        DELETE /api/low-days/{date} -> authenticated
        DELETE /api/me/discord -> authenticated
        DELETE /api/me/helps -> authenticated
        DELETE /api/me/journal-key -> authenticated
        DELETE /api/me/push-subscriptions -> authenticated
        DELETE /api/routines/{id:int} -> authenticated
        GET /api/account/merge -> authenticated
        GET /api/auth/antiforgery -> anonymous
        GET /api/auth/confirmEmail -> anonymous
        GET /api/auth/discord/link -> authenticated
        GET /api/auth/discord/link/complete -> anonymous
        GET /api/auth/discord/login -> anonymous
        GET /api/auth/discord/login/complete -> anonymous
        GET /api/auth/discord/native/complete -> anonymous
        GET /api/auth/manage/info -> authenticated
        GET /api/day-notes -> authenticated
        GET /api/fireside/ -> authenticated
        GET /api/habit-notes -> authenticated
        GET /api/habit-templates -> anonymous
        GET /api/habits -> authenticated
        GET /api/habits/{id:int} -> authenticated
        GET /api/habits/{id:int}/days -> authenticated
        GET /api/habits/{id:int}/slips -> authenticated
        GET /api/low-days -> authenticated
        GET /api/me -> authenticated
        GET /api/me/2fa -> authenticated
        GET /api/me/export -> authenticated
        GET /api/me/helps -> authenticated
        GET /api/me/journal-key -> authenticated
        GET /api/me/reminder-destinations -> authenticated
        GET /api/moods -> authenticated
        GET /api/push/vapid-public-key -> anonymous
        GET /api/routines -> authenticated
        GET /api/stats/days -> authenticated
        GET /api/stats/weekly -> authenticated
        GET /{*path:nonfile} -> anonymous
        HEAD /{*path:nonfile} -> anonymous
        PATCH /api/fireside/friends/{id:int} -> authenticated
        PATCH /api/habits/{id:int} -> authenticated
        PATCH /api/me -> authenticated
        PATCH /api/me/reminder-destinations/{id} -> authenticated
        PATCH /api/moods/{id:int} -> authenticated
        PATCH /api/routines/{id:int} -> authenticated
        POST /api/account/merge -> authenticated
        POST /api/auth/confirm-email-code -> anonymous
        POST /api/auth/confirmEmail -> anonymous
        POST /api/auth/discord/2fa -> anonymous
        POST /api/auth/discord/link/start -> authenticated
        POST /api/auth/discord/native/token -> anonymous
        POST /api/auth/forgotPassword -> anonymous
        POST /api/auth/login -> anonymous
        POST /api/auth/logout -> anonymous
        POST /api/auth/logout-everywhere -> anonymous
        POST /api/auth/manage/2fa -> authenticated
        POST /api/auth/manage/info -> authenticated
        POST /api/auth/refresh -> anonymous
        POST /api/auth/register -> anonymous
        POST /api/auth/resendConfirmationEmail -> anonymous
        POST /api/auth/resetPassword -> anonymous
        POST /api/feedback -> anonymous
        POST /api/fireside/code -> authenticated
        POST /api/fireside/friends/{id:int}/acknowledge -> authenticated
        POST /api/fireside/friends/{id:int}/warmth -> authenticated
        POST /api/fireside/join -> authenticated
        POST /api/fireside/warmth/{id:int}/dismiss -> authenticated
        POST /api/habits -> authenticated
        POST /api/habits/{id:int}/pause -> authenticated
        POST /api/habits/{id:int}/slips -> authenticated
        POST /api/me/2fa/disable -> authenticated
        POST /api/me/2fa/enable -> authenticated
        POST /api/me/2fa/recovery-codes -> authenticated
        POST /api/me/2fa/setup -> authenticated
        POST /api/me/consent -> authenticated
        POST /api/me/consent/decline -> authenticated
        POST /api/me/consent/withdraw -> authenticated
        POST /api/me/delete -> authenticated
        POST /api/me/dm-reminders/resume -> authenticated
        POST /api/me/import -> authenticated
        POST /api/me/journal-key/turn-off -> authenticated
        POST /api/me/phones -> authenticated
        POST /api/me/push-subscriptions -> authenticated
        POST /api/me/widget-key -> authenticated
        POST /api/moods -> authenticated
        POST /api/reminders/done -> anonymous
        POST /api/routines -> authenticated
        POST /api/widget/check-in -> anonymous
        PUT /api/day-notes/{date} -> authenticated
        PUT /api/fireside/ -> authenticated
        PUT /api/fireside/ask -> authenticated
        PUT /api/habits/{id:int}/days/{date} -> authenticated
        PUT /api/habits/{id:int}/notes/{date} -> authenticated
        PUT /api/low-days/{date} -> authenticated
        PUT /api/me/helps -> authenticated
        PUT /api/me/journal-key -> authenticated
        PUT /api/me/tips/{id} -> authenticated
        PUT /api/me/today-order -> authenticated
        PUT /api/routines/order -> authenticated
        """;

    private readonly string _webRoot = CreateWebRoot();

    protected override Dictionary<string, string?>? Settings => new() { ["Ember:WebRoot"] = _webRoot };

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    private static string CreateWebRoot()
    {
        var root = Directory.CreateTempSubdirectory("ember-webroot-").FullName;
        File.WriteAllText(Path.Combine(root, "index.html"), "<html></html>");
        return root;
    }

    [DockerFact]
    public void Every_mapped_endpoint_has_the_reviewed_auth_requirement()
    {
        var expected = Parse(Inventory);
        var actual = MappedEndpoints().ToDictionary(e => e.Key, e => e.Requirement);

        var problems = new List<string>();
        foreach (var (key, requirement) in actual.Where(e => !expected.ContainsKey(e.Key)))
            problems.Add(requirement is "anonymous" or "allow-anonymous"
                ? $"New endpoint '{key}' is open to anyone ({requirement}). If it should need a signed-in user, add .RequireAuthorization() "
                    + "or map it on the signed-in group. List it as anonymous only after reading its handler: it must check its own "
                    + "token, key or cookie, or be meant for anyone."
                : $"New endpoint '{key}' ({requirement}) is not in the inventory: review it, then add '{key} -> {requirement}'.");
        foreach (var key in expected.Keys.Where(k => !actual.ContainsKey(k)))
            problems.Add($"Endpoint '{key}' is in the inventory but no longer mapped: remove its line.");
        foreach (var (key, requirement) in actual.Where(e => expected.TryGetValue(e.Key, out var listed) && listed != e.Value))
            problems.Add($"Endpoint '{key}' now requires '{requirement}' instead of '{expected[key]}': restore its auth or review and update the line.");

        string.Join("\n", problems).Should().BeEmpty("the endpoint inventory is the reviewed auth contract; the app maps:\n{0}",
            string.Join("\n", actual.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key} -> {e.Value}")));
    }

    /// <summary>
    /// Every endpoint the inventory lists as requiring auth gets an anonymous request (an empty JSON object when it reads a body,
    /// so the route matches) and must answer 401 before its handler runs. Identity's manage routes answer 404 to everyone
    /// (<see cref="AuthEndpoints.HideIdentityManage"/>).
    /// </summary>
    [DockerFact]
    public async Task Every_endpoint_that_requires_auth_answers_an_anonymous_request_with_401()
    {
        var inventory = Parse(Inventory);
        var client = Host.NewClient();
        var failures = new List<string>();

        foreach (var endpoint in MappedEndpoints().Where(e => inventory.GetValueOrDefault(e.Key, e.Requirement) is not ("anonymous" or "allow-anonymous")))
        {
            var response = await client.Send(new HttpMethod(endpoint.Method), endpoint.SampleUrl, endpoint.ReadsBody ? new { } : null);
            var expected = endpoint.Pattern.StartsWith("/api/auth/manage/", StringComparison.Ordinal)
                ? HttpStatusCode.NotFound
                : HttpStatusCode.Unauthorized;
            if (response.StatusCode != expected)
                failures.Add($"{endpoint.Key} ({endpoint.SampleUrl}): {(int)response.StatusCode}, expected {(int)expected}");
        }

        string.Join("\n", failures).Should().BeEmpty("an anonymous caller must be turned away before any handler runs");
    }

    private sealed record Mapped(string Method, string Pattern, string Requirement, string SampleUrl, bool ReadsBody)
    {
        public string Key => $"{Method} {Pattern}";
    }

    private IEnumerable<Mapped> MappedEndpoints()
        => Host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ISuppressMatchingMetadata>()?.SuppressMatching != true)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? methods : ["*"])
                .Select(method => new Mapped(method, Pattern(e.RoutePattern), Requirement(e.Metadata), SampleUrl(e.RoutePattern),
                    e.Metadata.GetMetadata<IAcceptsMetadata>() is not null)));

    private static string Pattern(RoutePattern pattern) => "/" + (pattern.RawText ?? "").TrimStart('/');

    private static string Requirement(EndpointMetadataCollection metadata)
    {
        if (metadata.GetMetadata<IAllowAnonymous>() is not null) return "allow-anonymous";
        var authorize = metadata.GetOrderedMetadata<IAuthorizeData>();
        var policies = metadata.GetOrderedMetadata<AuthorizationPolicy>();
        var requirements = metadata.GetOrderedMetadata<IAuthorizationRequirementData>();
        if (authorize.Count == 0 && policies.Count == 0 && requirements.Count == 0) return "anonymous";

        var parts = authorize.Select(a => a.Policy is { Length: > 0 } p ? $"policy:{p}" : null)
            .Concat(authorize.Select(a => a.Roles is { Length: > 0 } r ? $"roles:{r}" : null))
            .Concat(authorize.Select(a => a.AuthenticationSchemes is { Length: > 0 } s ? $"schemes:{s}" : null))
            .Concat(policies.Select(_ => "policy:inline"))
            .Concat(requirements.Select(_ => "requirements:inline"))
            .OfType<string>().Distinct().ToList();
        return parts.Count == 0 ? "authenticated" : string.Join(" ", parts);
    }

    /// <summary>The pattern with each route parameter filled with a value the current constraints (<c>int</c> or none) accept.</summary>
    private static string SampleUrl(RoutePattern pattern)
        => "/" + string.Join("/", pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter => SampleValue(parameter),
            _ => "",
        }))));

    private static string SampleValue(RoutePatternParameterPart parameter) => parameter.IsCatchAll ? "sample/path" : "1";

    private static Dictionary<string, string> Parse(string inventory)
        => inventory.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split(" -> ", 2))
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());
}
