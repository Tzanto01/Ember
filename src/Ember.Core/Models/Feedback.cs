namespace Ember.Core.Models;

/// <summary>
/// One piece of in-app feedback from the web app, the Android app or Discord. Linked to the sender's account when they were
/// signed in and didn't choose to send anonymously (deleted with it, moved by an account merge), else anonymous. Lookout gets it
/// through the <see cref="LookoutOutbox"/> and owns its status, comments and board; Ember keeps what the user sent only for the web
/// export and the account deletion, and deletes it 12 months after it was submitted at the latest.
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

    /// <summary>Attached pictures (signed-in and Discord senders only), in upload order by <see cref="FeedbackImage.Id"/>.</summary>
    public List<FeedbackImage> Images { get; set; } = [];
}
