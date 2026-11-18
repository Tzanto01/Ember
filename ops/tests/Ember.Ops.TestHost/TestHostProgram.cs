using System.Security.Cryptography;
using System.Text.Json;

namespace Ember.Ops.TestHost;

/// <summary>
/// Runs the dashboard against a seeded throwaway Postgres (<see cref="OpsTestDatabase"/>, <see cref="OpsSeed"/>) on
/// http://localhost:5185, trusting loopback and the login <c>e2e@example.test</c>, serving <c>ops/web/dist</c>, with a
/// synthetic host status file kept fresh. <c>--prod-schema</c> migrates emberdb only as far as production has.
/// Sends nothing outside localhost.
/// </summary>
public static class TestHostProgram
{
    public const string Url = "http://localhost:5185";
    public const string Login = "e2e@example.test";

    public static async Task Main(string[] args)
    {
        var productionSchema = args.Contains("--prod-schema");
        args = args.Where(a => a != "--prod-schema").ToArray();
        var opsRoot = OpsRoot();

        await using var database = await OpsTestDatabase.StartAsync(productionSchema, opsRoot);
        await OpsSeed.SeedEmberAsync(database.SuperuserConnection("emberdb"), DateTime.UtcNow);

        var statusDir = Directory.CreateTempSubdirectory("ember-ops-status-").FullName;
        var statusFile = Path.Combine(statusDir, "status.json");
        await WriteStatusAsync(statusFile);
        var upstream = new FakeNoticesUpstream(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        var tokenFile = Path.Combine(statusDir, "notices-token");
        await File.WriteAllTextAsync(tokenFile, upstream.Token);

        var builder = OpsApp.CreateBuilder(["--urls", Url, .. args]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(database.AppSettings())
        {
            ["Ops:Access:TrustedProxies:0"] = "127.0.0.1",
            ["Ops:Access:TrustedProxies:1"] = "::1",
            ["Ops:Access:AllowedLogins:0"] = Login,
            ["Ops:WebRoot"] = Path.Combine(opsRoot, "web", "dist"),
            ["Ops:StatusFile"] = statusFile,
            ["Ops:BuildSha"] = "testhost",
            ["Ops:NoticesUrl"] = FakeNoticesUpstream.ReadUrl,
            ["Ops:NoticesPublishUrl"] = FakeNoticesUpstream.PublishUrl,
            ["Ops:NoticesTokenFile"] = tokenFile,
            ["Logging:LogLevel:Default"] = "Information",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        });
        builder.Services.AddHttpClient(NoticeStore.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);
        builder.Services.AddHttpClient(SystemInfo.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);

        await using var app = OpsApp.Build(builder);
        await OpsApp.InitializeAsync(app);
        await OpsSeed.SeedOpsAsync(database.SuperuserConnection("emberops"), DateTime.UtcNow);

        using var refresh = new CancellationTokenSource();
        var keepFresh = KeepFreshAsync(statusFile, refresh.Token);
        try
        {
            Console.WriteLine($"Ember ops test host on {Url} ({(productionSchema ? "production" : "v2")} schema)");
            await app.RunAsync();
        }
        finally
        {
            await refresh.CancelAsync();
            await keepFresh;
            Directory.Delete(statusDir, recursive: true);
        }
    }

    /// <summary>Writes a synthetic host status file like <c>ops/host/ops-status.sh</c> does.</summary>
    public static Task WriteStatusAsync(string path)
    {
        var now = DateTime.UtcNow;
        var status = new HostStatus(now, null, new("emberdb-20261118-120000.dump", now.AddHours(-6), 3_456_789),
            new("8e324d4synthetic", "f9ff73fsynthetic"),
            [
                new("ember-ember-1", "running", "none"), new("ember-api-1", "running", "healthy"),
                new("ember-staging-api-1", "running", "unhealthy"), new("homeserver-postgres", "running", "healthy"),
            ],
            [new("/", 9L << 30, 110L << 30), new("/srv/data", 3_400L << 30, 3_600L << 30)]);
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(status, JsonSerializerOptions.Web));
    }

    private static async Task KeepFreshAsync(string path, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await WriteStatusAsync(path);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The <c>ops</c> directory: the nearest ancestor of the build output containing <c>Ember.Ops.slnx</c>.</summary>
    public static string OpsRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ember.Ops.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Ember.Ops.slnx not found above the build output.");
    }
}
