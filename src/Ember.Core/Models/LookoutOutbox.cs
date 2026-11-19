namespace Ember.Core.Models;

/// <summary>
/// One request for Lookout's ingest, sent by the Lookout sender strictly in <see cref="Id"/> order. A <see cref="LookoutOutbox.Ticket"/>
/// row is deleted with its feedback, so a report deleted in Ember is never sent.
/// </summary>
public class LookoutOutbox
{
    /// <summary><see cref="Kind"/> of a row that creates the ticket of <see cref="FeedbackId"/>.</summary>
    public const string Ticket = "ticket";

    /// <summary><see cref="Kind"/> of a row that deletes every ticket of <see cref="UserRef"/>.</summary>
    public const string UserDelete = "user-delete";

    /// <summary>Identity; the send order.</summary>
    public long Id { get; set; }

    /// <summary><see cref="Ticket"/> or <see cref="UserDelete"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Tickets: the idempotency key (version 7 GUID, 32 hex digits), set at enqueue.</summary>
    public string? Key { get; set; }

    /// <summary>Tickets: the report to send.</summary>
    public int? FeedbackId { get; set; }

    public Feedback? Feedback { get; set; }

    /// <summary>Tickets: the sender's <see cref="LookoutUserRef.Ref"/> at enqueue, null when anonymous. User deletions: the ref to delete.</summary>
    public string? UserRef { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Tickets: when Lookout accepted it; the row is kept so the import skips reports the outbox owns. User deletions are deleted once sent.</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>When Lookout refused the row for good; parked rows are never sent again.</summary>
    public DateTime? ParkedAt { get; set; }

    /// <summary>The HTTP status Lookout refused the row with.</summary>
    public int? ParkedStatus { get; set; }
}
