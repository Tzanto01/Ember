namespace Ember.Core.Models;

/// <summary>
/// One web or app sign-in. The API carries <see cref="Id"/> in the cookie and bearer tokens and accepts them only while this
/// row exists and hasn't expired; deleting it signs that device out.
/// </summary>
public class AuthSession
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Slides forward while the session is in use.</summary>
    public DateTimeOffset ExpiresUtc { get; set; }

    public DateTimeOffset? LastSeenUtc { get; set; }

    /// <summary>The client's platform (one of <see cref="Platforms"/>) from its last request that named it; null when none did.</summary>
    public string? Platform { get; set; }

    /// <summary>The client's app version (up to 32 characters) from its last request that named it; null when none did.</summary>
    public string? AppVersion { get; set; }

    /// <summary>The values <see cref="Platform"/> may hold.</summary>
    public static readonly IReadOnlySet<string> Platforms = new HashSet<string> { "web", "android", "discord" };

    /// <summary>SHA-256 of the session's widget key, or null; one per session, and deleting the session revokes it.</summary>
    public byte[]? WidgetKeyHash { get; set; }
}
