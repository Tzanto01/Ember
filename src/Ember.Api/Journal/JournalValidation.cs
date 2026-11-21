using System.Buffers;
using System.Globalization;
using System.Text;
using Ember.Core.Models;
using Ember.Core.Services;

namespace Ember.Api.Journal;

/// <summary>The field checks of journal writes (day notes, habit notes, moods, the journal key), shared by the journal and import endpoints.</summary>
internal static class JournalValidation
{
    public const int MaxTextLength = 2000;
    public const int MaxLabelLength = 24;
    public const int MaxEmojiLength = 32;
    public const int MaxCipherTextLength = 16384;
    public const int MaxHabitNoteLength = 280;
    public const int MaxHabitNoteCipherTextLength = 4096;

    internal static readonly HashSet<string> BuiltInMoods = ["low", "meh", "okay", "good", "great"];

    /// <summary>
    /// The validated blob of an encrypted write, or null for a plaintext one. Adds an error when the form isn't allowed now:
    /// encrypted needs a key, plaintext (<paramref name="plaintextWrite"/>) is refused while a key exists and isn't turning off.
    /// </summary>
    internal static JournalCipher? WriteForm(JournalEndpoints.EncryptedDto? encrypted, bool plaintextGiven, bool plaintextWrite,
        JournalKey? key, Dictionary<string, string[]> errors, int maxCipherTextLength = MaxCipherTextLength)
    {
        if (encrypted is null)
        {
            if (plaintextWrite && key is { TurningOff: false }) errors["encrypted"] = ["The private journal is on. Send this encrypted."];
            return null;
        }

        if (plaintextGiven) errors["encrypted"] = ["Can't be combined with plaintext fields."];
        else if (key is null) errors["encrypted"] = ["The private journal is off. Send this as plaintext."];
        else
        {
            var count = errors.Count;
            if (encrypted.V != 1) errors["encrypted.v"] = ["Must be 1."];
            ExpectBase64(encrypted.Iv, 12, "encrypted.iv", errors);
            if (encrypted.Ct is null || encrypted.Ct.Length > maxCipherTextLength || Base64Length(encrypted.Ct) is not >= 16)
                errors["encrypted.ct"] = [$"Must be base64 of at least 16 bytes and at most {maxCipherTextLength} characters."];
            if (errors.Count == count) return new JournalCipher(1, encrypted.Iv!, encrypted.Ct!);
        }
        return null;
    }

    /// <summary>The id of a <c>custom:&lt;id&gt;</c> mood key in its canonical form.</summary>
    internal static bool TryParseCustomMood(string mood, out int id)
    {
        id = 0;
        return mood.StartsWith("custom:", StringComparison.Ordinal)
            && int.TryParse(mood.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && mood == $"custom:{id}";
    }

    /// <summary>The trimmed journal text, null when empty; adds an error when it is longer than <paramref name="maxLength"/>.</summary>
    internal static string? ValidText(string? text, Dictionary<string, string[]> errors, int maxLength = MaxTextLength)
    {
        var trimmed = text?.Trim() is { Length: > 0 } t ? t : null;
        if (trimmed?.Length > maxLength) errors["text"] = [$"Must be at most {maxLength} characters."];
        return trimmed;
    }

    internal static string? ValidLabel(string? label, Dictionary<string, string[]> errors)
    {
        var trimmed = label?.Trim();
        if (trimmed is { Length: >= 1 and <= MaxLabelLength }) return trimmed;
        errors["label"] = [$"Must be 1 to {MaxLabelLength} characters."];
        return null;
    }

    internal static string? ValidEmoji(string? emoji, Dictionary<string, string[]> errors)
    {
        if (emoji is null || IsEmoji(emoji)) return emoji;
        errors["emoji"] = ["Must be a single emoji."];
        return null;
    }

    private static bool IsEmoji(string value)
    {
        if (value.Length is 0 or > MaxEmojiLength || new StringInfo(value).LengthInTextElements != 1) return false;
        if (value[0] is (>= '0' and <= '9') or '#' or '*' && value.EndsWith('⃣')) return true;
        return Rune.DecodeFromUtf16(value, out var first, out _) == OperationStatus.Done
            && Rune.GetUnicodeCategory(first) is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol;
    }

    internal static JournalKey? ValidKey(JournalEndpoints.JournalKeyRequest request, Dictionary<string, string[]> errors)
    {
        var passphrase = request.Passphrase;
        var recovery = request.Recovery;
        ExpectBase64(passphrase?.Salt, 16, "passphrase.salt", errors);
        if (passphrase?.Iterations is not (>= 600_000 and <= 10_000_000))
            errors["passphrase.iterations"] = ["Must be between 600000 and 10000000."];
        ExpectBase64(passphrase?.Iv, 12, "passphrase.iv", errors);
        ExpectBase64(passphrase?.Wrapped, 48, "passphrase.wrapped", errors);
        ExpectBase64(recovery?.Salt, 16, "recovery.salt", errors);
        ExpectBase64(recovery?.Iv, 12, "recovery.iv", errors);
        ExpectBase64(recovery?.Wrapped, 48, "recovery.wrapped", errors);
        if (errors.Count > 0) return null;

        return new JournalKey
        {
            PassphraseSalt = passphrase!.Salt!,
            PassphraseIterations = passphrase.Iterations!.Value,
            PassphraseIv = passphrase.Iv!,
            PassphraseWrapped = passphrase.Wrapped!,
            RecoverySalt = recovery!.Salt!,
            RecoveryIv = recovery.Iv!,
            RecoveryWrapped = recovery.Wrapped!,
        };
    }

    private static void ExpectBase64(string? value, int bytes, string field, Dictionary<string, string[]> errors)
    {
        if (value?.Length != (bytes + 2) / 3 * 4 || Base64Length(value) != bytes) errors[field] = [$"Must be base64 of {bytes} bytes."];
    }

    /// <summary>The decoded length of strict (padded, no whitespace) base64, or null when it isn't.</summary>
    private static int? Base64Length(string value)
    {
        if (value.Length % 4 != 0 || value.Any(char.IsWhiteSpace)) return null;
        var bytes = new byte[value.Length / 4 * 3];
        return Convert.TryFromBase64String(value, bytes, out var written) ? written : null;
    }
}
