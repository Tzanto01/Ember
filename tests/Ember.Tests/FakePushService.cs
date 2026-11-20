using System.Net;
using System.Security.Cryptography;

namespace Ember.Tests;

internal sealed record PushRequest(string Endpoint, string? Authorization, string ContentEncoding, string? Ttl, int BodyLength);

/// <summary>The browser push service: records every request and answers with <see cref="Responses"/> per endpoint, else <see cref="Status"/>.</summary>
internal sealed class FakePushService : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<PushRequest> _requests = [];

    public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

    public Dictionary<string, HttpStatusCode> Responses { get; } = [];

    public IReadOnlyList<PushRequest> Requests
    {
        get
        {
            lock (_gate) return _requests.ToList();
        }
    }

    public IEnumerable<string> Endpoints => Requests.Select(r => r.Endpoint);

    public int Count => Requests.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var endpoint = request.RequestUri!.ToString();
        var contentEncoding = request.Content is null ? "" : string.Join(",", request.Content.Headers.ContentEncoding);
        lock (_gate)
        {
            _requests.Add(new PushRequest(endpoint,
                request.Headers.TryGetValues("Authorization", out var authorization) ? authorization.First() : null,
                contentEncoding,
                request.Headers.TryGetValues("TTL", out var ttl) ? ttl.First() : null,
                body.Length));
        }

        return new HttpResponseMessage(Responses.GetValueOrDefault(endpoint, Status));
    }
}

internal static class WebPushTestKeys
{
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static (string Public, string Private) NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(true);
        return (Base64Url([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]), Base64Url(parameters.D!));
    }
}
