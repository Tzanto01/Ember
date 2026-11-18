using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Ember.Ops;

/// <summary>One notice as published in <c>notices.json</c>; absent times are omitted from the JSON.</summary>
public sealed record Notice(
    string Id, string Kind, string En, string De,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ShowFrom = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ShowUntil = null);

/// <summary>The whole published document, <c>{"notices":[...]}</c>.</summary>
public sealed record NoticeDocument(IReadOnlyList<Notice>? Notices);

/// <summary>The <c>POST /api/notices</c> body.</summary>
public sealed record NoticeInput(string? Id, string? Kind, string? En, string? De, string? ShowFrom, string? ShowUntil);

/// <summary>A notice with its state at the server's clock: <c>scheduled</c>, <c>live</c> or <c>ended</c>.</summary>
public sealed record NoticeView(
    string Id, string Kind, string En, string De,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ShowFrom,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ShowUntil,
    string State);

/// <summary><c>GET /api/notices</c> and every write's answer.</summary>
public sealed record NoticesView(bool Publishing, IReadOnlyList<NoticeView> Notices);

/// <summary>The validation rules the publish endpoint applies; the first failing rule's message, or null when valid.</summary>
public static class NoticeRules
{
    public const int MaxNotices = 10;
    public const int MaxIdLength = 64;
    public const int MaxTextLength = 280;
    public const int MaxDocumentBytes = 16 * 1024;

    public static string? Validate(IReadOnlyList<Notice> notices)
    {
        if (notices.Count > MaxNotices) return $"At most {MaxNotices} notices";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var notice in notices)
        {
            if (string.IsNullOrEmpty(notice.Id)) return "id is empty";
            if (!ids.Add(notice.Id)) return $"id {notice.Id} is used twice";
            if (notice.Kind is not ("info" or "maintenance")) return "kind must be info or maintenance";
            if ((Text(notice.En, "en") ?? Text(notice.De, "de")) is { } text) return text;
            if (notice.ShowFrom is not null && ParseTime(notice.ShowFrom) is null) return "showFrom must be a UTC time ending in Z";
            if (notice.ShowUntil is not null && ParseTime(notice.ShowUntil) is null) return "showUntil must be a UTC time ending in Z";
            if (ParseTime(notice.ShowFrom) >= ParseTime(notice.ShowUntil)) return "showFrom must be before showUntil";
        }
        if (JsonSerializer.SerializeToUtf8Bytes(new NoticeDocument(notices), JsonSerializerOptions.Web).Length > MaxDocumentBytes)
            return "The notices are larger than 16 KiB";
        return null;
    }

    /// <summary>The id rule for a notice the dashboard writes; the message when <paramref name="id"/> breaks it, else null.</summary>
    public static string? CheckNewId(string id) =>
        id.Length > MaxIdLength ? $"id is longer than {MaxIdLength} characters"
        : id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? null
        : "id may only contain letters, digits, '.', '_' and '-'";

    /// <summary>Parses a <c>showFrom</c>/<c>showUntil</c> value; null when absent, not a UTC ISO time ending in <c>Z</c>, or unparseable.</summary>
    public static DateTimeOffset? ParseTime(string? value) =>
        value is { Length: > 0 } && TimeFormat.IsMatch(value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    /// <summary>Formats a time the way notices carry it, e.g. <c>2026-11-18T19:30:00Z</c>.</summary>
    public static string FormatTime(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The notice's state at <paramref name="now"/>.</summary>
    public static string State(Notice notice, DateTimeOffset now) =>
        ParseTime(notice.ShowFrom) > now ? "scheduled" : ParseTime(notice.ShowUntil) <= now ? "ended" : "live";

    private static readonly Regex TimeFormat = new(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]+)?)?Z\z", RegexOptions.CultureInvariant);

    private static string? Text(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return $"{name} is empty";
        return text.EnumerateRunes().Count() > MaxTextLength ? $"{name} is longer than {MaxTextLength} characters" : null;
    }
}

