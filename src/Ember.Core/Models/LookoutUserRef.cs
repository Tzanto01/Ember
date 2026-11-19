using System.Buffers.Text;
using System.Security.Cryptography;

namespace Ember.Core.Models;

/// <summary>
/// The opaque id under which a user's feedback is sent to Lookout, so Lookout never learns an Ember id, name or email. Random
/// (24 bytes, base64url), created on first need; a user's ref is their oldest one (by <see cref="CreatedAt"/>, then
/// <see cref="Ref"/>). Deleted with the user, after <c>DeleteUserDataAsync</c> queued a Lookout deletion for every ref; moved by an
/// account merge.
/// </summary>
public class LookoutUserRef
{
    /// <summary>32 base64url characters.</summary>
    public string Ref { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>A new random <see cref="Ref"/>.</summary>
    public static string NewRef() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
}
