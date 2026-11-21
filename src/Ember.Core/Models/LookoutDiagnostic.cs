namespace Ember.Core.Models;

/// <summary>
/// One anonymous diagnostics upload waiting for Lookout's <c>PUT diagnostics/{key}</c>, sent by the Lookout sender after every user
/// deletion and ticket, in <see cref="Id"/> order. Holds no user, installation or IP; deleted once Lookout answers for good or after
/// 30 days unsent. Its own table so backups can leave its data out.
/// </summary>
public class LookoutDiagnostic
{
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
}
