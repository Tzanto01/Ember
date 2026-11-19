namespace Ember.Core.Models;

/// <summary>
/// A short note on one habit for one of the user's local days. Owned through <see cref="Habit"/>. Stored either as plaintext
/// (<see cref="Text"/>) or, with the private journal on, as an opaque client-side ciphertext (<see cref="CipherVersion"/>,
/// <see cref="CipherIv"/>, <see cref="CipherText"/>).
/// </summary>
public class HabitNote
{
    public int Id { get; set; }
    public int HabitId { get; set; }

    /// <summary>The user's local calendar date (in <see cref="User.Timezone"/>).</summary>
    public DateOnly Date { get; set; }

    /// <summary>Free text, up to 280 characters. Null when encrypted.</summary>
    public string? Text { get; set; }

    /// <summary>Encryption format version; null for a plaintext note.</summary>
    public int? CipherVersion { get; set; }

    /// <summary>AES-GCM IV (base64).</summary>
    public string? CipherIv { get; set; }

    /// <summary>AES-GCM ciphertext (base64).</summary>
    public string? CipherText { get; set; }

    public Habit Habit { get; set; } = null!;
}
