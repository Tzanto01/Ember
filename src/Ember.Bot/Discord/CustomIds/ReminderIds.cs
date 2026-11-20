namespace Ember.Bot.CustomIds;

/// <summary>
/// Setting a habit's reminder: <c>reminder:{action}:{habitId}</c>, the preset <c>reminder:preset:{habitId}:{HH:mm}</c> and the time form
/// <c>reminder:modal:{habitId}:{sourceMessageId}</c>. The string overloads pass on the id as the clicked button carried it.
/// </summary>
internal static class ReminderIds
{
    public const string SetPattern = "reminder:set:*";
    public const string TimePattern = "reminder:time:*";

    /// <summary>Three wildcards: the habit id, then the hour and minute, because the <c>HH:mm</c> time splits on its colon.</summary>
    public const string PresetPattern = "reminder:preset:*:*:*";

    public const string SkipPattern = "reminder:skip:*";
    public const string ClearPattern = "reminder:clear:*";
    public const string CancelPattern = "reminder:cancel:*";
    public const string ModalPattern = "reminder:modal:*:*";

    public static string Set(int habitId) => $"reminder:set:{habitId}";

    public static string Set(string habitId) => $"reminder:set:{habitId}";

    /// <summary>"I'm on UTC": the time form without asking for a timezone first.</summary>
    public static string Time(string habitId) => $"reminder:time:{habitId}";

    public static string Preset(int habitId, TimeOnly time) => $"reminder:preset:{habitId}:{time:HH\\:mm}";

    public static string Skip(int habitId) => $"reminder:skip:{habitId}";

    public static string Skip(string habitId) => $"reminder:skip:{habitId}";

    public static string Clear(int habitId) => $"reminder:clear:{habitId}";

    public static string Cancel(int habitId) => $"reminder:cancel:{habitId}";

    public static string Modal(string habitId, ulong sourceMessageId) => $"reminder:modal:{habitId}:{sourceMessageId}";
}
