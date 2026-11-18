using Ember.Bot.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Ember.Bot.Services;

/// <summary>One entry of <c>/notices.json</c>; <see cref="ShowFrom"/> and <see cref="ShowUntil"/> are optional.</summary>
public sealed record Notice(string Id, DateTimeOffset? ShowFrom, DateTimeOffset? ShowUntil, string En);

/// <summary>
/// Polls the live notices file every 5 minutes (<c>Ember:NoticesUrl</c>, or <c>Ember:WebOrigin</c> + <c>/notices.json</c>).
/// A failed request, non-200 or invalid JSON keeps the last known notices for at most 30 minutes, then clears them.
/// Raises <see cref="Changed"/> after every poll; never DMs anyone.
/// </summary>
public sealed class NoticePoller : BackgroundService
{
    internal const string NoticesUrlKey = "Ember:NoticesUrl";
    internal const string HttpClientName = "notices";
    internal const int StatusMaxLength = 128;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan KeepStaleFor = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly TimeProvider _time;
    private readonly ILogger<NoticePoller> _logger;
    private volatile Notice[] _notices = [];
    private DateTimeOffset? _lastSuccess;
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

    /// <summary>The notices in <paramref name="json"/>, skipping entries without an id or English text; <c>null</c> when the file is invalid.</summary>
    internal static Notice[]? Parse(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<NoticesFile>(json, Json);
            if (file is null) return null;
            return (file.Notices ?? [])
                .Where(n => n is not null && !string.IsNullOrWhiteSpace(n.Id) && !string.IsNullOrWhiteSpace(n.En))
                .Select(n => new Notice(n!.Id!, n.ShowFrom, n.ShowUntil, n.En!.Trim()))
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
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
    /// Applies one poll result: the response body on success, <c>null</c> on failure. Returns <c>true</c> when this
    /// failure starts a new failure streak (the only one worth a warning).
    /// </summary>
    internal bool Apply(string? body, DateTimeOffset now)
    {
        var parsed = body is null ? null : Parse(body);
        if (parsed is not null)
        {
            _notices = parsed;
            _lastSuccess = now;
            _failing = false;
            return false;
        }

        if (_lastSuccess is null || now - _lastSuccess > KeepStaleFor)
            _notices = [];

        var firstFailure = !_failing;
        _failing = true;
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
                _logger.LogWarning(ex, "Notice poll failed unexpectedly.");
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

    private async Task PollAsync(string url, CancellationToken stoppingToken)
    {
        string? body = null;
        string reason;
        try
        {
            using var response = await _http.CreateClient(HttpClientName).GetAsync(url, stoppingToken);
            if (response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                body = await response.Content.ReadAsStringAsync(stoppingToken);
                reason = "invalid JSON";
            }
            else
            {
                reason = $"HTTP {(int)response.StatusCode}";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
        {
            reason = ex.GetType().Name;
        }

        if (Apply(body, _time.GetUtcNow()))
            _logger.LogWarning("Could not read the notices from {Url} ({Reason}); keeping the last known notices for up to 30 minutes.", url, reason);

        if (Changed is { } changed)
            await changed();
    }

    private sealed class NoticesFile
    {
        public List<NoticeEntry?>? Notices { get; set; }
    }

    private sealed class NoticeEntry
    {
        public string? Id { get; set; }
        public DateTimeOffset? ShowFrom { get; set; }
        public DateTimeOffset? ShowUntil { get; set; }
        public string? En { get; set; }
    }
}
