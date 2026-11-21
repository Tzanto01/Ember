using System.Threading.RateLimiting;
using Ember.Api.Hosting;
using Ember.Core.Data;
using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ember.Api.Diagnostics;

/// <summary>"Send diagnostics" from the Android app: an anonymous technical report queued for Lookout.</summary>
internal static class DiagnosticsEndpoints
{
    public const string Path = "/api/diagnostics";

    /// <summary>Most uploads waiting for the Lookout sender; at this many, new ones get 503 <c>busy</c>.</summary>
    public const int MaxQueued = 200;

    /// <summary>How long an unsent upload is kept (Lookout's <c>diagnosticsDays</c> for Ember).</summary>
    public static readonly TimeSpan MaxUnsentAge = TimeSpan.FromDays(30);

    private const int RefAttempts = 3;

    public sealed record DiagnosticsCreated(string Ref);

    /// <summary>
    /// <c>POST /api/diagnostics</c>: anonymous for everyone, signed in or not; never reads the user, a cookie or the Authorization
    /// header, so no upload is ever tied to an account. Validates against the server's now (<see cref="DiagnosticsValidation.Errors"/>;
    /// an <c>at</c> outside the window is 400 <c>clock</c>, never dropped), drops unsent uploads older than <see cref="MaxUnsentAge"/>,
    /// answers 503 <c>busy</c> at <see cref="MaxQueued"/> queued, then rate-limits
    /// (<see cref="RateLimits.DiagnosticsKeys"/>), stores the <see cref="DiagnosticsBody"/> under a fresh ref and key (413
    /// <c>too_large</c> over <see cref="DiagnosticsBody.MaxBytes"/>) and answers 201 <c>{ ref }</c>. Logs nothing of the upload.
    /// </summary>
    public static RouteGroupBuilder MapDiagnostics(this RouteGroupBuilder api)
    {
        api.MapPost("/diagnostics", async (DiagnosticsRequest request, HttpContext http, PartitionedRateLimiter<string> limiter,
            EmberDbContext db, TimeProvider time) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            if (DiagnosticsValidation.Errors(request, now) is { Count: > 0 } errors) return Problems.Validation(errors);

            await DeleteExpiredAsync(db, now, http.RequestAborted);
            if (await db.LookoutDiagnostics.CountAsync(http.RequestAborted) >= MaxQueued) return Problems.Busy();

            foreach (var key in RateLimits.DiagnosticsKeys(request.Installation!, http.Connection.RemoteIpAddress))
            {
                using var lease = limiter.AttemptAcquire(key);
                if (!lease.IsAcquired) return RateLimits.Reject(http, lease, TimeSpan.FromDays(1));
            }

            for (var attempt = 1; ; attempt++)
            {
                var reference = DiagnosticsBody.NewRef();
                var body = DiagnosticsBody.Serialize(reference, now, request);
                if (body.Length > DiagnosticsBody.MaxBytes) return Problems.TooLarge();

                db.LookoutDiagnostics.Add(new LookoutDiagnostic
                {
                    Key = Guid.CreateVersion7(time.GetUtcNow()).ToString("N"),
                    Ref = reference,
                    Body = body,
                    CreatedAt = now,
                });
                try
                {
                    await db.SaveChangesAsync(http.RequestAborted);
                    return Results.Json(new DiagnosticsCreated(reference), statusCode: StatusCodes.Status201Created);
                }
                catch (DbUpdateException e) when (attempt < RefAttempts && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                {
                    db.ChangeTracker.Clear();
                }
            }
        });

        return api;
    }

    /// <summary>Deletes the uploads queued longer than <see cref="MaxUnsentAge"/> before <paramref name="now"/> and returns how many.</summary>
    public static Task<int> DeleteExpiredAsync(EmberDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - MaxUnsentAge;
        return db.LookoutDiagnostics.Where(d => d.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
