using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ember.Ops;

public sealed record ServiceState(string Name, bool Ok, string? Detail);
/// <param name="Connections">
/// Backends attached to a database on the whole Postgres server (client connections plus any autovacuum worker; without <c>pg_read_all_stats</c> other roles' backend types are hidden, so they are not filtered by type); <paramref name="EmberConnections"/> those on emberdb.
/// </param>
public sealed record DatabaseInfo(long SizeBytes, string? LatestMigration, int Connections, int EmberConnections, int MaxConnections);
public sealed record DiskInfo(long FreeBytes, long TotalBytes);
public sealed record HostDisk(string Mount, long FreeBytes, long TotalBytes);
public sealed record BackupInfo(string Name, DateTime At, double AgeHours, long SizeBytes);
public sealed record DeployedShas(string? Production, string? Staging);
public sealed record CiRun(string Branch, string Status, string? Conclusion, string Url, DateTime At, string Sha);
public sealed record ApkInfo(string VersionName, long VersionCode);

/// <summary>The system report. A failing part is null and named in <see cref="Errors"/>; it never fails the whole call.</summary>
public sealed record SystemReport(
    DateTime GeneratedAt, DateTime? StatusWrittenAt, IReadOnlyList<ServiceState> Services, DatabaseInfo? Database, IReadOnlyList<HostDisk>? Disks,
    BackupInfo? Backup, DeployedShas? Deployed, IReadOnlyList<CiRun>? Ci, JsonElement? Notice, ApkInfo? Apk, string? DashboardSha,
    IReadOnlyDictionary<string, string> Errors);

/// <summary>The status file <c>ops/host/ops-status.sh</c> writes every minute. <see cref="Disk"/> is the single <c>/home</c> disk of files written before <see cref="Disks"/>.</summary>
public sealed record HostStatus(DateTime WrittenAt, DiskInfo? Disk, HostBackup? Backup, DeployedShas? Deployed, IReadOnlyList<HostContainer>? Containers,
    IReadOnlyList<HostDisk>? Disks = null);
public sealed record HostBackup(string Name, DateTime At, long SizeBytes);
public sealed record HostContainer(string Name, string State, string? Health);

/// <summary>Builds the system page from the host status file, Ember's database and the public GitHub, notices and APK URLs.</summary>
public sealed class SystemInfo(IOptions<OpsOptions> options, OpsDatabases db, IHttpClientFactory http, IMemoryCache cache, TimeProvider time, ILogger<SystemInfo> logger)
{
    public const string HttpClientName = "outbound";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FailureCache = TimeSpan.FromSeconds(30);
    private static readonly string[] DefaultBranches = ["main", "test", "v2/next"];

    private static readonly TimeZoneInfo Berlin = FindBerlin();

    private sealed record Fetched<T>(T? Value, string? Error)
    {
        /// <summary>Overrides the cache lifetime, e.g. until a rate limit resets.</summary>
        public TimeSpan? Ttl { get; init; }
    }

    private sealed record CiSnapshot(string ETag, IReadOnlyList<CiRun> Runs);

    private volatile CiSnapshot? ciSnapshot;
    private long ciRateLimitedUntil;

    public async Task<SystemReport> GetAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var errors = new Dictionary<string, string>();

        var ciTask = Cached("system:ci", TimeSpan.FromMinutes(10), () => CiAsync(o, CancellationToken.None), TimeSpan.FromMinutes(10));
        var noticeTask = Cached("system:notice", TimeSpan.FromMinutes(2), () => NoticeAsync(o.NoticesUrl, CancellationToken.None));
        var apkTask = Cached("system:apk", TimeSpan.FromMinutes(2), () => ApkAsync(o.ApkUrl, CancellationToken.None));

        var status = ReadStatus(o.StatusFile, out var statusError);
        if (statusError is not null) errors["status"] = statusError;
        else if (now - status!.WrittenAt > StaleAfter) errors["status"] = $"status file is {(int)(now - status.WrittenAt).TotalMinutes} min old";

        DatabaseInfo? database = null;
        int? overdue = null;
        try
        {
            database = await DatabaseAsync(ct);
            overdue = await ReminderQueries.OverdueAsync(db.Ember, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "System: database read failed");
            errors["database"] = "database unavailable";
        }

        var services = (status?.Containers ?? []).Select(c =>
        {
            var health = string.IsNullOrEmpty(c.Health) || c.Health == "none" ? null : c.Health;
            return new ServiceState(c.Name, c.State == "running" && health is null or "healthy", health is null ? c.State : $"{c.State}, {health}");
        }).ToList();
        if (overdue is { } late) services.Add(new(Lookout.LookoutPages.RemindersAdvancing, late == 0, late == 0 ? null : $"{late} overdue"));

        var (ci, notice, apk) = (await ciTask, await noticeTask, await apkTask);
        if (ci.Error is not null) errors["ci"] = ci.Error;
        if (notice.Error is not null) errors["notice"] = notice.Error;
        if (apk.Error is not null) errors["apk"] = apk.Error;

