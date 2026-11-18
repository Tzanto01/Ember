using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Ember.Ops;

public sealed record Session(string Author, string CsrfToken);
public sealed record StatusMove(string? Status, double? Position);
public sealed record CommentInput(string? Body);

/// <summary>Builds the dashboard: access control in front of everything but <c>/healthz</c>, the JSON API, then the web app.</summary>
public static class OpsApp
{
    public const string CsrfHeader = "X-CSRF-TOKEN";
    public static readonly TimeSpan CounterCache = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan SeriesCache = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FeaturesCache = TimeSpan.FromSeconds(60);
    public const string ReminderCountersKey = "reminders:counters";
    private const string AuthorItem = "ops.author";

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var services = builder.Services;
        services.AddOptions<OpsOptions>().BindConfiguration("Ops");
        services.AddSingleton(sp => OpsDatabases.Create(sp.GetRequiredService<IOptions<OpsOptions>>().Value));
        services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddHttpClient(SystemInfo.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.MaxResponseContentBufferSize = 1 << 20;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ember-ops");
        });
        services.AddSingleton<SystemInfo>();
        services.AddHttpClient(NoticeStore.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.MaxResponseContentBufferSize = 64 << 10;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ember-ops");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(_ => true);
        services.AddSingleton<NoticeStore>();
        services.AddHostedService<ReminderSnapshotService>();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAntiforgery(o =>
        {
            o.HeaderName = CsrfHeader;
            o.Cookie.Name = "ember-ops-csrf";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.SuppressXFrameOptionsHeader = true;
        });
        return builder;
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        var options = app.Services.GetRequiredService<IOptions<OpsOptions>>();
        var refused = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ember.Ops.Access");

        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy =
                "default-src 'self'; img-src 'self' blob: data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (context.Request.Path.StartsWithSegments("/api")) headers.CacheControl = "no-store";
            return next(context);
        });

        app.Use((context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/healthz") return next(context);
            var author = OpsAccess.Resolve(context.Connection.RemoteIpAddress, context.Request.Headers, options.Value.Access);
            if (author is null)
            {
                refused.LogInformation("Refused {Method} {Path} from {RemoteIp}", context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            context.Items[AuthorItem] = author;
            context.Request.Scheme = Uri.UriSchemeHttps;
            return next(context);
        });

        var webRoot = string.IsNullOrWhiteSpace(options.Value.WebRoot)
            ? Path.Combine(app.Environment.ContentRootPath, "wwwroot")
            : Path.GetFullPath(options.Value.WebRoot);
        if (Directory.Exists(webRoot))
            app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(webRoot) });

        app.MapGet("/healthz", () => "ok");
        MapApi(app.MapGroup("/api"));

        var index = Path.Combine(webRoot, "index.html");
        app.MapFallback(async context =>
        {
            if (context.Request.Path.StartsWithSegments("/api") || !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                || !File.Exists(index))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(index);
        });

        return app;
    }

    /// <summary>Creates or updates the emberops schema; run once before the app starts.</summary>
    public static Task InitializeAsync(WebApplication app, CancellationToken ct = default) =>
        OpsMigrator.ApplyAsync(app.Services.GetRequiredService<OpsDatabases>().Ops, ct);

    /// <summary>The author the access check resolved for this request.</summary>
    public static string Author(HttpContext context) => (string)context.Items[AuthorItem]!;

    private static void MapApi(RouteGroupBuilder api)
    {
        api.MapGet("/session", (HttpContext context, IAntiforgery antiforgery) =>
            new Session(Author(context), antiforgery.GetAndStoreTokens(context).RequestToken!));

        api.MapGet("/overview", async (IMemoryCache cache, OpsDatabases db, TimeProvider time, CancellationToken ct) =>
        {
            var features = await Cached(cache, "ember:features", FeaturesCache, () => EmberFeatures.DetectAsync(db.Ember, ct));
            return Overview.From(
                await Cached(cache, "overview:counters", CounterCache, () => OverviewQueries.CountersAsync(db.Ember, features, time, ct)),
                await Cached(cache, "overview:series", SeriesCache, () => OverviewQueries.SeriesAsync(db.Ember, features, time, ct)));
        });

        api.MapGet("/reminders", async (IMemoryCache cache, OpsDatabases db, TimeProvider time, CancellationToken ct) =>
            Reminders.From(
                await Cached(cache, ReminderCountersKey, CounterCache, () => ReminderQueries.CountersAsync(db.Ember, time, ct)),
                await Cached(cache, "reminders:history", SeriesCache, () => ReminderQueries.HistoryAsync(db.Ops, time, ct))));

        api.MapGet("/system", (IMemoryCache cache, SystemInfo system, CancellationToken ct) =>
            Cached(cache, "system", CounterCache, () => system.GetAsync(ct)));

        api.MapGet("/feedback", (OpsDatabases db, CancellationToken ct) => FeedbackStore.ListAsync(db, ct));

        api.MapPut("/feedback/{id:int}/status", async (int id, StatusMove move, OpsDatabases db, CancellationToken ct) =>
        {
            if (move.Status is not { } status || !FeedbackStore.Statuses.Contains(status) || move.Position is { } p && !double.IsFinite(p))
                return Results.BadRequest();
            return await FeedbackStore.MoveAsync(db, id, status, move.Position, ct) ? Results.NoContent() : Results.NotFound();
        }).AddEndpointFilter(WriteGuard);

        api.MapGet("/feedback/{id:int}/images/{imageId:guid}", async (int id, Guid imageId, HttpContext context, OpsDatabases db, CancellationToken ct) =>
        {
            if (await FeedbackStore.ImageAsync(db.Ember, id, imageId, ct) is not { } image) return Results.NotFound();
            context.Response.Headers.ContentDisposition = "inline";
            return Results.Bytes(image.Data, image.ContentType);
        });

        api.MapGet("/feedback/{id:int}/comments", (int id, OpsDatabases db, CancellationToken ct) => FeedbackStore.CommentsAsync(db.Ops, id, ct));

        api.MapPost("/feedback/{id:int}/comments", async (int id, CommentInput input, HttpContext context, OpsDatabases db, CancellationToken ct) =>
        {
            var body = input.Body?.Trim();
            if (string.IsNullOrEmpty(body) || body.Length > FeedbackStore.MaxCommentLength || body.Contains('\0')) return Results.BadRequest();
            if (!await FeedbackStore.ExistsAsync(db.Ember, id, ct)) return Results.NotFound();
            return Results.Json(await FeedbackStore.AddCommentAsync(db.Ops, id, Author(context), body, ct), statusCode: StatusCodes.Status201Created);
        }).AddEndpointFilter(WriteGuard);

        api.MapGet("/notices", (NoticeStore notices, CancellationToken ct) => notices.GetAsync(ct));

        api.MapPost("/notices", (NoticeInput input, HttpContext context, NoticeStore notices, CancellationToken ct) =>
            notices.PublishAsync(input, Author(context), ct)).AddEndpointFilter(WriteGuard);

        api.MapPost("/notices/{id}/end", (string id, HttpContext context, NoticeStore notices, CancellationToken ct) =>
            notices.EndAsync(id, Author(context), ct)).AddEndpointFilter(WriteGuard);

        api.MapDelete("/notices/{id}", (string id, HttpContext context, NoticeStore notices, CancellationToken ct) =>
            notices.DeleteAsync(id, Author(context), ct)).AddEndpointFilter(WriteGuard);
    }

    private static async ValueTask<object?> WriteGuard(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        if (context.Request.Headers["Sec-Fetch-Site"] is { Count: > 0 } site && site.ToString() is not ("same-origin" or "none"))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await context.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(context))
            return Results.BadRequest();
        return await next(invocation);
    }

    internal static async Task<T> Cached<T>(IMemoryCache cache, string key, TimeSpan ttl, Func<Task<T>> load) =>
        (await cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ttl;
            return load();
        }))!;
}
