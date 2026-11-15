namespace Ember.Core.Models;

/// <summary>Where a <see cref="Feedback"/> report stands; stored as lowercase text. Released and declined are closed.</summary>
public enum FeedbackStatus { Open, Planned, Implemented, Released, Declined }

/// <summary>
/// One piece of in-app feedback from the web app, the Android app or Discord. Linked to the sender's account when they were
/// signed in and didn't choose to send anonymously (deleted with it, moved by an account merge), else anonymous. Mailed to the
/// operator in the daily digest and deleted 12 months after it was submitted; its <see cref="Images"/> are deleted 30 days after
/// <see cref="ResolvedAt"/>. Not part of the export.
/// </summary>
public class Feedback
{
    public int Id { get; set; }

    public Guid? UserId { get; set; }

    /// <summary><c>web</c>, <c>android</c> or <c>discord</c>.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>The app version the client reported, if any.</summary>
    public string? Version { get; set; }

    /// <summary>One line, up to 100 characters; for feedback stored before titles existed, the first line of <see cref="Text"/>.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The description.</summary>
    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>When a digest email carried it; null while pending.</summary>
    public DateTime? DigestedAt { get; set; }

    /// <summary>Set by the operator with <c>--feedback-status</c>.</summary>
    public FeedbackStatus Status { get; set; }

    /// <summary>When <see cref="Status"/> became released or declined; null while it is open, planned or implemented.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Attached pictures (signed-in and Discord senders only), in upload order by <see cref="FeedbackImage.Id"/>.</summary>
    public List<FeedbackImage> Images { get; set; } = [];
}
