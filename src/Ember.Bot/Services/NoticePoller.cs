using Ember.Bot.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Ember.Bot.Services;

/// <summary>One entry of <c>/notices.json</c>; <see cref="ShowFrom"/> and <see cref="ShowUntil"/> are optional.</summary>
public sealed record Notice(string Id, DateTimeOffset? ShowFrom, DateTimeOffset? ShowUntil, string En);

/// <summary>
/// Polls the live notices file every 5 minutes (<c>Ember:NoticesUrl</c>, or <c>Ember:WebOrigin</c> + <c>/notices.json</c>).
/// A 404 means no notice; any other failed poll (network error, timeout, non-200, invalid JSON) also clears the notices
/// and logs one warning per failure streak. Raises <see cref="Changed"/> after every poll; never DMs anyone.
/// </summary>
public sealed class NoticePoller : BackgroundService
{
    internal const string NoticesUrlKey = "Ember:NoticesUrl";
    internal const string HttpClientName = "notices";
    internal const int StatusMaxLength = 128;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly TimeProvider _time;
    private readonly ILogger<NoticePoller> _logger;
    private volatile Notice[] _notices = [];
    private bool _failing;

    public NoticePoller(IHttpClientFactory http, IConfiguration config, TimeProvider time, ILogger<NoticePoller> logger)
    {
        _http = http;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <summary>Raised after each poll, whether or not the notices changed.</summary>
    public event Func<Task>? Changed;

    /// <summary>The notice active right now, or <c>null</c>.</summary>
    public Notice? Active => Select(_notices, _time.GetUtcNow());

    /// <summary>The URL to poll, or <c>null</c> when neither key is configured.</summary>
    internal static string? NoticesUrl(IConfiguration config)
        => config[NoticesUrlKey] is { Length: > 0 } url ? url
            : config[LinkModule.WebOriginKey] is { Length: > 0 } origin ? origin.TrimEnd('/') + "/notices.json"
            : null;

    /// <summary>The first notice whose optional window contains <paramref name="now"/> (from inclusive, until exclusive).</summary>
    internal static Notice? Select(IEnumerable<Notice> notices, DateTimeOffset now)
        => notices.FirstOrDefault(n => (n.ShowFrom is null || n.ShowFrom <= now) && (n.ShowUntil is null || now < n.ShowUntil));

    /// <summary>
    /// The notices in <paramref name="json"/>, skipping malformed entries (no id or English text, a non-string or unreadable date);
    /// a <c>null</c> date means absent. <c>null</c> when the file itself is invalid.
    /// </summary>
    internal static Notice[]? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("notices", out var list) || list.ValueKind == JsonValueKind.Null) return [];
            if (list.ValueKind != JsonValueKind.Array) return null;
            return list.EnumerateArray().Select(Entry).OfType<Notice>().ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Notice? Entry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (Text(entry, "id") is not { } id || Text(entry, "en") is not { } en) return null;
        if (!Date(entry, "showFrom", out var from) || !Date(entry, "showUntil", out var until)) return null;
        return new Notice(id, from, until, en);
    }

    private static string? Text(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static bool Date(JsonElement entry, string name, out DateTimeOffset? date)
    {
        date = null;
        if (!entry.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out var parsed)) return false;
        date = parsed;
        return true;
    }

    /// <summary>The bot's custom status for <paramref name="notice"/>: its English text, cut to 128 characters without splitting a surrogate pair.</summary>
    internal static string StatusText(Notice notice)
    {
        var text = notice.En;
        if (text.Length <= StatusMaxLength) return text;
        var length = char.IsHighSurrogate(text[StatusMaxLength - 1]) ? StatusMaxLength - 1 : StatusMaxLength;
        return text[..length];
    }

    /// <summary>
    /// Applies one poll result: the notices on success, <c>null</c> on failure, which clears them. Returns <c>true</c> when
    /// this failure starts a new failure streak (the only one worth a warning).
    /// </summary>
    internal bool Apply(Notice[]? notices)
    {
        _notices = notices ?? [];
        var firstFailure = notices is null && !_failing;
        _failing = notices is null;
        return firstFailure;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (NoticesUrl(_config) is not { } url) return;

        using var timer = new PeriodicTimer(PollInterval, _time);
        do
        {
            try
            {
                await PollAsync(url, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (Apply(null))
                    _logger.LogWarning(ex, "Notice poll failed unexpectedly; showing no notice.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Fetches <paramref name="url"/> once, applies the result and raises <see cref="Changed"/>.</summary>
    internal async Task PollAsync(string url, CancellationToken stoppingToken)
    {
        Notice[]? notices = null;
        Exception? error = null;
        var reason = "invalid JSON";
        try
        {
            using var response = await _http.CreateClient(HttpClientName).GetAsync(url, stoppingToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                notices = [];
            else if (response.StatusCode == HttpStatusCode.OK)
                notices = Parse(await response.Content.ReadAsStringAsync(stoppingToken));
            else
                reason = $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            error = ex;
            reason = ex.GetType().Name;
        }

        if (Apply(notices))
            _logger.LogWarning(error, "Could not read the notices from {Url} ({Reason}); showing no notice.", url, reason);

        if (Changed is { } changed)
            await changed();
    }
}
