using Ember.Api.Feedback;
using Ember.Api.Lookout;
using Ember.Core;
using Ember.Core.Data;
using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api;

/// <summary>
/// Delivers <see cref="LookoutOutbox"/> and <see cref="LookoutDiagnostic"/> rows to Lookout's ingest one at a time, run by
/// <c>--lookout-sender</c> in its own container: user deletions first, then tickets, each in id order, then diagnostics uploads in id
/// order. Deleting an account cascades away that user's unsent tickets, so a deletion still follows every ticket of that user, and a
/// ticket stuck on a quota can't hold up an erasure. Sends nothing unless <see cref="LookoutIngest.TryGetEndpoint"/> accepts the
/// configuration. A transient failure keeps an outbox row at the head and waits <see cref="LookoutIngest.Backoff"/>; a permanent
/// refusal parks a ticket and moves on, while a deletion is never parked, only retried. With
/// <see cref="FeedbackRetention.DeleteSentCopiesSetting"/> a confirmed anonymous report and the pictures of a
/// confirmed linked one are deleted at once. A diagnostics upload never holds up the
/// outbox: each step first drops uploads unsent for <see cref="LookoutDiagnostic.MaxUnsentAge"/>; an upload Lookout accepted or
/// refused for good is deleted, and any other answer defers only that upload by its own backoff (<see cref="LookoutDiagnostic.NextAttemptAt"/>).
/// Logs row id, kind and status or exception type only.
/// </summary>
internal sealed class LookoutSender(IServiceScopeFactory scopes, HttpClient http, IConfiguration configuration, TimeProvider time,
    ILogger<LookoutSender> logger) : BackgroundService
{
    /// <summary>How long the sender waits when the outbox is empty.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private int _failures;

    /// <summary>The sender host: environment configuration, console logging, Ember's Core (<see cref="ServiceCollectionExtensions.AddEmberCore"/>) and this loop only.</summary>
    public static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddEmberCore(builder.Configuration);
        builder.Services.AddSingleton(_ => LookoutIngest.CreateClient());
        builder.Services.AddHostedService<LookoutSender>();
        return builder.Build();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!LookoutIngest.TryGetEndpoint(configuration, out _, out _))
        {
            logger.LogWarning("Lookout ingest is not configured; feedback and diagnostics stay queued.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = await StepAsync(stoppingToken);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, time, stoppingToken);
        }
    }

    /// <summary>
    /// Drops expired diagnostics uploads, then handles the head of the outbox once, or with the outbox empty the next due diagnostics
    /// upload, and returns how long to wait before the next step: zero after a delivery, a park, a row whose feedback is gone or any
    /// answer to an upload, <see cref="PollInterval"/> when nothing is due, the backoff after a transient outbox failure. Does
    /// nothing and returns <see cref="PollInterval"/> when Lookout isn't configured.
    /// </summary>
    internal async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        if (!LookoutIngest.TryGetEndpoint(configuration, out var endpoint, out var keyFile)) return PollInterval;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EmberDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        if (await LookoutDiagnostic.DeleteExpiredAsync(db.LookoutDiagnostics, now, cancellationToken) is > 0 and var expired)
            logger.LogInformation("Dropped {Count} diagnostics uploads unsent for 30 days.", expired);
        var row = await db.LookoutOutbox.AsNoTracking().Where(o => o.SentAt == null && o.ParkedAt == null)
            .OrderBy(o => o.Kind == LookoutOutbox.UserDelete ? 0 : 1).ThenBy(o => o.Id).FirstOrDefaultAsync(cancellationToken);
        if (row is null) return await StepDiagnosticAsync(db, endpoint, keyFile, now, cancellationToken);
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
            using var response = await LookoutIngest.SendAsync(db, http, endpoint, keyFile, method, path, body, cancellationToken, logger);
            status = (int)response.StatusCode;
        }
        catch (Exception e) when (LookoutIngest.IsTransient(e, cancellationToken))
        {
            return Failed(row, e.GetType().Name);
        }

        now = time.GetUtcNow().UtcDateTime;
        if (LookoutIngest.IsConfirmed(row.Kind, status))
        {
            var deleteCopy = configuration.GetValue(FeedbackRetention.DeleteSentCopiesSetting, false);
            if (row.Kind == LookoutOutbox.UserDelete) await rows.ExecuteDeleteAsync(cancellationToken);
            else if (deleteCopy && row.UserRef is null) await db.Feedback.Where(f => f.Id == row.FeedbackId).ExecuteDeleteAsync(cancellationToken);
            else
            {
                await rows.ExecuteUpdateAsync(s => s.SetProperty(o => o.SentAt, now), cancellationToken);
                if (deleteCopy) await db.FeedbackImages.Where(i => i.FeedbackId == row.FeedbackId).ExecuteDeleteAsync(cancellationToken);
            }
            _failures = 0;
            return TimeSpan.Zero;
        }
        if (!LookoutIngest.IsRefused(status) || row.Kind == LookoutOutbox.UserDelete) return Failed(row, status.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await rows.ExecuteUpdateAsync(s => s.SetProperty(o => o.ParkedAt, now).SetProperty(o => o.ParkedStatus, status), cancellationToken);
        logger.LogError("Lookout refused outbox row {Id} ({Kind}) with {Status}; parked.", row.Id, row.Kind, status);
        _failures = 0;
        return TimeSpan.Zero;
    }

    private async Task<TimeSpan> StepDiagnosticAsync(EmberDbContext db, Uri endpoint, string keyFile, DateTime now, CancellationToken cancellationToken)
    {
        var row = await db.LookoutDiagnostics.AsNoTracking().Where(d => d.NextAttemptAt == null || d.NextAttemptAt <= now)
            .OrderBy(d => d.Id).FirstOrDefaultAsync(cancellationToken);
        if (row is null) return PollInterval;
        var rows = db.LookoutDiagnostics.Where(d => d.Id == row.Id);

        string reason;
        try
        {
            using var response = await LookoutIngest.SendAsync(db, http, endpoint, keyFile, HttpMethod.Put, LookoutIngest.DiagnosticsPath(row.Key),
                row.Body, cancellationToken, logger);
            var status = (int)response.StatusCode;
            if (status is 200 or 201 || LookoutIngest.IsRefused(status))
            {
                await rows.ExecuteDeleteAsync(cancellationToken);
                if (status is not (200 or 201)) logger.LogError("Lookout refused diagnostics row {Id} with {Status}; dropped.", row.Id, status);
                return TimeSpan.Zero;
            }
            reason = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (LookoutIngest.IsTransient(e, cancellationToken))
        {
            reason = e.GetType().Name;
        }

        var attempts = row.Attempts + 1;
        var wait = LookoutIngest.Backoff(attempts);
        var next = time.GetUtcNow().UtcDateTime + wait;
        await rows.ExecuteUpdateAsync(s => s.SetProperty(d => d.Attempts, attempts).SetProperty(d => d.NextAttemptAt, next), cancellationToken);
        logger.LogWarning("Lookout diagnostics row {Id} failed with {Reason}; retrying in {Wait}.", row.Id, reason, wait);
        return TimeSpan.Zero;
    }

    private TimeSpan Failed(LookoutOutbox row, string reason)
    {
        var wait = LookoutIngest.Backoff(++_failures);
        logger.LogWarning("Lookout outbox row {Id} ({Kind}) failed with {Reason}; retrying in {Wait}.", row.Id, row.Kind, reason, wait);
        return wait;
    }
}
