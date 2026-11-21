using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Models;

/// <summary>
/// One anonymous diagnostics upload waiting for Lookout's <c>PUT diagnostics/{key}</c>, sent by the Lookout sender after every user
/// deletion and ticket, in <see cref="Id"/> order. Holds no user, installation or IP; deleted once Lookout answers for good or after
/// <see cref="MaxUnsentAge"/> unsent. Its own table so backups can leave its data out.
/// </summary>
public class LookoutDiagnostic
{
    /// <summary>How long an unsent upload is kept (Lookout's <c>diagnosticsDays</c> for Ember).</summary>
    public static readonly TimeSpan MaxUnsentAge = TimeSpan.FromDays(30);

    /// <summary>Identity; the send order.</summary>
    public long Id { get; set; }

    /// <summary>The idempotency key (version 7 GUID, 32 hex digits).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The public reference code (<c>EMB-</c> and 8 Crockford base32 characters) the user quotes, also inside <see cref="Body"/>.</summary>
    public string Ref { get; set; } = string.Empty;

    /// <summary>The exact UTF-8 JSON bytes sent to Lookout.</summary>
    public byte[] Body { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    /// <summary>When the sender may try again after a deferred answer; null until the first one.</summary>
    public DateTime? NextAttemptAt { get; set; }

    /// <summary>How many sends were deferred.</summary>
    public int Attempts { get; set; }

    /// <summary>Deletes the uploads in <paramref name="diagnostics"/> queued longer than <see cref="MaxUnsentAge"/> before <paramref name="now"/> and returns how many.</summary>
    public static Task<int> DeleteExpiredAsync(IQueryable<LookoutDiagnostic> diagnostics, DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now - MaxUnsentAge;
        return diagnostics.Where(d => d.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
