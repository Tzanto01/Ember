using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Ember.Ops.Lookout;

/// <summary>
/// The Lookout provider v1 routes under <c>/lookout/v1</c> and their gate: only the configured hub address with the shared bearer key
/// gets in, and only to a Lookout route. Writes are attributed to <see cref="ActorHeader"/>.
/// </summary>
public static class LookoutApi
{
    public const string ActorHeader = "Lookout-Actor";
    public const int MaxActorLength = 100;
    public const int MinKeyBytes = 32;
    private const string BoardId = "feedback";
    private const string NoticesId = "notices";

    /// <summary>
    /// Gates every request under <c>/lookout</c>: 403 unless the peer is the hub, 401 unless the bearer equals the key file,
    /// 404 problem unless a Lookout route matched. Runs after routing and before every other access check, static files and endpoints.
    /// </summary>
    internal static Func<HttpContext, RequestDelegate, Task> Gate(IOptions<OpsOptions> options, ILogger log) => (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/lookout")) return next(context);
        var o = options.Value.Lookout;
        if (context.Connection.RemoteIpAddress is not { } remote || !OpsAccess.IsTrusted(remote, [o.HubAddress]))
            return Refuse(context, log, StatusCodes.Status403Forbidden);
        if (!KeyMatches(o.KeyFile, context.Request.Headers.Authorization))
            return Refuse(context, log, StatusCodes.Status401Unauthorized);
        if (context.GetEndpoint() is not RouteEndpoint { RoutePattern.RawText: { } pattern } || !pattern.StartsWith("/lookout/", StringComparison.OrdinalIgnoreCase))
            return Problem(StatusCodes.Status404NotFound, "Not found").ExecuteAsync(context);
        return next(context);
    };

    internal static void Map(RouteGroupBuilder lookout)
    {
        lookout.MapGet("/manifest", (IOptions<OpsOptions> options) => LookoutPages.Manifest(options.Value.BuildSha));

        lookout.MapGet("/summary", SummaryAsync);

        lookout.MapGet("/pages/{pageId}", async (string pageId, IMemoryCache cache, OpsDatabases db, TimeProvider time, SystemInfo system,
            NoticeStore notices, CancellationToken ct) => pageId switch
        {
            "overview" => Results.Ok(LookoutPages.Overview(await OpsApp.OverviewAsync(cache, db, time, ct))),
            "reminders" => Results.Ok(LookoutPages.Reminders(await OpsApp.RemindersAsync(cache, db, time, ct))),
            "feedback" => Results.Ok(LookoutPages.Feedback(await FeedbackStore.ListAsync(db, ct), time.GetUtcNow().UtcDateTime)),
            "notices" => Results.Ok(LookoutPages.Notices(await notices.GetAsync(ct), time.GetUtcNow().UtcDateTime)),
            "system" => Results.Ok(LookoutPages.System(await OpsApp.SystemAsync(cache, system, ct))),
            _ => Problem(StatusCodes.Status404NotFound, "No page with that id"),
        });

        lookout.MapPut("/widgets/{w}/cards/{card}/move", async (string w, string card, HttpRequest request, OpsDatabases db, CancellationToken ct) =>
        {
            if (Actor(request) is null) return MissingActor();
            if (w != BoardId) return NoWidget();
            if (CardId(card) is not { } id) return NoFeedback();
            if (await BodyAsync<CardMove>(request, ct) is not { } move) return InvalidBody();
            if (move.Column is not { } column || !FeedbackStore.Statuses.Contains(column))
                return Problem(StatusCodes.Status400BadRequest, "column must be one of " + string.Join(", ", FeedbackStore.Statuses));
            if (move.Position is { } position && !double.IsFinite(position))
                return Problem(StatusCodes.Status400BadRequest, "position must be a finite number or null");
            return await FeedbackStore.MoveAsync(db, id, column, move.Position, ct) ? Results.NoContent() : NoFeedback();
        });

        lookout.MapGet("/widgets/{w}/cards/{card}/comments", async (string w, string card, OpsDatabases db, CancellationToken ct) =>
        {
            if (w != BoardId) return NoWidget();
            if (CardId(card) is not { } id || !await FeedbackStore.ExistsAsync(db.Ember, id, ct)) return NoFeedback();
            return Results.Ok((await FeedbackStore.CommentsAsync(db.Ops, id, ct)).Select(ToLookout).ToList());
        });

        lookout.MapPost("/widgets/{w}/cards/{card}/comments", async (string w, string card, HttpRequest request, OpsDatabases db, CancellationToken ct) =>
        {
            if (Actor(request) is not { } actor) return MissingActor();
            if (w != BoardId) return NoWidget();
            if (CardId(card) is not { } id) return NoFeedback();
            if (await BodyAsync<CommentInput>(request, ct) is not { } input) return InvalidBody();
            var body = input.Body?.Trim();
            if (string.IsNullOrEmpty(body) || body.Length > FeedbackStore.MaxCommentLength || body.Contains('\0'))
                return Problem(StatusCodes.Status400BadRequest, $"A comment needs 1 to {FeedbackStore.MaxCommentLength} characters and no NUL");
            if (!await FeedbackStore.ExistsAsync(db.Ember, id, ct)) return NoFeedback();
            return Results.Json(ToLookout(await FeedbackStore.AddCommentAsync(db.Ops, id, actor, body, ct)), statusCode: StatusCodes.Status201Created);
        });

        lookout.MapGet("/widgets/{w}/cards/{card}/images/{imageId}", async (string w, string card, string imageId, OpsDatabases db, CancellationToken ct) =>
        {
            if (w != BoardId) return NoWidget();
            if (CardId(card) is not { } id || !Guid.TryParse(imageId, out var image)
                || await FeedbackStore.ImageAsync(db.Ember, id, image, ct) is not { } data)
                return Problem(StatusCodes.Status404NotFound, "No image with that id");
            return Results.Bytes(data.Data, data.ContentType);
        });

        lookout.MapPost("/widgets/{w}/notices", async (string w, HttpRequest request, NoticeStore notices, CancellationToken ct) =>
        {
            if (Actor(request) is not { } actor) return MissingActor();
            if (w != NoticesId) return NoWidget();
            if (await BodyAsync<NoticeInput>(request, ct) is not { } input) return InvalidBody();
            var en = input.Text?.GetValueOrDefault("en");
            var de = input.Text?.GetValueOrDefault("de");
            return Notices(await notices.PublishAsync(new Ember.Ops.NoticeInput(input.Id, input.Kind, en, de, input.ShowFrom, input.ShowUntil), actor, ct));
        });

        lookout.MapPost("/widgets/{w}/notices/{id}/end", async (string w, string id, HttpRequest request, NoticeStore notices, CancellationToken ct) =>
        {
            if (Actor(request) is not { } actor) return MissingActor();
            if (w != NoticesId) return NoWidget();
            return Notices(await notices.EndAsync(id, actor, ct));
        });

        lookout.MapDelete("/widgets/{w}/notices/{id}", async (string w, string id, HttpRequest request, NoticeStore notices, CancellationToken ct) =>
        {
            if (Actor(request) is not { } actor) return MissingActor();
            if (w != NoticesId) return NoWidget();
            return Notices(await notices.DeleteAsync(id, actor, ct));
        });
    }

