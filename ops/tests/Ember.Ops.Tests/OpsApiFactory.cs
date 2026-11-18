using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ember.Ops.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Ember.Ops.Tests;

/// <summary>The dashboard in-process against a fixture's databases. Each request carries its own remote address in <see cref="RemoteIpHeader"/>.</summary>
public sealed class OpsApiFactory : WebApplicationFactory<Program>
{
    public const string RemoteIpHeader = "X-Test-Remote-Ip";
    public const string TrustedIp = "10.1.2.3";
    public const string UntrustedIp = "10.9.9.9";
    public const string Login = "operator@example.test";
    public const string Capability = "example.test/cap/ember-ops";

    private readonly DatabaseFixture _database;
    private readonly string[] _trusted;
    private readonly string[] _logins;
    private readonly string? _capability;
    private readonly string? _allowedHosts;
    private readonly HttpMessageHandler? _notices;
    private readonly string _noticesTokenFile;
    private readonly string _webRoot = Directory.CreateTempSubdirectory("ember-ops-web-").FullName;

    /// <summary>The host status file the dashboard reads; missing unless a test writes it before the first request.</summary>
    public string StatusFile => Path.Combine(_webRoot, "missing-status.json");

    public OpsApiFactory(DatabaseFixture database, string[]? trusted = null, string[]? logins = null, string? capability = Capability, string? allowedHosts = null,
        HttpMessageHandler? notices = null, string? noticesToken = "synthetic-test-token\n")
    {
        (_database, _trusted, _logins, _capability, _allowedHosts, _notices) = (database, trusted ?? [TrustedIp], logins ?? [Login], capability, allowedHosts, notices);
        _noticesTokenFile = Path.Combine(_webRoot, "notices-token");
        if (noticesToken is not null) File.WriteAllText(_noticesTokenFile, noticesToken);
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<html><body>ember-ops-spa</body></html>");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = _database.Database.AppSettings();
            settings["Ops:WebRoot"] = _webRoot;
            settings["Ops:StatusFile"] = StatusFile;
            for (var i = 0; i < _trusted.Length; i++) settings[$"Ops:Access:TrustedProxies:{i}"] = _trusted[i];
            for (var i = 0; i < _logins.Length; i++) settings[$"Ops:Access:AllowedLogins:{i}"] = _logins[i];
            if (_capability is not null) settings["Ops:Access:AppCapability"] = _capability;
            if (_allowedHosts is not null) settings["AllowedHosts"] = _allowedHosts;
            if (_notices is not null)
            {
                settings["Ops:NoticesUrl"] = FakeNoticesUpstream.ReadUrl;
                settings["Ops:NoticesPublishUrl"] = FakeNoticesUpstream.PublishUrl;
                settings["Ops:NoticesTokenFile"] = _noticesTokenFile;
            }
            config.AddInMemoryCollection(settings);
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.AddSingleton<TimeProvider>(_database.Time);
            services.AddTransient<IStartupFilter, RemoteIpFilter>();
            if (_notices is not null) services.AddHttpClient(NoticeStore.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _notices);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_webRoot)) Directory.Delete(_webRoot, recursive: true);
    }

    /// <summary>A cookie-keeping client that arrives from <paramref name="remoteIp"/> with the given identity headers.</summary>
    public HttpClient ClientFrom(string remoteIp = TrustedIp, string? login = Login, string? capabilities = null, string scheme = "https")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"{scheme}://localhost") });
        client.DefaultRequestHeaders.Add(RemoteIpHeader, remoteIp);
        if (login is not null) client.DefaultRequestHeaders.Add("Tailscale-User-Login", login);
        if (capabilities is not null) client.DefaultRequestHeaders.Add("Tailscale-App-Capabilities", capabilities);
        return client;
    }

    /// <summary>Fetches the CSRF token (and cookie) and adds the token header to every later request of the client.</summary>
    public static async Task<JsonElement> StartSessionAsync(HttpClient client)
    {
        var session = await client.GetFromJsonAsync<JsonElement>("/api/session");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.GetProperty("csrfToken").GetString());
        return session;
    }

    private sealed class RemoteIpFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(RemoteIpHeader, out var ip)) context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
