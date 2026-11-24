namespace Ember.Core.Models;

/// <summary>
/// One accepted <see cref="Feedback"/> as the 24-hour limits count it: when and how many picture bytes, nothing of its content or
/// sender. Its own table, so a report that is deleted early still counts; deleted once it is older than
/// <see cref="Services.FeedbackService.LimitWindow"/>.
/// </summary>
public class FeedbackIntake
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The stored bytes of the report's pictures together.</summary>
    public long ImageBytes { get; set; }
}
