namespace Ember.Bot.CustomIds;

/// <summary>
/// The buttons of a DM reminder: <c>checkin:{done|skip|snooze}:{habitId}:{sentUnixSeconds}</c>, with the reminder's send time so a
/// tap logs the day the reminder was for. A routine reminder has one Done button per pending habit.
/// </summary>
internal static class CheckInIds
{
    public const string DonePattern = "checkin:done:*:*";
    public const string SkipPattern = "checkin:skip:*:*";
    public const string SnoozePattern = "checkin:snooze:*:*";

    /// <summary>The start of every Done id.</summary>
    public const string DonePrefix = "checkin:done:";

    public static string Done(int habitId, long sentUnixSeconds) => $"checkin:done:{habitId}:{sentUnixSeconds}";

    public static string Skip(int habitId, long sentUnixSeconds) => $"checkin:skip:{habitId}:{sentUnixSeconds}";

    public static string Snooze(int habitId, long sentUnixSeconds) => $"checkin:snooze:{habitId}:{sentUnixSeconds}";

    /// <summary>The habit id of a Done id (0 when it is not a number), or null when <paramref name="customId"/> is no Done id.</summary>
    public static int? DoneHabitId(string? customId)
        => customId?.Split(':') is ["checkin", "done", var habitId, _] ? int.TryParse(habitId, out var id) ? id : 0 : null;
}