/// <summary>
/// Reads the live notices document and publishes changes to it, one write at a time, logging each to <c>NoticeLog</c>.
/// Edge-cached reads lag, so for <see cref="StaleReadWindow"/> a read that still shows a document this process replaced counts as
/// the document it wrote; any other read, such as another writer's change, counts as live.
/// </summary>
public sealed class NoticeStore(IOptions<OpsOptions> options, OpsDatabases db, IHttpClientFactory http, TimeProvider time, ILogger<NoticeStore> logger)
{
    public const string HttpClientName = "notices";
    public static readonly TimeSpan StaleReadWindow = TimeSpan.FromSeconds(90);
    private const string NotSetUp = "Publishing isn't set up";

    private sealed record Replaced(IReadOnlyList<Notice> Notices, DateTimeOffset At);

    private sealed record Memory(IReadOnlyList<Notice> Written, IReadOnlyList<Replaced> Replaced);

    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile Memory? memory;

    public async Task<IResult> GetAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var (notices, _, error) = await EffectiveAsync(o, now, ct);
        return notices is null
            ? Results.Problem(detail: error, statusCode: StatusCodes.Status502BadGateway)
            : Results.Ok(View(notices, now, ReadToken(o) is not null && Configured(o)));
    }

    /// <summary>Adds the notice, or replaces the one with its id.</summary>
    public Task<IResult> PublishAsync(NoticeInput input, string actor, CancellationToken ct)
    {
        if (new[] { input.Id, input.Kind, input.En, input.De, input.ShowFrom, input.ShowUntil }.Any(s => s?.Contains('\0') == true))
            return Task.FromResult(Results.Problem(detail: "Text can't contain NUL characters", statusCode: StatusCodes.Status400BadRequest));
        var notice = new Notice(input.Id ?? string.Empty, input.Kind ?? string.Empty, input.En ?? string.Empty, input.De ?? string.Empty,
            string.IsNullOrEmpty(input.ShowFrom) ? null : input.ShowFrom, string.IsNullOrEmpty(input.ShowUntil) ? null : input.ShowUntil);
        if (NoticeRules.CheckNewId(notice.Id) is { } badId)
            return Task.FromResult(Results.Problem(detail: badId, statusCode: StatusCodes.Status400BadRequest));
        return WriteAsync(actor, "publish", (notices, _) =>
        {
            var index = notices.FindIndex(n => n.Id == notice.Id);
            if (index < 0) notices.Add(notice);
            else notices[index] = notice;
            return (null, notice);
        }, ct);
    }

    /// <summary>Sets <c>showUntil</c> to now, dropping a <c>showFrom</c> that is not yet past.</summary>
    public Task<IResult> EndAsync(string id, string actor, CancellationToken ct) =>
        WriteAsync(actor, "end", (notices, now) =>
        {
            var index = notices.FindIndex(n => n.Id == id);
            if (index < 0) return (NotFound(), null);
            if (NoticeRules.State(notices[index], now) == "ended")
                return (Results.Problem(detail: "That notice has already ended", statusCode: StatusCodes.Status409Conflict), null);
            var until = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
            var ended = notices[index] with
            {
                ShowFrom = NoticeRules.ParseTime(notices[index].ShowFrom) >= until ? null : notices[index].ShowFrom,
                ShowUntil = NoticeRules.FormatTime(until),
            };
            notices[index] = ended;
            return (null, ended);
        }, ct);

    public Task<IResult> DeleteAsync(string id, string actor, CancellationToken ct) =>
        WriteAsync(actor, "delete", (notices, _) =>
        {
            var index = notices.FindIndex(n => n.Id == id);
            if (index < 0) return (NotFound(), null);
            var removed = notices[index];
            notices.RemoveAt(index);
            return (null, removed);
        }, ct);

    private async Task<IResult> WriteAsync(string actor, string action, Func<List<Notice>, DateTimeOffset, (IResult? Error, Notice? Logged)> change, CancellationToken ct)
    {
        var o = options.Value;
        if (!Configured(o) || ReadToken(o) is not { } token)
            return Results.Problem(detail: NotSetUp, statusCode: StatusCodes.Status503ServiceUnavailable);

        await gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            var (live, upstream, readError) = await EffectiveAsync(o, now, CancellationToken.None);
            if (live is null) return Results.Problem(detail: readError, statusCode: StatusCodes.Status502BadGateway);

            List<Notice> notices = [.. live];
            var (error, logged) = change(notices, now);
            if (error is not null) return error;
            if (NoticeRules.Validate(notices) is { } invalid) return Results.Problem(detail: invalid, statusCode: StatusCodes.Status400BadRequest);

            if (await PutAsync(o.NoticesPublishUrl!, token, notices, CancellationToken.None) is { } failed) return failed;
            memory = new(notices.ToArray(), [.. (memory?.Replaced ?? []).Where(r => now - r.At < StaleReadWindow), new(live, now), new(upstream!, now)]);
            await LogAsync(actor, action, logged!);
            return Results.Ok(View(notices, now, true));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IResult?> PutAsync(string url, string token, List<Notice> notices, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new NoticeDocument(notices), JsonSerializerOptions.Web), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return null;
            if (response.StatusCode == HttpStatusCode.Unauthorized) return BadGateway("The publish token was refused");
            if (response.StatusCode != HttpStatusCode.BadRequest) return BadGateway($"The publish endpoint answered HTTP {(int)response.StatusCode}");
            return Results.Problem(detail: await DetailAsync(response, ct) ?? "The publish endpoint refused the notices", statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Notices: publish failed ({Error})", e.GetType().Name);
            return BadGateway(e is TaskCanceledException ? "The publish endpoint timed out" : "Couldn't reach the publish endpoint");
        }
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(detail.GetString())
                    ? detail.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task LogAsync(string actor, string action, Notice notice)
    {
        try
        {
            await using var command = db.Ops.CreateCommand("INSERT INTO NoticeLog (actor, action, notice) VALUES ($1, $2, $3)");
            command.Parameters.Add(new NpgsqlParameter { Value = actor });
            command.Parameters.Add(new NpgsqlParameter { Value = action });
            command.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(notice, JsonSerializerOptions.Web), NpgsqlDbType = NpgsqlDbType.Jsonb });
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Notices: writing the {Action} log row failed; the change is live", action);
        }
    }

    private async Task<(IReadOnlyList<Notice>? Notices, IReadOnlyList<Notice>? Upstream, string? Error)> EffectiveAsync(OpsOptions o, DateTimeOffset now, CancellationToken ct)
    {
        var (upstream, error) = await ReadAsync(o, ct);
        if (upstream is null) return (null, null, error);
        if (memory is { Replaced.Count: > 0 } caughtUp && caughtUp.Written.SequenceEqual(upstream))
            Interlocked.CompareExchange(ref memory, caughtUp with { Replaced = [] }, caughtUp);
        return (memory is { } m && m.Replaced.Any(r => now - r.At < StaleReadWindow && r.Notices.SequenceEqual(upstream)) ? m.Written : upstream, upstream, null);
    }

    private async Task<(IReadOnlyList<Notice>? Notices, string? Error)> ReadAsync(OpsOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.NoticesUrl)) return ([], null);
        try
        {
            using var response = await http.CreateClient(HttpClientName).GetAsync(o.NoticesUrl, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return ([], null);
            if (!response.IsSuccessStatusCode) return (null, $"Reading the live notices failed (HTTP {(int)response.StatusCode})");
            var doc = JsonSerializer.Deserialize<NoticeDocument>(await response.Content.ReadAsStringAsync(ct), JsonSerializerOptions.Web);
            return ((doc?.Notices ?? []).OfType<Notice>().ToList(), null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Notices: reading the live document failed");
            return (null, e switch
            {
                TaskCanceledException => "Reading the live notices timed out",
                JsonException => "The live notices are unreadable",
                _ => "Couldn't read the live notices",
            });
        }
    }

    private string? ReadToken(OpsOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.NoticesTokenFile)) return null;
        try
        {
            var token = File.ReadAllText(o.NoticesTokenFile).Trim();
            return token.Length > 0 ? token : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Configured(OpsOptions o) =>
        Https(o.NoticesUrl) && Https(o.NoticesPublishUrl);

    private static bool Https(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static NoticesView View(IReadOnlyList<Notice> notices, DateTimeOffset now, bool publishing) =>
        new(publishing, notices.Select(n => new NoticeView(n.Id, n.Kind, n.En, n.De, n.ShowFrom, n.ShowUntil, NoticeRules.State(n, now))).ToList());

    private static IResult NotFound() => Results.Problem(detail: "No notice with that id", statusCode: StatusCodes.Status404NotFound);

    private static IResult BadGateway(string detail) => Results.Problem(detail: detail, statusCode: StatusCodes.Status502BadGateway);
}
