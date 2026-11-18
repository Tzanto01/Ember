namespace Ember.Ops;

/// <summary>The <c>Ops</c> configuration section.</summary>
public sealed class OpsOptions
{
    /// <summary>Connection string to emberdb as <c>ember_dashboard</c>, without the password.</summary>
    public string EmberConnection { get; set; } = string.Empty;

    /// <summary>File holding the <c>ember_dashboard</c> password; none when empty.</summary>
    public string? EmberPasswordFile { get; set; }

    /// <summary>Connection string to the emberops database, without the password.</summary>
    public string OpsConnection { get; set; } = string.Empty;

    /// <summary>File holding the <c>emberops</c> password; none when empty.</summary>
    public string? OpsPasswordFile { get; set; }

    public AccessOptions Access { get; set; } = new();

    /// <summary>The status file the host timer writes (<c>ops/host/ops-status.sh</c>).</summary>
    public string StatusFile { get; set; } = "/status/status.json";

    /// <summary><c>owner/name</c> of the GitHub repository whose CI runs are shown; CI is skipped when empty.</summary>
    public string? GitHubRepo { get; set; }

    /// <summary>Branches whose latest CI run is shown; <c>main, test, v2/next</c> when empty.</summary>
    public string[] GitHubBranches { get; set; } = [];

    /// <summary>URL of the published <c>notices.json</c>; skipped when empty.</summary>
    public string? NoticesUrl { get; set; }

    /// <summary>URL the whole notices document is PUT to with the bearer token from <see cref="NoticesTokenFile"/>.</summary>
    public string? NoticesPublishUrl { get; set; } = "https://beta.ember-glow.app/_ops/notices";

    /// <summary>File holding the notices publish token; publishing is off when it is unset, missing, empty or unreadable.</summary>
    public string? NoticesTokenFile { get; set; }

    /// <summary>URL of the published APK <c>latest.json</c>; skipped when empty.</summary>
    public string? ApkUrl { get; set; }

    /// <summary>Commit the dashboard image was built from.</summary>
    public string? BuildSha { get; set; }

    /// <summary>Directory of the built web app; <c>wwwroot</c> under the content root when empty.</summary>
    public string? WebRoot { get; set; }
}

/// <summary>The <c>Ops:Access</c> section: who may use the dashboard. Everything is refused when nothing is configured.</summary>
public sealed class AccessOptions
{
    /// <summary>Remote addresses whose identity headers are believed (the proxy in front of the app).</summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary><c>Tailscale-User-Login</c> values allowed in (case-insensitive).</summary>
    public string[] AllowedLogins { get; set; } = [];

    /// <summary>Tailscale app capability name that grants access through <c>Tailscale-App-Capabilities</c>.</summary>
    public string? AppCapability { get; set; }
}
