using System.Net;
using Ember.Api.Hosting.Metrics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Ember.Api;

/// <summary>Serves <see cref="ApiMetrics.Path"/> on its own Kestrel port, apart from the app's routes.</summary>
public sealed class MetricsServer(RequestMetrics metrics, IConfiguration configuration, ILogger<MetricsServer> logger) : IHostedService, IAsyncDisposable
{
    private WebApplication? _app;

    /// <summary>The address the server listens on once started.</summary>
    public string? Address { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ApiMetrics.Enabled(configuration)) return;
        var key = new MetricsKey(configuration["Ops:MetricsKeyFile"]);
        var port = configuration.GetValue("Ops:MetricsPort", 9090);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Any, port));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet(ApiMetrics.Path, (HttpRequest request, DateTime? since, DateTime? until) =>
            key.Matches(request.Headers.Authorization.Count == 1 ? request.Headers.Authorization[0] : null)
                ? Results.Ok(metrics.Snapshot(since?.ToUniversalTime(), until?.ToUniversalTime()))
                : Results.StatusCode(StatusCodes.Status401Unauthorized));
        await app.StartAsync(cancellationToken);
        _app = app;
        Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        logger.LogInformation("Request metrics on {Address}{Path}", Address, ApiMetrics.Path);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null) await _app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
