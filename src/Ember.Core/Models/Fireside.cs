namespace Ember.Core.Models;

/// <summary>
/// The one active Fireside friend code of a user. Only its SHA-256 (<see cref="CodeHash"/>) is used for lookup; it is single use
/// and replaced by a new code. Not part of the export.
/// </summary>
public class FiresideCode
{
    public Guid UserId { get; set; }

    /// <summary>SHA-256 (hex) of the normalized code (8 characters of <see cref="Services.FiresideService.CodeAlphabet"/>).</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>
    /// The code encrypted with the API's Data Protection keys, so the API can show it again; null for a code made by the bot,
    /// which shows it only once.
    /// </summary>
    public string? CodeProtected { get; set; }

    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>
/// One direction of a Fireside friendship, owned by <see cref="OwnerId"/>; always created and deleted together with the row of the
/// other direction. Not part of the export.
/// </summary>
public class FiresideLink
{
    public int Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid FriendId { get; set; }

    /// <summary>Whether the owner hides the friend's warmth.</summary>
    public bool Muted { get; set; }

    /// <summary>Whether the friend sees if the owner checked in today.</summary>
    public bool ShareFlame { get; set; }

    /// <summary>The friend's Fireside name the owner last acknowledged; a different current name shows a rename notice.</summary>
    public string KnownName { get; set; } = string.Empty;

    /// <summary>Whether the friend joined with the owner's code and the owner hasn't acknowledged it yet.</summary>
    public bool JoinedViaMyCode { get; set; }

    /// <summary>Whether the bot still has to notify the owner that the friend joined (set only when the owner's notifications are on).</summary>
    public bool JoinNotifyPending { get; set; }

    public DateTime CreatedAt { get; set; }

    public User Friend { get; set; } = null!;
}

/// <summary>One of the five fixed Fireside messages from one user to a friend. Deleted after 30 days. Not part of the export.</summary>
public class Warmth
{
    public int Id { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }

    /// <summary>One of <see cref="Services.FiresideService.Messages"/>.</summary>
    public string Message { get; set; } = string.Empty;

    public DateTime SentAtUtc { get; set; }

    /// <summary>When the receiver dismissed it; set right away when the receiver has muted the sender.</summary>
    public DateTime? DismissedAtUtc { get; set; }

    /// <summary>Whether the bot still has to notify the receiver (set only when the receiver's notifications are on).</summary>
    public bool NotifyPending { get; set; }
}