    /// <summary>The decoded, trimmed <see cref="ActorHeader"/>; null unless it is one value of 1 to 100 characters without control characters.</summary>
    public static string? Actor(HttpRequest request)
    {
        if (request.Headers[ActorHeader] is not { Count: 1 } values || values[0] is not { } raw) return null;
        var actor = Uri.UnescapeDataString(raw).Trim();
        return actor.Length is >= 1 and <= MaxActorLength && !actor.Any(char.IsControl) ? actor : null;
    }

    private static async Task<IResult> SummaryAsync(IMemoryCache cache, OpsDatabases db, TimeProvider time, SystemInfo system, ILoggerFactory loggers,
        CancellationToken ct)
    {
        var log = loggers.CreateLogger("Ember.Ops.Lookout");
        async Task<T?> Read<T>(Func<Task<T>> load) where T : class
        {
            try
            {
                return await load();
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning(e, "Lookout summary: database read failed");
                return null;
            }
        }

        var overview = await Read(() => OpsApp.OverviewCountersAsync(cache, db, time, ct));
        var reminders = await Read(() => OpsApp.ReminderCountersAsync(cache, db, time, ct));
        var feedback = await Read(() => FeedbackStore.ListAsync(db, ct));
        var report = await OpsApp.SystemAsync(cache, system, ct);
        return Results.Ok(LookoutPages.Summary(overview, reminders, feedback, report, time.GetUtcNow().UtcDateTime));
    }

    private static IResult Notices(IResult result) =>
        result is Ok<NoticesView> { Value: { } view } ? Results.Ok<Widget>(LookoutPages.Notices(view)) : result;

    private static Comment ToLookout(Ember.Ops.Comment c) => new(c.Id.ToString(CultureInfo.InvariantCulture), c.Author, c.Body, c.CreatedAt);

    private static int? CardId(string card) =>
        int.TryParse(card, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    private static async Task<T?> BodyAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        try
        {
            return await request.ReadFromJsonAsync<T>(ct);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static bool KeyMatches(string? keyFile, StringValues authorization)
    {
        const string scheme = "Bearer ";
        if (ReadKey(keyFile) is not { } key || authorization.Count != 1 || authorization[0] is not { } value
            || !value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(value[scheme.Length..])), SHA256.HashData(key));
    }

    private static byte[]? ReadKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var key = Encoding.UTF8.GetBytes(File.ReadAllText(path).Trim());
            return key.Length >= MinKeyBytes ? key : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static Task Refuse(HttpContext context, ILogger log, int status)
    {
        log.LogInformation("Refused {Method} {Path} from {RemoteIp} with {Status}", context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress, status);
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    private static IResult Problem(int status, string detail) => Results.Problem(statusCode: status, detail: detail);

    private static IResult MissingActor() => Problem(StatusCodes.Status400BadRequest, $"{ActorHeader} is missing or invalid");

    private static IResult InvalidBody() => Problem(StatusCodes.Status400BadRequest, "Invalid JSON body");

    private static IResult NoWidget() => Problem(StatusCodes.Status404NotFound, "No widget with that id");

    private static IResult NoFeedback() => Problem(StatusCodes.Status404NotFound, "No feedback with that id");
}
