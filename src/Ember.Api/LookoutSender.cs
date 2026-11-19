using Ember.Core.Data;
using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api;

/// <summary>
/// Delivers <see cref="LookoutOutbox"/> rows to Lookout's ingest one at a time, run by <c>--lookout-sender</c> in its own container:
/// user deletions first, then tickets, each in id order. Deleting an account cascades away that user's unsent tickets, so a deletion
/// still follows every ticket of that user, and a ticket stuck on a quota can't hold up an erasure. Sends nothing unless
/// <see cref="LookoutIngest.TryGetEndpoint"/> accepts the configuration. A transient failure keeps the row at the head and waits
/// <see cref="LookoutIngest.Backoff"/>; a permanent refusal parks a ticket and moves on, while a deletion is never parked, only
/// retried. Logs row id, kind and status or exception type only.
/// </summary>
internal sealed class LookoutSender(IServiceScopeFactory scopes, HttpClient http, IConfiguration configuration, TimeProvider time,
    ILogger<LookoutSender> logger) : BackgroundService
{
    /// <summary>How long the sender waits when the outbox is empty.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private int _failures;

    /// <summary>The sender host: environment configuration, console logging, the database, <see cref="TimeProvider.System"/> and this loop only.</summary>
    public static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<EmberDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_ => LookoutIngest.CreateClient());
        builder.Services.AddHostedService<LookoutSender>();
        return builder.Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!LookoutIngest.TryGetEndpoint(configuration, out _, out _))
        {
            logger.LogWarning("Lookout ingest is not configured; feedback stays in the outbox.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = await StepAsync(stoppingToken);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, time, stoppingToken);
        }
    }

    /// <summary>
    /// Handles the head of the outbox once and returns how long to wait before the next step: zero after a delivery, a park or a row
    /// whose feedback is gone, <see cref="PollInterval"/> when the outbox is empty, the backoff after a transient failure. Does
    /// nothing and returns <see cref="PollInterval"/> when Lookout isn't configured.
    /// </summary>
    internal async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        if (!LookoutIngest.TryGetEndpoint(configuration, out var endpoint, out var keyFile)) return PollInterval;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EmberDbContext>();
        var row = await db.LookoutOutbox.AsNoTracking().Where(o => o.SentAt == null && o.ParkedAt == null)
            .OrderBy(o => o.Kind == LookoutOutbox.UserDelete ? 0 : 1).ThenBy(o => o.Id).FirstOrDefaultAsync(cancellationToken);
        if (row is null) return PollInterval;
        var rows = db.LookoutOutbox.Where(o => o.Id == row.Id);

        HttpMethod method;
        string path;
        byte[]? body = null;
        if (row.Kind == LookoutOutbox.UserDelete)
        {
            method = HttpMethod.Delete;
            path = LookoutIngest.UserPath(row.UserRef!);
        }
        else
        {
            var feedback = await db.Feedback.AsNoTracking().Include(f => f.Images.OrderBy(i => i.Id))
                .FirstOrDefaultAsync(f => f.Id == row.FeedbackId, cancellationToken);
            if (feedback is null)
            {
                await rows.ExecuteDeleteAsync(cancellationToken);
                return TimeSpan.Zero;
            }
            method = HttpMethod.Put;
            path = LookoutIngest.TicketPath(row.Key!);
            body = LookoutIngest.TicketBody(feedback, row.UserRef);
        }

        int status;
        try
        {
            using var response = await LookoutIngest.SendAsync(http, endpoint, keyFile, method, path, body, cancellationToken);
            status = (int)response.StatusCode;
        }
        catch (Exception e) when (LookoutIngest.IsTransient(e, cancellationToken))
        {
            return Failed(row, e.GetType().Name);
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (status is >= 200 and < 300)
        {
            if (row.Kind == LookoutOutbox.UserDelete) await rows.ExecuteDeleteAsync(cancellationToken);
            else await rows.ExecuteUpdateAsync(s => s.SetProperty(o => o.SentAt, now), cancellationToken);
            _failures = 0;
            return TimeSpan.Zero;
        }
        if (!LookoutIngest.IsRefused(status) || row.Kind == LookoutOutbox.UserDelete) return Failed(row, status.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await rows.ExecuteUpdateAsync(s => s.SetProperty(o => o.ParkedAt, now).SetProperty(o => o.ParkedStatus, status), cancellationToken);
        logger.LogError("Lookout refused outbox row {Id} ({Kind}) with {Status}; parked.", row.Id, row.Kind, status);
        _failures = 0;
        return TimeSpan.Zero;
    }

    private TimeSpan Failed(LookoutOutbox row, string reason)
    {
        var wait = LookoutIngest.Backoff(++_failures);
        logger.LogWarning("Lookout outbox row {Id} ({Kind}) failed with {Reason}; retrying in {Wait}.", row.Id, row.Kind, reason, wait);
        return wait;
    }
}
