namespace Ember.Bot.Discord.CustomIds;

/// <summary>The buttons under a picked habit: <c>habitaction:{action}:{habitId}[:{arg}]</c>.</summary>
internal static class HabitActionIds
{
    public const string CheckInDonePattern = "habitaction:checkin:done:*";
    public const string CheckInSkipPattern = "habitaction:checkin:skip:*";
    public const string SlipPattern = "habitaction:slip:*";
    public const string MadeItPattern = "habitaction:madeit:*";
    public const string DeleteConfirmPattern = "habitaction:delete:confirm:*";

    /// <summary>Cancels a delete; it has no arguments, so the id is its own handler pattern.</summary>
    public const string DeleteCancel = "habitaction:delete:cancel";

    public const string RenamePattern = "habitaction:edit:rename:*";
    public const string PausePattern = "habitaction:pause:*:*";
    public const string FrequencyPattern = "habitaction:freq:*:*";

    public static string CheckInDone(int habitId) => $"habitaction:checkin:done:{habitId}";

    public static string CheckInSkip(int habitId) => $"habitaction:checkin:skip:{habitId}";

    public static string Slip(int habitId) => $"habitaction:slip:{habitId}";

    public static string MadeIt(int habitId) => $"habitaction:madeit:{habitId}";

    public static string DeleteConfirm(int habitId) => $"habitaction:delete:confirm:{habitId}";

    public static string Rename(int habitId) => $"habitaction:edit:rename:{habitId}";

    /// <summary>Pauses for <paramref name="duration"/>: <c>today</c>, <c>3days</c>, <c>1week</c>, or <c>resume</c>.</summary>
    public static string Pause(int habitId, string duration) => $"habitaction:pause:{habitId}:{duration}";

    /// <summary>Makes the habit daily; the only frequency this family sets.</summary>
    public static string Daily(int habitId) => $"habitaction:freq:{habitId}:daily";
}
