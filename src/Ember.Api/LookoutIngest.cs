using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ember.Core.Data;
using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api;

/// <summary>
/// The client side of Lookout's ingest API (<c>PUT tickets/{key}</c>, <c>DELETE users/{userRef}</c>), shared by
/// <see cref="LookoutSender"/> and <see cref="LookoutImportCli"/>. Never logs or throws the key, a body or a user ref.
/// </summary>
internal static class LookoutIngest
{
    /// <summary>Request timeout of the Lookout HTTP client.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Key of the PostgreSQL session advisory lock <see cref="SendAsync"/> holds around each request, so the sender and the import
    /// never have more than one request open at Lookout.
    /// </summary>
    internal const long SendLockKey = 0x4C6F6F6B6F7574;

    /// <summary>How often <see cref="SendAsync"/> tries the lock again while another sender holds it.</summary>
    internal static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(200);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The ingest base (<c>{Lookout:IngestUrl}/ingest/v1/</c>) and <c>Lookout:KeyFile</c>, or false when either is empty or the URL
    /// isn't an absolute http or https URL whose host is an IP literal.
    /// </summary>
    public static bool TryGetEndpoint(IConfiguration configuration, out Uri endpoint, out string keyFile)
    {
        endpoint = null!;
        keyFile = configuration["Lookout:KeyFile"] ?? "";
        if (keyFile.Length == 0
            || !Uri.TryCreate(configuration["Lookout:IngestUrl"], UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https")
            || url.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
            return false;
        endpoint = new Uri(url.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/ingest/v1/");
        return true;
    }

    /// <summary>A new HTTP client for the ingest: <see cref="Timeout"/>, no redirects.</summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout };

    /// <summary>
    /// Sends one request with the key read fresh from <paramref name="keyFile"/>, holding the <see cref="SendLockKey"/> advisory lock
    /// on <paramref name="db"/>'s connection from before the request until its response arrives; while another sender holds it, tries
    /// again every <see cref="LockPoll"/>, so no command timeout ends the wait. A failed unlock is logged to <paramref name="logger"/>
    /// and left to the closing connection. Throws the file's I/O exception, or <see cref="InvalidDataException"/> when the trimmed key
    /// is empty or not printable ASCII.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(EmberDbContext db, HttpClient http, Uri endpoint, string keyFile, HttpMethod method,
        string path, byte[]? body, CancellationToken cancellationToken, ILogger? logger = null)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            while (!await db.Database.SqlQuery<bool>($"""SELECT pg_try_advisory_lock({SendLockKey}) AS "Value" """).SingleAsync(cancellationToken))
                await Task.Delay(LockPoll, cancellationToken);
            try
            {
                return await SendUnlockedAsync(http, endpoint, keyFile, method, path, body, cancellationToken);
            }
            finally
            {
                try
                {
                    await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({SendLockKey})", CancellationToken.None);
                }
                catch (Exception e)
                {
                    logger?.LogWarning("Releasing the Lookout send lock failed with {ExceptionType}; the connection releases it on close.", e.GetType().Name);
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<HttpResponseMessage> SendUnlockedAsync(HttpClient http, Uri endpoint, string keyFile, HttpMethod method, string path,
        byte[]? body, CancellationToken cancellationToken)
    {
        var key = (await File.ReadAllTextAsync(keyFile, cancellationToken)).Trim();
        if (key.Length == 0 || key.Any(c => c is < '!' or > '~')) throw new InvalidDataException("The Lookout key file holds no key.");

        using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return await http.SendAsync(request, cancellationToken);
    }

    /// <summary>The path of <c>PUT tickets/{key}</c>.</summary>
    public static string TicketPath(string key) => "tickets/" + Uri.EscapeDataString(key);

    /// <summary>The path of <c>DELETE users/{userRef}</c>.</summary>
    public static string UserPath(string userRef) => "users/" + Uri.EscapeDataString(userRef);

    /// <summary>Whether a non-2xx status is one of the contract's permanent refusals: 400, 409, 413 or 415. Everything else is retried.</summary>
    public static bool IsRefused(int status) => status is 400 or 409 or 413 or 415;

    /// <summary>Whether <paramref name="e"/> is a failure worth retrying: network, timeout, key file or the database around the send lock.</summary>
    public static bool IsTransient(Exception e, CancellationToken stopping)
        => e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or System.Data.Common.DbException
            || (e is TaskCanceledException && !stopping.IsCancellationRequested);

    /// <summary>The wait after the <paramref name="failures"/>-th consecutive transient failure: 5 s doubling, at most 10 minutes.</summary>
    public static TimeSpan Backoff(int failures)
        => TimeSpan.FromSeconds(Math.Min(600, 5 * Math.Pow(2, Math.Clamp(failures, 1, 30) - 1)));

    /// <summary>A UTC time as Lookout takes it: <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>.</summary>
    public static string Utc(DateTime time)
        => DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The UTF-8 JSON <c>TicketInput</c> of <paramref name="feedback"/> (with its <see cref="Feedback.Images"/> in id order); the
    /// same input gives the same bytes.
    /// </summary>
    public static byte[] TicketBody(Feedback feedback, string? userRef, TicketImport? import = null)
        => JsonSerializer.SerializeToUtf8Bytes(new TicketInput("feedback", feedback.Title, feedback.Text, feedback.Platform, feedback.Version,
            Utc(feedback.CreatedAt), userRef, [.. feedback.Images.OrderBy(i => i.Id).Select(i => new TicketImage(i.ContentType, i.Data))], import), Json);

    private sealed record TicketInput(string Kind, string Title, string Text, string Platform, string? Version, string CreatedAt, string? UserRef,
        IReadOnlyList<TicketImage> Images, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TicketImport? Import);

    private sealed record TicketImage(string ContentType, byte[] Data);

    /// <summary>The <c>import</c> part of a ticket for the one-time import.</summary>
    public sealed record TicketImport(string Status, string? ResolvedAt, int? Position, IReadOnlyList<TicketComment> Comments);

    /// <summary>One imported comment.</summary>
    public sealed record TicketComment(string Author, string Body, string CreatedAt);
}
