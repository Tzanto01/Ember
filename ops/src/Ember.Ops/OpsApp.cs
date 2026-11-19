using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ember.Ops;

/// <summary>Builds the Lookout provider: <c>/healthz</c> and the gated <c>/lookout/v1</c> routes.</summary>
public static class OpsApp
{
    public static readonly TimeSpan CounterCache = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan SeriesCache = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FeaturesCache = TimeSpan.FromSeconds(60);
    public const string ReminderCountersKey = "reminders:counters";

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
            client.Timeout = TimeSpan.FromSeconds(3);
            client.MaxResponseContentBufferSize = 1 << 20;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ember-ops");
        });
        services.AddSingleton<SystemInfo>();
        services.AddHttpClient(NoticeStore.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(4);
                client.MaxResponseContentBufferSize = 64 << 10;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ember-ops");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RedactLoggedHeaders(_ => true);
        services.AddSingleton<NoticeStore>();
        services.AddHostedService<ReminderSnapshotService>();
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
            return next(context);
        });

        app.Use(Lookout.LookoutApi.Gate(options, refused));

        app.MapGet("/healthz", () => "ok");
        Lookout.LookoutApi.Map(app.MapGroup("/lookout/v1"));

        return app;
    }

    /// <summary>Creates or updates the emberops schema; run once before the app starts.</summary>
    public static Task InitializeAsync(WebApplication app, CancellationToken ct = default) =>
        OpsMigrator.ApplyAsync(app.Services.GetRequiredService<OpsDatabases>().Ops, ct);

    internal static async Task<OverviewCounters> OverviewCountersAsync(IMemoryCache cache, OpsDatabases db, TimeProvider time, CancellationToken ct)
    {
        var features = await FeaturesAsync(cache, db, ct);
        return await Cached(cache, "overview:counters", CounterCache, () => OverviewQueries.CountersAsync(db.Ember, features, time, ct));
    }

    internal static async Task<Overview> OverviewAsync(IMemoryCache cache, OpsDatabases db, TimeProvider time, CancellationToken ct)
    {
        var counters = await OverviewCountersAsync(cache, db, time, ct);
        return Overview.From(counters, await Cached(cache, "overview:series", SeriesCache, () => OverviewQueries.SeriesAsync(db.Ember, time, ct)));
    }

    internal static Task<ReminderCounters> ReminderCountersAsync(IMemoryCache cache, OpsDatabases db, TimeProvider time, CancellationToken ct) =>
        Cached(cache, ReminderCountersKey, CounterCache, () => ReminderQueries.CountersAsync(db.Ember, time, ct));

    internal static async Task<Reminders> RemindersAsync(IMemoryCache cache, OpsDatabases db, TimeProvider time, CancellationToken ct) =>
        Reminders.From(
            await ReminderCountersAsync(cache, db, time, ct),
            await Cached(cache, "reminders:history", SeriesCache, () => ReminderQueries.HistoryAsync(db.Ops, time, ct)));

    internal static Task<SystemReport> SystemAsync(IMemoryCache cache, SystemInfo system, CancellationToken ct) =>
        Cached(cache, "system", CounterCache, () => system.GetAsync(ct));

    private static Task<EmberFeatures> FeaturesAsync(IMemoryCache cache, OpsDatabases db, CancellationToken ct) =>
        Cached(cache, "ember:features", FeaturesCache, () => EmberFeatures.DetectAsync(db.Ember, ct));

    internal static async Task<T> Cached<T>(IMemoryCache cache, string key, TimeSpan ttl, Func<Task<T>> load) =>
        (await cache.GetOrCreateAsync(key, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ttl;
            return load();
        }))!;
}
