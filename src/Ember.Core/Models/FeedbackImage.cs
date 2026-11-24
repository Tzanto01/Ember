namespace Ember.Core.Models;

/// <summary>
/// One picture attached to a <see cref="Models.Feedback"/>, stored with its metadata already stripped, until Lookout confirmed the
/// report and it is 30 days old, at most 12 months. Deleted with the feedback. Never exported.
/// </summary>
public class FeedbackImage
{
    /// <summary>Opaque version 7 GUID, so ids sort in upload order; the id <c>--list-feedback</c> prints and <c>--feedback-images</c> takes.</summary>
    public Guid Id { get; set; }

    public int FeedbackId { get; set; }

    /// <summary><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>, as sniffed from the bytes.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Length of <see cref="Data"/> in bytes, so it can be counted without loading the data.</summary>
    public int Size { get; set; }

    public byte[] Data { get; set; } = [];
}
