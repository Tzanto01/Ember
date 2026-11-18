using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ember.Ops;

public sealed record ServiceState(string Name, bool Ok, string? Detail);
public sealed record DatabaseInfo(long SizeBytes, string? LatestMigration);
public sealed record DiskInfo(long FreeBytes, long TotalBytes);
public sealed record BackupInfo(string Name, DateTime At, double AgeHours, long SizeBytes);
public sealed record DeployedShas(string? Production, string? Staging);
public sealed record CiRun(string Branch, string Status, string? Conclusion, string Url, DateTime At, string Sha);
public sealed record ApkInfo(string VersionName, long VersionCode);

/// <summary><c>GET /api/system</c>. A failing part is null and named in <see cref="Errors"/>; it never fails the whole call.</summary>
public sealed record SystemReport(
    DateTime GeneratedAt, DateTime? StatusWrittenAt, IReadOnlyList<ServiceState> Services, DatabaseInfo? Database, DiskInfo? Disk,
    BackupInfo? Backup, DeployedShas? Deployed, IReadOnlyList<CiRun>? Ci, JsonElement? Notice, ApkInfo? Apk, string? DashboardSha,
    IReadOnlyDictionary<string, string> Errors);

/// <summary>The status file <c>ops/host/ops-status.sh</c> writes every minute.</summary>
public sealed record HostStatus(DateTime WrittenAt, DiskInfo? Disk, HostBackup? Backup, DeployedShas? Deployed, IReadOnlyList<HostContainer>? Containers);
public sealed record HostBackup(string Name, DateTime At, long SizeBytes);
public sealed record HostContainer(string Name, string State, string? Health);

/// <summary>Builds the system page from the host status file, Ember's database and the public GitHub, notices and APK URLs.</summary>
public sealed class SystemInfo(IOptions<OpsOptions> options, OpsDatabases db, IHttpClientFactory http, IMemoryCache cache, TimeProvider time, ILogger<SystemInfo> logger)
{
    public const string HttpClientName = "outbound";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);
    private static readonly string[] DefaultBranches = ["main", "test", "v2/next"];

    private sealed record Fetched<T>(T? Value, string? Error);

    public async Task<SystemReport> GetAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var errors = new Dictionary<string, string>();

        var ciTask = Cached("system:ci", TimeSpan.FromMinutes(5), () => CiAsync(o, CancellationToken.None));
        var noticeTask = Cached("system:notice", TimeSpan.FromMinutes(2), () => NoticeAsync(o.NoticesUrl, CancellationToken.None));
        var apkTask = Cached("system:apk", TimeSpan.FromMinutes(2), () => ApkAsync(o.ApkUrl, CancellationToken.None));

        var status = ReadStatus(o.StatusFile, out var statusError);
        if (statusError is not null) errors["status"] = statusError;
        else if (now - status!.WrittenAt > StaleAfter) errors["status"] = $"status file is {(int)(now - status.WrittenAt).TotalMinutes} min old";

        DatabaseInfo? database = null;
        int? overdue = null;
        try
        {
            (database, overdue) = await DatabaseAsync(ct);
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
        if (overdue is { } late) services.Add(new("Reminders advancing", late == 0, late == 0 ? null : $"{late} overdue"));

        var (ci, notice, apk) = (await ciTask, await noticeTask, await apkTask);
        if (ci.Error is not null) errors["ci"] = ci.Error;
        if (notice.Error is not null) errors["notice"] = notice.Error;
        if (apk.Error is not null) errors["apk"] = apk.Error;

        var backup = status?.Backup is { } b ? new BackupInfo(b.Name, b.At, Math.Round((now - b.At).TotalHours, 1), b.SizeBytes) : null;
        return new(now, status?.WrittenAt, services, database, status?.Disk, backup, status?.Deployed, ci.Value, notice.Value, apk.Value,
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

    private async Task<(DatabaseInfo, int)> DatabaseAsync(CancellationToken ct)
    {
        await using var connection = await db.Ember.OpenConnectionAsync(ct);
        await using var reader = await Db.RunAsync(connection,
        [
            Db.Command($"""
                SELECT pg_database_size(current_database()),
                       (SELECT count(*) FROM "Habits" WHERE "NextReminderUtc" < now() - interval '{ReminderQueries.OverdueAfter}')::int
                     + (SELECT count(*) FROM "Routines" WHERE "NextReminderUtc" < now() - interval '{ReminderQueries.OverdueAfter}')::int
                """),
            Db.Command("""SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1"""),
        ], ct);
        await reader.RowAsync(ct);
        var (size, overdue) = (reader.GetInt64(0), reader.GetInt32(1));
        await reader.NextResultAsync(ct);
        var latest = await reader.ReadAsync(ct) ? reader.GetString(0) : null;
        return (new(size, latest), overdue);
    }

    private async Task<Fetched<T>> Cached<T>(string key, TimeSpan ttl, Func<Task<Fetched<T>>> load) =>
        (await cache.GetOrCreateAsync(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = ttl;
            return load();
        }))!;

    private async Task<Fetched<IReadOnlyList<CiRun>>> CiAsync(OpsOptions o, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.GitHubRepo)) return new(null, null);
        var branches = o.GitHubBranches is { Length: > 0 } configured ? configured : DefaultBranches;
        return await Fetch<IReadOnlyList<CiRun>>("ci", async client =>
        {
            var runs = await Task.WhenAll(branches.Select(async branch =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{o.GitHubRepo.Trim()}/actions/runs?branch={Uri.EscapeDataString(branch)}&per_page=1");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await client.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.GetProperty("workflow_runs") is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() == 0)
                    return null;
                var run = list[0];
                return new CiRun(branch, run.GetProperty("status").GetString() ?? "unknown",
                    run.GetProperty("conclusion").ValueKind == JsonValueKind.String ? run.GetProperty("conclusion").GetString() : null,
                    run.GetProperty("html_url").GetString() ?? string.Empty, run.GetProperty("created_at").GetDateTime().ToUniversalTime(),
                    run.GetProperty("head_sha").GetString() ?? string.Empty);
            }));
            return runs.OfType<CiRun>().ToList();
        });
    }

    private Task<Fetched<JsonElement?>> NoticeAsync(string? url, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(url)
            ? Task.FromResult(new Fetched<JsonElement?>(null, null))
            : Fetch<JsonElement?>("notice", async client =>
            {
                using var response = await client.GetAsync(url, ct);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
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