        var backup = status?.Backup is { } b ? new BackupInfo(b.Name, b.At, Math.Round((now - b.At).TotalHours, 1), b.SizeBytes) : null;
        IReadOnlyList<HostDisk>? disks = status?.Disks ?? (status?.Disk is { } d ? [new("/home", d.FreeBytes, d.TotalBytes)] : null);
        return new(now, status?.WrittenAt, services, database, disks, backup, status?.Deployed, ci.Value, notice.Value, apk.Value,
            string.IsNullOrWhiteSpace(o.BuildSha) ? null : o.BuildSha, errors);
    }

    /// <summary>Reads the host status file; null with an error message when it is missing or unreadable.</summary>
    public static HostStatus? ReadStatus(string path, out string? error)
    {
        try
        {
            var status = JsonSerializer.Deserialize<HostStatus>(File.ReadAllText(path), JsonSerializerOptions.Web);
            error = status is null ? "status file unreadable" : null;
            return status;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            error = "status file missing";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            error = "status file unreadable";
        }
        return null;
    }

    private async Task<DatabaseInfo> DatabaseAsync(CancellationToken ct)
    {
        await using var connection = await db.Ember.OpenConnectionAsync(ct);
        await using var reader = await Db.RunAsync(connection,
        [
            Db.Command("SELECT pg_database_size(current_database())"),
            Db.Command("""SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1"""),
            Db.Command("""
                SELECT count(*)::int, count(*) FILTER (WHERE datname = current_database())::int, current_setting('max_connections')::int
                FROM pg_stat_activity WHERE datid IS NOT NULL
                """),
        ], ct);
        await reader.RowAsync(ct);
        var size = reader.GetInt64(0);
        await reader.NextResultAsync(ct);
        var latest = await reader.ReadAsync(ct) ? reader.GetString(0) : null;
        await reader.NextResultAsync(ct);
        await reader.RowAsync(ct);
        return new(size, latest, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private async Task<Fetched<T>> Cached<T>(string key, TimeSpan ttl, Func<Task<Fetched<T>>> load, TimeSpan? failureTtl = null) =>
        (await cache.GetOrCreateAsync(key, async e =>
        {
            var fetched = await load();
            e.AbsoluteExpirationRelativeToNow = fetched.Ttl ?? (fetched.Error is null ? ttl : failureTtl ?? FailureCache);
            return fetched;
        }))!;

    private async Task<Fetched<IReadOnlyList<CiRun>>> CiAsync(OpsOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.GitHubRepo)) return new(null, null);
        if (CiRateLimited() is { } limited) return limited;
        var branches = o.GitHubBranches is { Length: > 0 } configured ? configured : DefaultBranches;
        var fetched = await Fetch<IReadOnlyList<CiRun>>("ci", async client =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{o.GitHubRepo.Trim()}/actions/runs?per_page=50");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            var last = ciSnapshot;
            if (last is not null) request.Headers.TryAddWithoutValidation("If-None-Match", last.ETag);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotModified && last is not null) return last.Runs;
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0"
                && response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var resetAt)
                && resetAt > 0 && resetAt < DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                Volatile.Write(ref ciRateLimitedUntil, Math.Min(resetAt, time.GetUtcNow().AddHours(1).ToUnixTimeSeconds()));
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var list = doc.RootElement.GetProperty("workflow_runs");
            IEnumerable<JsonElement> all = list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
            var newest = all
                .Where(run => run.GetProperty("head_branch") is { ValueKind: JsonValueKind.String } branch && branches.Contains(branch.GetString()))
                .Select(run => new CiRun(run.GetProperty("head_branch").GetString()!, run.GetProperty("status").GetString() ?? "unknown",
                    run.GetProperty("conclusion").ValueKind == JsonValueKind.String ? run.GetProperty("conclusion").GetString() : null,
                    run.GetProperty("html_url").GetString() ?? string.Empty, run.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                    run.GetProperty("head_sha").GetString() ?? string.Empty))
                .GroupBy(run => run.Branch)
                .ToDictionary(g => g.Key, g => g.MaxBy(run => run.At)!);
            IReadOnlyList<CiRun> runs = branches.Where(newest.ContainsKey).Select(b => newest[b]).ToList();
            ciSnapshot = response.Headers.ETag is { } etag ? new(etag.ToString(), runs) : null;
            return runs;
        });
        return CiRateLimited() ?? fetched;
    }

    private Fetched<IReadOnlyList<CiRun>>? CiRateLimited()
    {
        var until = DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref ciRateLimitedUntil));
        var wait = until - time.GetUtcNow();
        if (wait <= TimeSpan.Zero) return null;
        var zone = Berlin == TimeZoneInfo.Utc ? " UTC" : string.Empty;
        return new(null, $"GitHub rate limit, back at {TimeZoneInfo.ConvertTime(until, Berlin):HH:mm}{zone}") { Ttl = wait };
    }

    private static TimeZoneInfo FindBerlin()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private Task<Fetched<JsonElement?>> NoticeAsync(string? url, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(url)
            ? Task.FromResult(new Fetched<JsonElement?>(null, null))
            : Fetch<JsonElement?>("notice", async client =>
            {
                using var response = await client.GetAsync(url, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return JsonDocument.Parse("""{"notices":[]}""").RootElement.Clone();
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                return doc.RootElement.Clone();
            });

    private Task<Fetched<ApkInfo>> ApkAsync(string? url, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(url)
            ? Task.FromResult(new Fetched<ApkInfo>(null, null))
            : Fetch("apk", async client =>
            {
                using var response = await client.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                return new ApkInfo(doc.RootElement.GetProperty("versionName").GetString() ?? string.Empty,
                    doc.RootElement.GetProperty("versionCode").GetInt64());
            });

    private async Task<Fetched<T>> Fetch<T>(string section, Func<HttpClient, Task<T?>> load)
    {
        try
        {
            return new(await load(http.CreateClient(HttpClientName)), null);
        }
        catch (HttpRequestException e) when (e.StatusCode is { } code)
        {
            return new(default, $"HTTP {(int)code}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            logger.LogWarning(e, "System: {Section} fetch failed", section);
            return new(default, e is TaskCanceledException ? "timed out" : "unavailable");
        }
    }
}
