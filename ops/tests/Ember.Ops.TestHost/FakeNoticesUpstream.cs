using System.Net;
using System.Text;
using System.Text.Json;

namespace Ember.Ops.TestHost;

/// <summary>
/// In-process stand-in for the published <c>notices.json</c> and the Worker's publish endpoint. Answers
/// <c>GET <see cref="ReadUrl"/></c> with <see cref="Notices"/> and <c>PUT <see cref="PublishUrl"/></c> like the Worker:
/// 401 without <c>Bearer <see cref="Token"/></c>, 400 problem+json when <see cref="NoticeRules"/> refuses, else 204 and stores
/// the document. Everything else is 404. Use it as the primary handler of the notices and outbound clients.
/// </summary>
public sealed class FakeNoticesUpstream(string token) : HttpMessageHandler
{
    public const string ReadUrl = "https://notices.test/notices.json";
    public const string PublishUrl = "https://notices.test/_ops/notices";

    private readonly Lock gate = new();
    private IReadOnlyList<Notice> notices = [];

    public string Token { get; } = token;

    /// <summary>The stored document's notices.</summary>
    public IReadOnlyList<Notice> Notices
    {
        get { lock (gate) return notices; }
        set { lock (gate) notices = value; }
    }

    /// <summary>Accepted PUTs so far.</summary>
    public int Publishes { get; private set; }

    /// <summary>The body of the last accepted PUT, as sent.</summary>
    public string? LastPublished { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.ToString();
        if (request.Method == HttpMethod.Get && url == ReadUrl)
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new NoticeDocument(Notices), JsonSerializerOptions.Web), "application/json");
        if (request.Method != HttpMethod.Put || url != PublishUrl) return new(HttpStatusCode.NotFound);
        if (request.Headers.Authorization is not { Scheme: "Bearer", Parameter: var sent } || sent != Token) return new(HttpStatusCode.Unauthorized);

        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        NoticeDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<NoticeDocument>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            doc = null;
        }
        if (doc?.Notices is not { } sentNotices) return Problem("The body is not a notices document");
        if (NoticeRules.Validate(sentNotices) is { } error) return Problem(error);
        lock (gate)
        {
            notices = sentNotices.ToArray();
            LastPublished = body;
            Publishes++;
        }
        return new(HttpStatusCode.NoContent);
    }

    private static HttpResponseMessage Problem(string detail) =>
        Json(HttpStatusCode.BadRequest, JsonSerializer.Serialize(new { detail }, JsonSerializerOptions.Web), "application/problem+json");

    private static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
}
