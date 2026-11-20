using System.Net;
using Ember.Ops.TestHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Ember.Ops.Tests;

/// <summary>The ops app in-process against a fixture's databases. Each request carries its own remote address in <see cref="RemoteIpHeader"/>.</summary>
public sealed class OpsApiFactory : WebApplicationFactory<Program>
{
    public const string RemoteIpHeader = "X-Test-Remote-Ip";
    public const string UntrustedIp = "10.9.9.9";
    public const string HubIp = "10.7.7.7";
    public const string LookoutKey = "synthetic-lookout-key-for-tests-0123456789";

    private readonly DatabaseFixture _database;
    private readonly HttpMessageHandler? _notices;
    private readonly string _noticesTokenFile;
    private readonly string? _hubAddress;
    private readonly string _lookoutKeyFile;
    private readonly string _keyDirectory = Directory.CreateTempSubdirectory("ember-ops-key-").FullName;
    private readonly string _workDirectory = Directory.CreateTempSubdirectory("ember-ops-work-").FullName;

    /// <summary>The host status file the app reads; missing unless a test writes it before the first request.</summary>
    public string StatusFile => Path.Combine(_workDirectory, "missing-status.json");

    public OpsApiFactory(DatabaseFixture database, HttpMessageHandler? notices = null, string? noticesToken = "synthetic-test-token\n",
        string? hubAddress = null, string? lookoutKey = null)
    {
        (_database, _notices) = (database, notices);
        _noticesTokenFile = Path.Combine(_workDirectory, "notices-token");
        if (noticesToken is not null) File.WriteAllText(_noticesTokenFile, noticesToken);
        _hubAddress = hubAddress;
        _lookoutKeyFile = Path.Combine(_keyDirectory, "lookout-key");
        if (lookoutKey is not null) File.WriteAllText(_lookoutKeyFile, lookoutKey);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = _database.Database.AppSettings();
            settings["Ops:StatusFile"] = StatusFile;
            if (_hubAddress is not null)
            {
                settings["Ops:Lookout:HubAddress"] = _hubAddress;
                settings["Ops:Lookout:KeyFile"] = _lookoutKeyFile;
            }
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
        if (!disposing) return;
        foreach (var directory in new[] { _workDirectory, _keyDirectory })
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    /// <summary>A client that arrives from <paramref name="remoteIp"/>.</summary>
    public HttpClient ClientFrom(string remoteIp)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(RemoteIpHeader, remoteIp);
        return client;
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
